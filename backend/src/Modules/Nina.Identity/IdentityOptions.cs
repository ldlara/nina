namespace Nina.Identity;

/// <summary>Configuração do módulo Identity (seção <c>Identity</c>). Valores padrão seguem privacy-security-spec e ADR-0007.</summary>
public sealed class IdentityOptions
{
    public const string SectionName = "Identity";

    // Política de senha (SEC-010): tamanho mínimo, sem regras de composição. Valores a confirmar em D-16.
    public int PasswordMinLength { get; set; } = 10;

    public int PasswordMaxLength { get; set; } = 128;

    /// <summary>Dígitos do código de verificação (contrato: 6 a 12; alvo 8).</summary>
    public int VerificationCodeDigits { get; set; } = 8;

    /// <summary>Tentativas por código antes de invalidá-lo (AD-34).</summary>
    public int VerificationCodeAttempts { get; set; } = 5;

    // Argon2id (OWASP: m=19 MiB, t=2, p=1).
    public int Argon2MemoryKiB { get; set; } = 19456;

    public int Argon2Iterations { get; set; } = 2;

    public int Argon2Parallelism { get; set; } = 1;

    public int VerificationCodeMinutes { get; set; } = 10;

    public int ResendAfterSeconds { get; set; } = 60;

    public int PasswordResetMinutes { get; set; } = 30;

    /// <summary>Validade do refresh token (cortada pelo limite absoluto da sessão).</summary>
    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>Vida absoluta da sessão (privacy-security-spec §4: 30 dias).</summary>
    public int SessionAbsoluteDays { get; set; } = 30;

    public string DefaultLocale { get; set; } = "pt-BR";

    public string LegalBaseUrl { get; set; } = "https://nina.app/legal";

    public DateTimeOffset LegalEffectiveAt { get; set; } = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Client IDs aceitos como <c>aud</c> de id_tokens do Google.</summary>
    public List<string> GoogleClientIds { get; set; } = [];

    /// <summary>Services/Bundle IDs aceitos como <c>aud</c> de id_tokens da Apple.</summary>
    public List<string> AppleClientIds { get; set; } = [];

    public RateLimitSettings RateLimits { get; set; } = new();
}

/// <summary>Limites de taxa (SEC-040/041). Janela de falhas em minutos; demais por hora/minuto conforme o nome.</summary>
public sealed class RateLimitSettings
{
    public int FailureWindowMinutes { get; set; } = 15;

    public int LoginFailuresPerAccountAndIp { get; set; } = 5;

    public int LoginFailuresPerAccount { get; set; } = 20;

    public int LoginFailuresPerIp { get; set; } = 30;

    public int RegisterPerIpPerHour { get; set; } = 20;

    public int RegisterPerEmailPerHour { get; set; } = 5;

    public int ForgotPerIpPerHour { get; set; } = 10;

    public int ForgotPerEmailPerHour { get; set; } = 3;

    public int LoginFailuresPerDevice { get; set; } = 20;

    /// <summary>Teto do bloqueio progressivo de login, em horas.</summary>
    public int LoginMaxLockoutHours { get; set; } = 24;

    public int VerifyFailuresPerIp { get; set; } = 30;

    public int ResetFailuresPerIp { get; set; } = 20;

    public int RefreshPerIpPerMinute { get; set; } = 120;

    public int SocialPerIpPerMinute { get; set; } = 30;

    public int ReauthFailuresPerUser { get; set; } = 5;
}
