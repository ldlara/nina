namespace Nina.SharedKernel.Security;

/// <summary>Configuração do access token e do token de reautenticação (seção <c>Jwt</c>).</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "https://api.nina.app";

    public string Audience { get; set; } = "nina-app";

    public string ReauthAudience { get; set; } = "nina-reauth";

    /// <summary>Chave simétrica HS256 em Base64 (mínimo 32 bytes). Obrigatória fora de Development (vem do cofre, ADR-0006).</summary>
    public string? SigningKey { get; set; }

    public int AccessTokenMinutes { get; set; } = 15;

    public int ReauthTokenSeconds { get; set; } = 300;

    public int ClockSkewSeconds { get; set; } = 15;
}
