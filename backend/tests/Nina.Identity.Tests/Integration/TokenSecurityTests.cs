using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nina.Identity.Tests.Infrastructure;
using Nina.SharedKernel.Security;

namespace Nina.Identity.Tests.Integration;

/// <summary>AZ-17/AZ-18: JWT com alg=none, assinatura/audience/issuer errados, expirado e sessão inexistente.</summary>
public sealed class TokenSecurityTests(PostgresFixture postgres) : IntegrationTestBase(postgres)
{
    private string Forge(SecurityKey key, string alg, string audience = "nina-app", string issuer = "https://api.nina.app", Guid? sessionId = null, Guid? userId = null, DateTime? expires = null)
    {
        var now = Factory.Time.GetUtcNow().UtcDateTime;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", (userId ?? Guid.NewGuid()).ToString()),
                new Claim("sid", (sessionId ?? Guid.NewGuid()).ToString()),
                new Claim("role", "admin"),
            ]),
            NotBefore = now,
            Expires = expires ?? now.AddMinutes(10),
            SigningCredentials = new SigningCredentials(key, alg),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private SecurityKey RealKey() => Factory.Services.GetRequiredService<SecretKeys>().SigningKey;

    [Fact]
    public async Task Request_without_token_gets_401_with_www_authenticate()
    {
        var response = await Api.GetAsync("/v1/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        Assert.True(response.IsProblem);
        Assert.Equal("AUTHENTICATION_REQUIRED", response.Code);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Token_with_alg_none_is_rejected()
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var token = $"{B64("""{"alg":"none","typ":"JWT"}""")}.{B64($$"""{"sub":"{{Guid.NewGuid()}}","sid":"{{Guid.NewGuid()}}","iss":"https://api.nina.app","aud":"nina-app","exp":{{exp}}}""")}.";

        var response = await Api.GetAsync("/v1/me", token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        Assert.Equal("INVALID_TOKEN", response.Code);
    }

    [Fact]
    public async Task Token_signed_with_another_key_or_forged_claims_is_rejected()
    {
        var wrongKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("another-key-another-key-another-key!"));
        var forged = Forge(wrongKey, SecurityAlgorithms.HmacSha256);

        var response = await Api.GetAsync("/v1/me", forged);

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        Assert.Equal("INVALID_TOKEN", response.Code);
    }

    [Theory]
    [InlineData("wrong-audience", "https://api.nina.app")]
    [InlineData("nina-app", "https://evil.example")]
    [InlineData("nina-reauth", "https://api.nina.app")]
    public async Task Token_with_wrong_audience_or_issuer_is_rejected(string audience, string issuer)
    {
        var session = await Api.RegisterAndVerifyAsync();
        var token = Forge(RealKey(), SecurityAlgorithms.HmacSha256, audience, issuer, session.SessionId, session.UserId);

        var response = await Api.GetAsync("/v1/me", token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
    }

    [Fact]
    public async Task Role_claims_are_ignored_and_a_valid_signature_for_a_nonexistent_session_is_rejected()
    {
        var token = Forge(RealKey(), SecurityAlgorithms.HmacSha256);

        var response = await Api.GetAsync("/v1/me", token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        Assert.Equal("SESSION_REVOKED", response.Code);
    }

    [Fact]
    public async Task Reauth_token_cannot_be_used_as_an_access_token()
    {
        var session = await Api.RegisterAndVerifyAsync();
        var reauth = await Api.PostAsync("/v1/auth/reauthenticate", new System.Text.Json.Nodes.JsonObject { ["password"] = TestConstants.GoodPassword }, session.AccessToken);

        var response = await Api.GetAsync("/v1/me", reauth.Json!["reauth_token"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
    }

    [Fact]
    public async Task Expired_access_token_returns_TOKEN_EXPIRED()
    {
        var session = await Api.RegisterAndVerifyAsync();
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("/v1/me", session.AccessToken)).Status);

        Factory.Time.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(30));
        var response = await Api.GetAsync("/v1/me", session.AccessToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        Assert.Equal("TOKEN_EXPIRED", response.Code);
    }

    [Fact]
    public async Task Access_token_lifetime_is_at_most_fifteen_minutes()
    {
        var session = await Api.RegisterAndVerifyAsync();
        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(session.AccessToken);

        Assert.Equal("HS256", jwt.Alg);
        Assert.True(jwt.ValidTo - jwt.ValidFrom <= TimeSpan.FromMinutes(15));
        Assert.Equal(session.UserId.ToString(), jwt.Subject);
    }
}
