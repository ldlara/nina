using Microsoft.IdentityModel.Tokens;

namespace Nina.SharedKernel.Security;

/// <summary>Validação de vigência do JWT usando <see cref="TimeProvider"/> (relógio testável).</summary>
public static class TokenLifetime
{
    public static LifetimeValidator Validator(TimeProvider time, TimeSpan skew) =>
        (notBefore, expires, _, _) =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            if (expires is null)
            {
                throw new SecurityTokenNoExpirationException("exp ausente");
            }

            if (now > expires.Value.ToUniversalTime() + skew)
            {
                throw new SecurityTokenExpiredException("expirado") { Expires = expires.Value };
            }

            if (notBefore is { } nbf && now < nbf.ToUniversalTime() - skew)
            {
                throw new SecurityTokenNotYetValidException("ainda não válido") { NotBefore = nbf };
            }

            return true;
        };
}
