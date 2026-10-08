using System.Text;
using Microsoft.Extensions.Options;
using Nina.Identity.Crypto;

namespace Nina.Identity.Tests.Unit;

public sealed class PasswordHasherTests : IDisposable
{
    private readonly PasswordHasher _hasher = Create(1024, 2, 1);

    private static PasswordHasher Create(int memory, int iterations, int parallelism) =>
        new(Options.Create(new IdentityOptions { Argon2MemoryKiB = memory, Argon2Iterations = iterations, Argon2Parallelism = parallelism }));

    public void Dispose() => _hasher.Dispose();

    [Fact]
    public async Task Hash_is_argon2id_phc_with_random_salt()
    {
        var a = await _hasher.HashAsync("a-long-secret-password");
        var b = await _hasher.HashAsync("a-long-secret-password");

        Assert.StartsWith("$argon2id$v=19$m=1024,t=2,p=1$", a, StringComparison.Ordinal);
        Assert.NotEqual(a, b);
        Assert.DoesNotContain("a-long-secret-password", a, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_accepts_the_right_password_and_rejects_others()
    {
        var hash = await _hasher.HashAsync("a-long-secret-password");

        Assert.True((await _hasher.VerifyAsync("a-long-secret-password", hash)).Valid);
        Assert.False((await _hasher.VerifyAsync("a-long-secret-passworD", hash)).Valid);
        Assert.False((await _hasher.VerifyAsync(string.Empty, hash)).Valid);
    }

    [Fact]
    public async Task Verify_handles_unicode_and_long_passwords()
    {
        var unicode = "senha-ção-🔒-" + new string('x', 100);
        var hash = await _hasher.HashAsync(unicode);

        Assert.True((await _hasher.VerifyAsync(unicode, hash)).Valid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("$argon2id$v=19$m=abc,t=2,p=1$AAAA$AAAA")]
    [InlineData("$argon2i$v=19$m=1024,t=2,p=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=99999999,t=2,p=1$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Verify_returns_false_for_malformed_or_abusive_hashes(string phc)
    {
        Assert.False((await _hasher.VerifyAsync("anything-goes-here", phc)).Valid);
    }

    [Fact]
    public async Task Verify_flags_hashes_made_with_weaker_parameters_for_rehash()
    {
        using var weak = Create(512, 1, 1);
        var hash = await weak.HashAsync("a-long-secret-password");

        var check = await _hasher.VerifyAsync("a-long-secret-password", hash);

        Assert.True(check.Valid);
        Assert.True(check.NeedsRehash);
        Assert.False((await _hasher.VerifyAsync("a-long-secret-password", await _hasher.HashAsync("a-long-secret-password"))).NeedsRehash);
    }

    [Fact]
    public async Task Burn_runs_without_error()
    {
        await _hasher.BurnAsync("whatever-password");
    }
}

public sealed class OpaqueTokensTests
{
    [Fact]
    public void Refresh_token_round_trips_ids_and_is_unique()
    {
        var user = Guid.NewGuid();
        var session = Guid.NewGuid();
        var a = OpaqueTokens.NewRefreshToken(user, session);
        var b = OpaqueTokens.NewRefreshToken(user, session);

        Assert.NotEqual(a, b);
        Assert.True(OpaqueTokens.TryParseRefreshToken(a, out var u, out var s));
        Assert.Equal((user, session), (u, s));
        Assert.Equal(32, OpaqueTokens.HashRefreshToken(a).Length);
        Assert.NotEqual(OpaqueTokens.HashRefreshToken(a), OpaqueTokens.HashRefreshToken(b));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rt_")]
    [InlineData("xx_AAAA")]
    [InlineData("rt_!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    public void Refresh_token_parsing_rejects_garbage(string? token)
    {
        Assert.False(OpaqueTokens.TryParseRefreshToken(token, out _, out _));
    }

    [Fact]
    public void Verification_codes_have_six_digits_and_do_not_repeat_constantly()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => OpaqueTokens.NewVerificationCode()).ToList();

        Assert.All(codes, c => Assert.Matches("^[0-9]{6}$", c));
        Assert.True(codes.Distinct().Count() > 150);
    }

    [Fact]
    public void Url_tokens_carry_256_bits()
    {
        var token = OpaqueTokens.NewUrlToken();

        Assert.Equal(32, OpaqueTokens.FromBase64Url(token).Length);
        Assert.DoesNotContain('=', token);
        Assert.NotEqual(token, OpaqueTokens.NewUrlToken());
        _ = Encoding.UTF8;
    }
}

public sealed class PasswordPolicyTests
{
    private static PasswordPolicy Policy(int min = 10) =>
        new(Options.Create(new IdentityOptions { PasswordMinLength = min }), new LocalCommonPasswordChecker());

    [Theory]
    [InlineData("short", "PASSWORD_TOO_SHORT")]
    [InlineData("password123", "PASSWORD_TOO_COMMON")]
    [InlineData("zzzzzzzzzzzz", "PASSWORD_TOO_COMMON")]
    public async Task Rejects_weak_passwords(string password, string code)
    {
        var error = await Policy().ValidateAsync("password", password, null, CancellationToken.None);

        Assert.Equal(code, error!.Code);
        Assert.Equal("password", error.Field);
    }

    [Fact]
    public async Task Rejects_passwords_derived_from_the_email_and_too_long_ones()
    {
        var sameAsEmail = await Policy().ValidateAsync("password", "someone.long@example.org", "someone.long@example.org", CancellationToken.None);
        var tooLong = await Policy().ValidateAsync("password", new string('a', 1) + new string('b', 200), null, CancellationToken.None);

        Assert.Equal("PASSWORD_TOO_COMMON", sameAsEmail!.Code);
        Assert.Equal("PASSWORD_TOO_LONG", tooLong!.Code);
    }

    [Theory]
    [InlineData("correct-horse-battery-staple")]
    [InlineData("alllowercase but long")]
    [InlineData("1234567890abc")]
    public async Task Accepts_long_passwords_without_composition_rules(string password)
    {
        Assert.Null(await Policy().ValidateAsync("password", password, "a@example.org", CancellationToken.None));
    }
}
