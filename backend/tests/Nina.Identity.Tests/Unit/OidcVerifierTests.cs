using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nina.Identity.External;

namespace Nina.Identity.Tests.Unit;

/// <summary>Verificador real de id_token (ADR-0007) com chaves RSA geradas localmente; sem chamadas de rede.</summary>
public sealed class OidcVerifierTests : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RSA _other = RSA.Create(2048);
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly OidcIdentityTokenVerifier _verifier;
    private readonly StubJwks _jwks;

    public OidcVerifierTests()
    {
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_rsa.ExportParameters(false)) { KeyId = "k1" });
        var set = new JsonWebKeySet();
        set.Keys.Add(jwk);
        _jwks = new StubJwks(set);
        var options = Options.Create(new IdentityOptions { GoogleClientIds = ["g-client"], AppleClientIds = ["a-client"] });
        _verifier = new OidcIdentityTokenVerifier(options, _jwks, _time);
    }

    public void Dispose()
    {
        _rsa.Dispose();
        _other.Dispose();
    }

    private string Token(
        string issuer = "https://accounts.google.com", string audience = "g-client", string? nonce = "raw-nonce", string subject = "sub-1",
        object? emailVerified = null, TimeSpan? lifetime = null, RSA? signWith = null, string alg = SecurityAlgorithms.RsaSha256, string kid = "k1")
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject,
            ["email"] = "ana@example.org",
            ["email_verified"] = emailVerified ?? true,
        };
        if (nonce is not null)
        {
            claims["nonce"] = nonce;
        }

        var life = lifetime ?? TimeSpan.FromMinutes(10);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            NotBefore = now.AddMinutes(-1),
            IssuedAt = now.AddMinutes(-1),
            Expires = now + life,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(signWith ?? _rsa) { KeyId = kid }, alg),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    [Fact]
    public async Task Valid_google_token_yields_the_identity()
    {
        var identity = await _verifier.VerifyAsync(Providers.Google, Token(), "raw-nonce", CancellationToken.None);

        Assert.Equal(new VerifiedIdentity("GOOGLE", "sub-1", "ana@example.org", true), identity);
    }

    [Fact]
    public async Task Accepts_apple_style_hashed_nonce_and_string_email_verified()
    {
        var hashed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("raw-nonce"))).ToLowerInvariant();
        var token = Token("https://appleid.apple.com", "a-client", hashed, emailVerified: "true");

        var identity = await _verifier.VerifyAsync(Providers.Apple, token, "raw-nonce", CancellationToken.None);

        Assert.True(identity.EmailVerified);
        Assert.Equal("APPLE", identity.Provider);
    }

    [Fact]
    public async Task Email_verified_false_is_reported_as_unverified()
    {
        var identity = await _verifier.VerifyAsync(Providers.Google, Token(emailVerified: false), "raw-nonce", CancellationToken.None);

        Assert.False(identity.EmailVerified);
    }

    public static TheoryData<string, string, string?, int, string> Rejections => new()
    {
        { "https://evil.example", "g-client", "raw-nonce", 10, "issuer errado" },
        { "https://accounts.google.com", "someone-elses-client", "raw-nonce", 10, "audience errada" },
        { "https://accounts.google.com", "g-client", "other-nonce", 10, "nonce diferente" },
        { "https://accounts.google.com", "g-client", null, 10, "nonce ausente" },
        { "https://accounts.google.com", "g-client", "raw-nonce", -10, "expirado" },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task Invalid_tokens_are_rejected(string issuer, string audience, string? nonce, int lifetimeMinutes, string reason)
    {
        var token = Token(issuer, audience, nonce, lifetime: TimeSpan.FromMinutes(lifetimeMinutes));

        await Assert.ThrowsAsync<IdentityTokenException>(() => _verifier.VerifyAsync(Providers.Google, token, "raw-nonce", CancellationToken.None));
        _ = reason;
    }

    [Fact]
    public async Task Token_signed_by_an_unknown_key_is_rejected_even_with_a_known_kid()
    {
        var forged = Token(signWith: _other);

        await Assert.ThrowsAsync<IdentityTokenException>(() => _verifier.VerifyAsync(Providers.Google, forged, "raw-nonce", CancellationToken.None));
    }

    [Fact]
    public async Task Unsigned_and_tampered_tokens_are_rejected()
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var none = $"{B64("""{"alg":"none"}""")}.{B64("""{"sub":"x","iss":"https://accounts.google.com","aud":"g-client","nonce":"raw-nonce","email":"a@b.c"}""")}.";
        var parts = Token().Split('.');
        var tampered = $"{parts[0]}.{B64("""{"sub":"admin","iss":"https://accounts.google.com","aud":"g-client","nonce":"raw-nonce"}""")}.{parts[2]}";

        await Assert.ThrowsAsync<IdentityTokenException>(() => _verifier.VerifyAsync(Providers.Google, none, "raw-nonce", CancellationToken.None));
        await Assert.ThrowsAsync<IdentityTokenException>(() => _verifier.VerifyAsync(Providers.Google, tampered, "raw-nonce", CancellationToken.None));
        await Assert.ThrowsAsync<IdentityTokenException>(() => _verifier.VerifyAsync(Providers.Google, "garbage", "raw-nonce", CancellationToken.None));
    }

    [Fact]
    public async Task Hmac_signed_tokens_cannot_be_confused_with_rs256()
    {
        var hmac = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "https://accounts.google.com",
            Audience = "g-client",
            Claims = new Dictionary<string, object> { ["sub"] = "x", ["nonce"] = "raw-nonce" },
            Expires = _time.GetUtcNow().UtcDateTime.AddMinutes(5),
            SigningCredentials = new SigningCredentials(hmac, SecurityAlgorithms.HmacSha256),
        };
        var token = new JsonWebTokenHandler().CreateToken(descriptor);

        await Assert.ThrowsAsync<IdentityTokenException>(() => _verifier.VerifyAsync(Providers.Google, token, "raw-nonce", CancellationToken.None));
    }

    [Fact]
    public async Task Rotated_key_triggers_one_forced_jwks_refresh()
    {
        using var rotated = RSA.Create(2048);
        var token = Token(signWith: rotated, kid: "k2");
        var newSet = new JsonWebKeySet();
        newSet.Keys.Add(JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(rotated.ExportParameters(false)) { KeyId = "k2" }));
        _jwks.OnRefresh = newSet;

        var identity = await _verifier.VerifyAsync(Providers.Google, token, "raw-nonce", CancellationToken.None);

        Assert.Equal("sub-1", identity.Subject);
        Assert.Equal(1, _jwks.Refreshes);
    }

    [Fact]
    public async Task Provider_without_configured_audiences_or_empty_nonce_fails_closed()
    {
        var unconfigured = new OidcIdentityTokenVerifier(Options.Create(new IdentityOptions()), _jwks, _time);

        await Assert.ThrowsAsync<IdentityTokenException>(() => unconfigured.VerifyAsync(Providers.Google, Token(), "raw-nonce", CancellationToken.None));
        await Assert.ThrowsAsync<IdentityTokenException>(() => _verifier.VerifyAsync(Providers.Google, Token(), string.Empty, CancellationToken.None));
        await Assert.ThrowsAsync<IdentityTokenException>(() => _verifier.VerifyAsync("FACEBOOK", Token(), "raw-nonce", CancellationToken.None));
    }

    private sealed class StubJwks(JsonWebKeySet initial) : IJwksProvider
    {
        public JsonWebKeySet? OnRefresh { get; set; }

        public int Refreshes { get; private set; }

        public Task<JsonWebKeySet> GetKeysAsync(string provider, bool forceRefresh, CancellationToken cancellationToken)
        {
            if (forceRefresh)
            {
                Refreshes++;
                return Task.FromResult(OnRefresh ?? initial);
            }

            return Task.FromResult(initial);
        }
    }
}
