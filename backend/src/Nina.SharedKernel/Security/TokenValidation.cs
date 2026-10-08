using Microsoft.IdentityModel.Tokens;

namespace Nina.SharedKernel.Security;

/// <summary>Parâmetros de validação do perfil JWT do contrato (AD-31): ES256 fixo, <c>typ</c>, <c>kid</c> obrigatório, <c>iss</c>/<c>aud</c>.</summary>
public static class TokenValidation
{
    public const string AccessTokenType = "at+jwt";
    public const string ReauthTokenType = "reauth+jwt";

    public static TokenValidationParameters Create(JwtOptions options, JwtKeyring keyring, TimeProvider time, string audience, string tokenType) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = options.Issuer,
        ValidateAudience = true,
        ValidAudience = audience,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ValidateIssuerSigningKey = true,
        // O algoritmo é fixado aqui; o `alg` do cabeçalho nunca é confiado (none, HS*, RS256 recusados).
        ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
        ValidTypes = [tokenType],
        // `kid` obrigatório: sem ele ou com kid desconhecido nenhuma chave é candidata.
        IssuerSigningKeyResolver = (_, _, kid, _) =>
            string.IsNullOrEmpty(kid) ? [] : keyring.VerificationKeys.Where(k => k.KeyId == kid),
        TryAllIssuerSigningKeys = false,
        ClockSkew = TimeSpan.FromSeconds(Math.Min(options.ClockSkewSeconds, 60)),
        LifetimeValidator = TokenLifetime.Validator(time, TimeSpan.FromSeconds(Math.Min(options.ClockSkewSeconds, 60))),
        NameClaimType = "sub",
    };
}
