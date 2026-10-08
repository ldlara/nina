using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nina.Identity.Services;
using Nina.Identity.Tests.Infrastructure;
using Nina.SharedKernel.Security;

namespace Nina.Identity.Tests.Unit;

public sealed class TokenServiceTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    private TokenService Create(string? key = TestConstants.SigningKey)
    {
        var jwt = Options.Create(new JwtOptions { SigningKey = key });
        return new TokenService(new SecretKeys(jwt, new StubEnvironment("Production")), jwt, _time);
    }

    [Fact]
    public async Task Reauth_token_is_bound_to_user_and_session_and_expires_in_five_minutes()
    {
        var service = Create();
        var user = Guid.NewGuid();
        var session = Guid.NewGuid();
        var (token, expiresIn) = service.IssueReauthToken(user, session);

        Assert.Equal(300, expiresIn);
        Assert.True(await service.ValidateReauthAsync(token, user, session));
        Assert.False(await service.ValidateReauthAsync(token, Guid.NewGuid(), session));
        Assert.False(await service.ValidateReauthAsync(token, user, Guid.NewGuid()));
        Assert.False(await service.ValidateReauthAsync(null, user, session));
        Assert.False(await service.ValidateReauthAsync("not.a.jwt", user, session));
        _time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(20));
        Assert.False(await service.ValidateReauthAsync(token, user, session));
    }

    [Fact]
    public async Task An_access_token_is_not_accepted_as_a_reauth_token()
    {
        var service = Create();
        var user = Guid.NewGuid();
        var session = Guid.NewGuid();
        var (access, _) = service.IssueAccessToken(user, session, Guid.NewGuid());

        Assert.False(await service.ValidateReauthAsync(access, user, session));
    }

    [Fact]
    public void Access_token_never_lives_longer_than_fifteen_minutes_even_if_misconfigured()
    {
        var jwt = Options.Create(new JwtOptions { SigningKey = TestConstants.SigningKey, AccessTokenMinutes = 600 });
        var service = new TokenService(new SecretKeys(jwt, new StubEnvironment("Production")), jwt, _time);

        var (_, expiresIn) = service.IssueAccessToken(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(900, expiresIn);
    }

    [Fact]
    public void Missing_or_short_signing_key_fails_closed_outside_development()
    {
        Assert.Throws<InvalidOperationException>(() => Create(null));
        Assert.Throws<InvalidOperationException>(() => Create(Convert.ToBase64String(new byte[16])));
        var dev = Options.Create(new JwtOptions());
        Assert.NotNull(new SecretKeys(dev, new StubEnvironment("Development")).SigningKey);
    }

    [Fact]
    public void Derived_keys_are_purpose_specific_and_stable()
    {
        var keys = new SecretKeys(Options.Create(new JwtOptions { SigningKey = TestConstants.SigningKey }), new StubEnvironment("Production"));

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
