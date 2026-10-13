using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nina.SharedKernel.Security;

namespace Nina.Identity.Services;

/// <summary>Claims aceitas de um token de reautenticação (já com assinatura, <c>typ</c>, <c>aud</c>, vigência, <c>sub</c> e <c>sid</c> conferidos).</summary>
public sealed record ReauthClaims(string Jti, IReadOnlyList<string> Scopes, DateTimeOffset ExpiresAt);

/// <summary>
/// Emite o access token (JWT ES256, <c>typ=at+jwt</c>, <c>kid</c>, ≤ 15 min) e o token de reautenticação
/// (<c>typ=reauth+jwt</c>, <c>aud=nina-reauth</c>, escopo, <c>jti</c>, ≤ 300 s), conforme o perfil do contrato v1.0.1.
/// </summary>
public sealed class TokenService(JwtKeyring keyring, IOptions<JwtOptions> jwt, TimeProvider time)
{
    public (string Token, int ExpiresIn) IssueAccessToken(Guid userId, Guid sessionId, Guid deviceId)
    {
        var lifetime = TimeSpan.FromMinutes(Math.Clamp(jwt.Value.AccessTokenMinutes, 1, 15));
        var claims = new List<Claim>
        {
            new("sub", userId.ToString("D")),
            new(ClaimsExtensions.SessionClaim, sessionId.ToString("D")),
            new(ClaimsExtensions.DeviceClaim, deviceId.ToString("D")),
        };
        return (Create(jwt.Value.Audience, TokenValidation.AccessTokenType, claims, lifetime), (int)lifetime.TotalSeconds);
    }

    /// <summary>Sem escopos (transição da v1.x) o token vale para UMA operação sensível qualquer; com escopos, só para eles.</summary>
    public (string Token, int ExpiresIn, string Jti) IssueReauthToken(Guid userId, Guid sessionId, IReadOnlyCollection<string>? scopes)
    {
        var lifetime = TimeSpan.FromSeconds(Math.Clamp(jwt.Value.ReauthTokenSeconds, 1, 300));
        var jti = Guid.NewGuid().ToString("N");
        var claims = new List<Claim>
        {
            new("sub", userId.ToString("D")),
            new(ClaimsExtensions.SessionClaim, sessionId.ToString("D")),
        };
        if (scopes is { Count: > 0 })
        {
            claims.Add(new Claim("scope", string.Join(' ', scopes)));
        }

        return (Create(jwt.Value.ReauthAudience, TokenValidation.ReauthTokenType, claims, lifetime, jti), (int)lifetime.TotalSeconds, jti);
    }

    /// <summary>Valida o token de reautenticação para o usuário e a sessão correntes; null se inválido por qualquer motivo.</summary>
    public async Task<ReauthClaims?> ParseReauthAsync(string? token, Guid userId, Guid sessionId)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 16 or > 4096)
        {
            return null;
        }

        var parameters = TokenValidation.Create(jwt.Value, keyring, time, jwt.Value.ReauthAudience, TokenValidation.ReauthTokenType);
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
        if (!result.IsValid || result.SecurityToken is not JsonWebToken parsed || string.IsNullOrEmpty(parsed.Id))
        {
            return null;
        }

        if (parsed.Subject != userId.ToString("D")
            || !parsed.TryGetPayloadValue<string>(ClaimsExtensions.SessionClaim, out var sid) || sid != sessionId.ToString("D")
            || parsed.ValidTo - parsed.IssuedAt > TimeSpan.FromSeconds(300))
        {
            return null;
        }

        IReadOnlyList<string> scopes = parsed.TryGetPayloadValue<string>("scope", out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [];
        return new ReauthClaims(parsed.Id, scopes, new DateTimeOffset(parsed.ValidTo, TimeSpan.Zero));
    }

    private string Create(string audience, string type, List<Claim> claims, TimeSpan lifetime, string? jti = null)
    {
        var now = time.GetUtcNow().UtcDateTime;
        claims.Add(new Claim("jti", jti ?? Guid.NewGuid().ToString("N")));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Value.Issuer,
            Audience = audience,
            TokenType = type,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = now + lifetime,
            SigningCredentials = new SigningCredentials(keyring.SigningKey, SecurityAlgorithms.EcdsaSha256),
        };
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }
}
