using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nina.SharedKernel.Security;

namespace Nina.Identity.External;

public static class Providers
{
    public const string Google = "GOOGLE";
    public const string Apple = "APPLE";

    public static bool IsKnown(string? provider) => provider is Google or Apple;
}

/// <summary>Identidade já validada de um id_token externo.</summary>
public sealed record VerifiedIdentity(string Provider, string Subject, string? Email, bool EmailVerified);

/// <summary>id_token inválido (assinatura, <c>aud</c>, <c>iss</c>, expiração ou <c>nonce</c>).</summary>
public sealed class IdentityTokenException(string message) : Exception(message);

/// <summary>Valida id_tokens de Google/Apple (ADR-0007). Os testes usam fake; a implementação real é <see cref="OidcIdentityTokenVerifier"/>.</summary>
public interface IIdentityTokenVerifier
{
    Task<VerifiedIdentity> VerifyAsync(string provider, string idToken, string nonce, CancellationToken cancellationToken);
}

/// <summary>Fornece o JWKS do provedor (cache e atualização a cargo da implementação).</summary>
public interface IJwksProvider
{
    Task<JsonWebKeySet> GetKeysAsync(string provider, bool forceRefresh, CancellationToken cancellationToken);
}

/// <summary>Busca o JWKS por HTTPS com cache de 1 h; atualização forçada no máximo a cada 5 min.</summary>
public sealed class HttpJwksProvider(HttpClient http, TimeProvider time) : IJwksProvider, IDisposable
{
    private static readonly Dictionary<string, Uri> Endpoints = new()
    {
        [Providers.Google] = new Uri("https://www.googleapis.com/oauth2/v3/certs"),
        [Providers.Apple] = new Uri("https://appleid.apple.com/auth/keys"),
    };

    private readonly Dictionary<string, (JsonWebKeySet Keys, DateTimeOffset FetchedAt)> _cache = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public void Dispose() => _lock.Dispose();

    public async Task<JsonWebKeySet> GetKeysAsync(string provider, bool forceRefresh, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            if (_cache.TryGetValue(provider, out var entry))
            {
                var age = now - entry.FetchedAt;
                if (age < TimeSpan.FromHours(1) && !(forceRefresh && age > TimeSpan.FromMinutes(5)))
                {
                    return entry.Keys;
                }
            }

            var json = await http.GetStringAsync(Endpoints[provider], cancellationToken);
            var keys = new JsonWebKeySet(json);
            _cache[provider] = (keys, now);
            return keys;
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>
/// Implementação real: RS256 apenas, <c>iss</c>/<c>aud</c> fixos por provedor, expiração e <c>nonce</c>.
/// Aceita o nonce cru (Google) ou o SHA-256 em hex/base64url do nonce cru (Apple).
/// </summary>
public sealed class OidcIdentityTokenVerifier(IOptions<IdentityOptions> options, IJwksProvider jwks, TimeProvider time) : IIdentityTokenVerifier
{
    private static readonly string[] GoogleIssuers = ["https://accounts.google.com", "accounts.google.com"];
    private static readonly string[] AppleIssuers = ["https://appleid.apple.com"];

    public async Task<VerifiedIdentity> VerifyAsync(string provider, string idToken, string nonce, CancellationToken cancellationToken)
    {
        var (issuers, audiences) = provider switch
        {
            Providers.Google => (GoogleIssuers, options.Value.GoogleClientIds),
            Providers.Apple => (AppleIssuers, options.Value.AppleClientIds),
            _ => throw new IdentityTokenException("provedor desconhecido"),
        };
        if (audiences.Count == 0 || string.IsNullOrEmpty(nonce))
        {
            throw new IdentityTokenException("provedor não configurado ou nonce ausente");
        }

        var result = await ValidateAsync(provider, idToken, issuers, audiences, false, cancellationToken);
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            result = await ValidateAsync(provider, idToken, issuers, audiences, true, cancellationToken);
        }

        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
        {
            throw new IdentityTokenException("id_token inválido");
        }

        if (!NonceMatches(jwt, nonce))
        {
            throw new IdentityTokenException("nonce inválido");
        }

        var subject = jwt.Subject;
        if (string.IsNullOrEmpty(subject))
        {
            throw new IdentityTokenException("sub ausente");
        }

        var email = TryString(jwt, "email");
        var verified = TryBool(jwt, "email_verified");
        return new VerifiedIdentity(provider, subject, email, verified);
    }

    private async Task<TokenValidationResult> ValidateAsync(
        string provider, string idToken, string[] issuers, List<string> audiences, bool forceRefresh, CancellationToken ct)
    {
        var keys = await jwks.GetKeysAsync(provider, forceRefresh, ct);
        var parameters = new TokenValidationParameters
        {
            ValidIssuers = issuers,
            ValidAudiences = audiences,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys.GetSigningKeys(),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            LifetimeValidator = TokenLifetime.Validator(time, TimeSpan.FromSeconds(60)),
        };
        return await new JsonWebTokenHandler().ValidateTokenAsync(idToken, parameters);
    }

    private static bool NonceMatches(JsonWebToken jwt, string rawNonce)
    {
        var claim = TryString(jwt, "nonce");
        if (string.IsNullOrEmpty(claim))
        {
            return false;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawNonce));
        var candidates = new[] { rawNonce, Convert.ToHexString(hash).ToLowerInvariant(), Crypto.OpaqueTokens.Base64Url(hash) };
        return candidates.Any(c => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(c), Encoding.UTF8.GetBytes(claim)));
    }

    private static string? TryString(JsonWebToken jwt, string name) =>
        jwt.TryGetPayloadValue<string>(name, out var value) ? value : null;

    // Apple envia email_verified como string "true"; Google como booleano.
    private static bool TryBool(JsonWebToken jwt, string name)
    {
        if (jwt.TryGetPayloadValue<JsonElement>(name, out var element))
        {
            return element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.String => string.Equals(element.GetString(), "true", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }

        return false;
    }
}
