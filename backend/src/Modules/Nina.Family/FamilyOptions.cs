namespace Nina.Family;

/// <summary>Configuração do módulo Family (seção <c>Family</c>).</summary>
public sealed class FamilyOptions
{
    public const string SectionName = "Family";

    /// <summary>Validade do convite em dias (contrato: <c>limits.invitation_ttl_days</c>, padrão 7; SEC-004/D-17).</summary>
    public int InvitationTtlDays { get; set; } = 7;

    /// <summary>
    /// Exige a declaração de responsável legal (<c>child_data_guardian</c>, versão vigente) para criar bebê (contrato: 403 CONSENT_REQUIRED).
    /// Só desligue em ambientes de desenvolvimento enquanto o registro da declaração anterior ao bebê não existir no Identity.
    /// </summary>
    public bool RequireGuardianConsent { get; set; } = true;

    public string DefaultLocale { get; set; } = "pt-BR";

    /// <summary>Retenção da resposta guardada por <c>Idempotency-Key</c> (contrato: 24 h).</summary>
    public int IdempotencyRetentionHours { get; set; } = 24;

    public FamilyRateLimits RateLimits { get; set; } = new();
}

/// <summary>Limites de taxa por usuário/IP (SEC-040). Valores por janela indicada no nome; configuração do servidor.</summary>
public sealed class FamilyRateLimits
{
    /// <summary>Teto geral de chamadas autenticadas do módulo, por usuário.</summary>
    public int RequestsPerUserPerMinute { get; set; } = 300;

    public int BabyCreatePerUserPerHour { get; set; } = 30;

    public int InvitePerUserPerHour { get; set; } = 30;

    public int ResendPerUserPerHour { get; set; } = 30;

    /// <summary>inspect/accept/decline: o token é de 256 bits, mas o limite barra enumeração e tentativas de e-mail alheio.</summary>
    public int InvitationTokenOpsPerUserPerMinute { get; set; } = 10;

    public int InvitationTokenOpsPerIpPerMinute { get; set; } = 60;

    /// <summary>Exclusão de bebê e transferência de propriedade (reautenticação já limita; isto limita por usuário).</summary>
    public int SensitivePerUserPerHour { get; set; } = 10;
}
