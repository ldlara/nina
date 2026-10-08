using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nina.SharedKernel.Security;

namespace Nina.Identity.Services;

/// <summary>Emite o access token (JWT HS256, ≤ 15 min) e o token de reautenticação (JWT de 5 min, outra audiência).</summary>
public sealed class TokenService(SecretKeys keys, IOptions<JwtOptions> jwt, TimeProvider time)
{
    private const string ReauthPurpose = "reauth";

    public (string Token, int ExpiresIn) IssueAccessToken(Guid userId, Guid sessionId, Guid deviceId)
    {
        var lifetime = TimeSpan.FromMinutes(Math.Min(jwt.Value.AccessTokenMinutes, 15));
        var claims = new List<Claim>
        {
            new("sub", userId.ToString("D")),
            new(ClaimsExtensions.SessionClaim, sessionId.ToString("D")),
            new(ClaimsExtensions.DeviceClaim, deviceId.ToString("D")),
        };
        return (Create(jwt.Value.Audience, claims, lifetime), (int)lifetime.TotalSeconds);
    }

    public (string Token, int ExpiresIn) IssueReauthToken(Guid userId, Guid sessionId)
    {
        var lifetime = TimeSpan.FromSeconds(jwt.Value.ReauthTokenSeconds);
        var claims = new List<Claim>
        {
            new("sub", userId.ToString("D")),
            new(ClaimsExtensions.SessionClaim, sessionId.ToString("D")),
            new("purpose", ReauthPurpose),
        };
        return (Create(jwt.Value.ReauthAudience, claims, lifetime), (int)lifetime.TotalSeconds);
    }

    /// <summary>O token de reautenticação vale só para o mesmo usuário e a mesma sessão que o obteve.</summary>
    public async Task<bool> ValidateReauthAsync(string? token, Guid userId, Guid sessionId)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048)
        {
            return false;
        }

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Value.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Value.ReauthAudience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = keys.SigningKey,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            LifetimeValidator = TokenLifetime.Validator(time, TimeSpan.FromSeconds(jwt.Value.ClockSkewSeconds)),
        };
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
        if (!result.IsValid || result.SecurityToken is not JsonWebToken parsed)
        {
            return false;
        }

        return parsed.TryGetPayloadValue<string>("purpose", out var purpose) && purpose == ReauthPurpose
               && parsed.Subject == userId.ToString("D")
               && parsed.TryGetPayloadValue<string>(ClaimsExtensions.SessionClaim, out var sid) && sid == sessionId.ToString("D");
    }

    private string Create(string audience, List<Claim> claims, TimeSpan lifetime)
    {
        var now = time.GetUtcNow().UtcDateTime;
        claims.Add(new Claim("jti", Guid.NewGuid().ToString("N")));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Value.Issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = now + lifetime,
            SigningCredentials = new SigningCredentials(keys.SigningKey, SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }
}
