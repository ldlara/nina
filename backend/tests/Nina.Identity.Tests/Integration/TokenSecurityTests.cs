using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nina.Identity.Tests.Infrastructure;
using Nina.SharedKernel.Security;

namespace Nina.Identity.Tests.Integration;

/// <summary>AZ-17/AZ-18 e perfil JWT do contrato 1.0.1 (AD-31): ES256 fixo, typ, kid, iss/aud, claims e tolerância de relógio.</summary>
public sealed class TokenSecurityTests(PostgresFixture postgres) : IntegrationTestBase(postgres)
{
    private string Forge(
        SecurityKey? key = null,
        string alg = SecurityAlgorithms.EcdsaSha256,
        string audience = "nina-api",
        string issuer = "https://api.nina.app",
        Guid? sessionId = null,
        Guid? userId = null,
        TimeSpan? lifetime = null,
        string? type = "at+jwt",
        string? kid = "test-key-1",
        bool addJti = true)
    {
        var now = Factory.Time.GetUtcNow().UtcDateTime;
        var claims = new List<Claim>
        {
            new("sub", (userId ?? Guid.NewGuid()).ToString()),
            new("sid", (sessionId ?? Guid.NewGuid()).ToString()),
            new("role", "admin"),
        };
        if (addJti)
        {
            claims.Add(new Claim("jti", Guid.NewGuid().ToString("N")));
        }

        // Sempre uma cópia do wrapper: alterar o KeyId da chave real quebraria o servidor (mesmo processo).
        var signing = key ?? new ECDsaSecurityKey(RealKey().ECDsa) { KeyId = kid };

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            TokenType = type,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + (lifetime ?? TimeSpan.FromMinutes(10)),
            SigningCredentials = new SigningCredentials(signing, alg),
        };
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    private ECDsaSecurityKey RealKey() => Factory.Services.GetRequiredService<JwtKeyring>().SigningKey;

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task AssertRejected(string token, string? expectedCode = "INVALID_TOKEN")
    {
        var response = await Api.GetAsync("/v1/me", token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        if (expectedCode is not null)
        {
            Assert.Equal(expectedCode, response.Code);
        }
    }

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
    public async Task Issued_access_token_follows_the_contract_profile()
    {
        var session = await Api.RegisterAndVerifyAsync();
        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(session.AccessToken);

        Assert.Equal("ES256", jwt.Alg);
        Assert.Equal("at+jwt", jwt.Typ);
        Assert.Equal("test-key-1", jwt.Kid);
        Assert.Equal("https://api.nina.app", jwt.Issuer);
        Assert.Contains("nina-api", jwt.Audiences);
        Assert.Equal(session.UserId.ToString(), jwt.Subject);
        Assert.Equal(session.SessionId.ToString(), jwt.GetPayloadValue<string>("sid"));
        Assert.False(string.IsNullOrEmpty(jwt.Id));
        Assert.True(jwt.ValidTo - jwt.IssuedAt <= TimeSpan.FromSeconds(900));
        Assert.True(jwt.ValidFrom <= jwt.IssuedAt);
        Assert.False(jwt.TryGetPayloadValue<string>("role", out _));
    }

    [Fact]
    public async Task Token_with_alg_none_is_rejected()
    {
        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var token = $"{B64("""{"alg":"none","typ":"at+jwt","kid":"test-key-1"}""")}.{B64($$"""{"sub":"{{Guid.NewGuid()}}","sid":"{{Guid.NewGuid()}}","iss":"https://api.nina.app","aud":"nina-api","jti":"x","exp":{{exp}}}""")}.";

        await AssertRejected(token);
    }

    [Fact]
    public async Task Hs256_token_signed_with_the_public_key_bytes_is_rejected()
    {
        // Ataque clássico de confusão de algoritmo: usar a chave pública como segredo HMAC.
        var publicKey = Factory.Services.GetRequiredService<JwtKeyring>().SigningKey.ECDsa.ExportSubjectPublicKeyInfo();
        var hmac = new SymmetricSecurityKey(publicKey) { KeyId = "test-key-1" };

        await AssertRejected(Forge(hmac, SecurityAlgorithms.HmacSha256));
    }

    [Fact]
    public async Task Rs256_token_is_rejected()
    {
        using var rsa = RSA.Create(2048);
        await AssertRejected(Forge(new RsaSecurityKey(rsa) { KeyId = "test-key-1" }, SecurityAlgorithms.RsaSha256));
    }

    [Fact]
    public async Task Es256_token_signed_with_another_key_is_rejected()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await AssertRejected(Forge(new ECDsaSecurityKey(other) { KeyId = "test-key-1" }));
    }

    [Theory]
    [InlineData("nina-app", "https://api.nina.app")]
    [InlineData("nina-reauth", "https://api.nina.app")]
    [InlineData("nina-api", "https://evil.example")]
    public async Task Token_with_wrong_audience_or_issuer_is_rejected(string audience, string issuer)
    {
        var session = await Api.RegisterAndVerifyAsync();

        await AssertRejected(Forge(audience: audience, issuer: issuer, sessionId: session.SessionId, userId: session.UserId));
    }

    [Theory]
    [InlineData("JWT")]
    [InlineData("reauth+jwt")]
    [InlineData(null)]
    public async Task Token_without_the_at_jwt_type_is_rejected(string? type)
    {
        var session = await Api.RegisterAndVerifyAsync();

        await AssertRejected(Forge(sessionId: session.SessionId, userId: session.UserId, type: type));
    }

    [Fact]
    public async Task Token_without_kid_or_with_an_unknown_kid_is_rejected()
    {
        var session = await Api.RegisterAndVerifyAsync();
        var noKid = new ECDsaSecurityKey(RealKey().ECDsa);

        await AssertRejected(Forge(noKid, kid: null, sessionId: session.SessionId, userId: session.UserId));
        await AssertRejected(Forge(kid: "other-kid", sessionId: session.SessionId, userId: session.UserId));
    }

    [Fact]
    public async Task Token_without_jti_or_living_longer_than_fifteen_minutes_is_rejected()
    {
        var session = await Api.RegisterAndVerifyAsync();

        await AssertRejected(Forge(sessionId: session.SessionId, userId: session.UserId, addJti: false));
        await AssertRejected(Forge(sessionId: session.SessionId, userId: session.UserId, lifetime: TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public async Task Valid_signature_with_forged_role_claims_is_accepted_only_for_a_live_session_and_the_role_is_ignored()
    {
        var session = await Api.RegisterAndVerifyAsync();
        var forgedForGhostSession = Forge();
        var forgedForRealSession = Forge(sessionId: session.SessionId, userId: session.UserId);

        await AssertRejected(forgedForGhostSession, "SESSION_REVOKED");
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("/v1/me", forgedForRealSession)).Status);
    }

    [Fact]
    public async Task Reauth_token_cannot_be_used_as_an_access_token()
    {
        var session = await Api.RegisterAndVerifyAsync();
        var reauth = await Api.ReauthAsync(session, ReauthScopes.IdentityLink);

        await AssertRejected(reauth);
    }

    [Fact]
    public async Task Expired_access_token_returns_TOKEN_EXPIRED_after_the_clock_skew()
    {
        var session = await Api.RegisterAndVerifyAsync();
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("/v1/me", session.AccessToken)).Status);

        Factory.Time.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("/v1/me", session.AccessToken)).Status); // dentro da tolerância

        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        var response = await Api.GetAsync("/v1/me", session.AccessToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        Assert.Equal("TOKEN_EXPIRED", response.Code);
    }

    [Fact]
    public async Task Rotated_out_key_keeps_verifying_tokens_only_while_it_is_listed()
    {
        using var oldKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var oldPem = oldKey.ExportPkcs8PrivateKeyPem();
        var oldPublic = oldKey.ExportSubjectPublicKeyInfoPem();

        // Fábrica A assina com a chave antiga; fábrica B (mesmo banco) assina com a nova e ainda lista a antiga para verificação.
        await using var oldFactory = new ApiFactory(Database.AppConnectionString, s =>
        {
            s["Jwt:SigningKeyPem"] = oldPem;
            s["Jwt:KeyId"] = "key-old";
        });
        var oldApi = new ApiClient(oldFactory.CreateClient(), oldFactory);
        var session = await oldApi.RegisterAndVerifyAsync();

        await using var rotated = new ApiFactory(Database.AppConnectionString, s =>
        {
            s["Jwt:KeyId"] = "key-new";
            s["Jwt:VerificationKeys:key-old"] = oldPublic;
        });
        await using var forgotten = new ApiFactory(Database.AppConnectionString, s => s["Jwt:KeyId"] = "key-newer");

        var ok = await new ApiClient(rotated.CreateClient(), rotated).GetAsync("/v1/me", session.AccessToken);
        var rejected = await new ApiClient(forgotten.CreateClient(), forgotten).GetAsync("/v1/me", session.AccessToken);

        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.Status);
    }
}
