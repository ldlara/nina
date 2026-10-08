using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nina.Identity.Services;
using Nina.Identity.Tests.Infrastructure;
using Nina.SharedKernel.Security;

namespace Nina.Identity.Tests.Unit;

public sealed class TokenServiceTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly JwtKeyring _keyring;
    private readonly IOptions<JwtOptions> _jwt = Options.Create(new JwtOptions { SigningKeyPem = TestConstants.NewSigningKeyPem(), KeyId = "k1" });

    public TokenServiceTests()
    {
        _keyring = new JwtKeyring(_jwt, new StubEnvironment("Production"));
    }

    public void Dispose() => _keyring.Dispose();

    private TokenService Create() => new(_keyring, _jwt, _time);

    [Fact]
    public async Task Reauth_token_is_bound_to_user_session_and_scope_and_expires_in_five_minutes()
    {
        var service = Create();
        var user = Guid.NewGuid();
        var session = Guid.NewGuid();
        var (token, expiresIn, jti) = service.IssueReauthToken(user, session, [ReauthScopes.AccountPasswordChange, ReauthScopes.IdentityLink]);

        Assert.Equal(300, expiresIn);
        var claims = await service.ParseReauthAsync(token, user, session);
        Assert.Equal(jti, claims!.Jti);
        Assert.Equal([ReauthScopes.AccountPasswordChange, ReauthScopes.IdentityLink], claims.Scopes);
        Assert.Null(await service.ParseReauthAsync(token, Guid.NewGuid(), session));
        Assert.Null(await service.ParseReauthAsync(token, user, Guid.NewGuid()));
        Assert.Null(await service.ParseReauthAsync(null, user, session));
        Assert.Null(await service.ParseReauthAsync("not.a.jwt.at.all.nope", user, session));
        _time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(45));
        Assert.Null(await service.ParseReauthAsync(token, user, session));
    }

    [Fact]
    public async Task Reauth_token_without_scope_has_an_empty_scope_list_meaning_one_use_for_any_operation()
    {
        var service = Create();
        var user = Guid.NewGuid();
        var session = Guid.NewGuid();
        var (token, _, _) = service.IssueReauthToken(user, session, null);

        var claims = await service.ParseReauthAsync(token, user, session);

        Assert.Empty(claims!.Scopes);
    }

    [Fact]
    public async Task An_access_token_is_not_accepted_as_a_reauth_token()
    {
        var service = Create();
        var user = Guid.NewGuid();
        var session = Guid.NewGuid();
        var (access, _) = service.IssueAccessToken(user, session, Guid.NewGuid());

        Assert.Null(await service.ParseReauthAsync(access, user, session));
    }

    [Fact]
    public void Access_token_never_lives_longer_than_fifteen_minutes_even_if_misconfigured()
    {
        var jwt = Options.Create(new JwtOptions { SigningKeyPem = TestConstants.NewSigningKeyPem(), AccessTokenMinutes = 600 });
        using var keyring = new JwtKeyring(jwt, new StubEnvironment("Production"));
        var service = new TokenService(keyring, jwt, _time);

        var (_, expiresIn) = service.IssueAccessToken(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(900, expiresIn);
    }

    [Fact]
    public void Missing_or_non_p256_signing_key_fails_closed_outside_development()
    {
        Assert.Throws<InvalidOperationException>(() => new JwtKeyring(Options.Create(new JwtOptions()), new StubEnvironment("Production")));
        using var p384 = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP384);
        var wrong = Options.Create(new JwtOptions { SigningKeyPem = p384.ExportPkcs8PrivateKeyPem() });
        Assert.Throws<InvalidOperationException>(() => new JwtKeyring(wrong, new StubEnvironment("Production")));
        using var dev = new JwtKeyring(Options.Create(new JwtOptions()), new StubEnvironment("Development"));
        Assert.False(string.IsNullOrEmpty(dev.SigningKey.KeyId));
    }

    [Fact]
    public void Master_key_is_required_outside_development_and_derived_keys_are_purpose_specific()
    {
        Assert.Throws<InvalidOperationException>(() => new SecretKeys(Options.Create(new SecurityOptions()), new StubEnvironment("Production")));
        Assert.Throws<InvalidOperationException>(() =>
            new SecretKeys(Options.Create(new SecurityOptions { MasterKey = Convert.ToBase64String(new byte[16]) }), new StubEnvironment("Production")));
        var keys = new SecretKeys(Options.Create(new SecurityOptions { MasterKey = TestConstants.MasterKey }), new StubEnvironment("Production"));

        Assert.Equal(keys.Derive("a"), keys.Derive("a"));
        Assert.NotEqual(keys.Derive("a"), keys.Derive("b"));
        Assert.NotEqual(keys.Hmac("a", "x"), keys.Hmac("a", "y"));
    }

    private sealed class StubEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = "/";

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
