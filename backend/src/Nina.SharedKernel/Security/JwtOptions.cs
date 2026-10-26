namespace Nina.SharedKernel.Security;

/// <summary>Configuração do access token e do token de reautenticação (seção <c>Jwt</c>), conforme o perfil do contrato v1.0.1 (AD-31).</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "https://api.nina.app";

    public string Audience { get; set; } = "nina-api";

    public string ReauthAudience { get; set; } = "nina-reauth";

    /// <summary>Chave privada ECDSA P-256 (PKCS#8, PEM) usada para assinar (ES256). Obrigatória fora de Development (vem do cofre, ADR-0006).</summary>
    public string? SigningKeyPem { get; set; }

    /// <summary>Identificador da chave de assinatura (<c>kid</c>); se vazio, deriva-se do fingerprint da chave pública.</summary>
    public string? KeyId { get; set; }

    /// <summary>Chaves públicas adicionais aceitas só para verificação (<c>kid</c> -> PEM SPKI): rotação sem derrubar tokens vigentes.</summary>
    public Dictionary<string, string> VerificationKeys { get; set; } = [];

    public int AccessTokenMinutes { get; set; } = 15;

    public int ReauthTokenSeconds { get; set; } = 300;

    /// <summary>Tolerância de relógio; o contrato limita a 60 s.</summary>
    public int ClockSkewSeconds { get; set; } = 30;
}

/// <summary>Segredo mestre para HMACs derivados (hash de código de verificação, de token de recuperação, de IP).</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>Base64 com pelo menos 32 bytes. Obrigatório fora de Development.</summary>
    public string? MasterKey { get; set; }
}
