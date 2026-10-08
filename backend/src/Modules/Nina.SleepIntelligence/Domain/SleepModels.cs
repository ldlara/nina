namespace Nina.SleepIntelligence;

/// <summary>Tipo da sessão de sono (contrato: <c>NAP</c> | <c>NIGHT</c>).</summary>
public enum SleepKind
{
    Nap,
    Night,
}

/// <summary>Tipo de previsão (contrato: <c>NEXT_NAP</c> | <c>BEDTIME</c>).</summary>
public enum PredictionKind
{
    NextNap,
    Bedtime,
}

/// <summary>Situação da resposta (contrato: <c>AVAILABLE</c> | <c>SLEEPING</c> | <c>INSUFFICIENT_DATA</c>).</summary>
public enum PredictionStatus
{
    Available,
    Sleeping,
    InsufficientData,
}

/// <summary>
/// Nível de confiança. Mapeamento ao contrato v1.0.1 (<c>confidence.level</c>):
/// <see cref="Low"/> = <c>BUILDING</c> ("Em construção"), <see cref="Medium"/> = <c>FAIR</c> ("Razoável"),
/// <see cref="High"/> = <c>GOOD</c> ("Boa").
/// </summary>
public enum ConfidenceLevel
{
    Low,
    Medium,
    High,
}

/// <summary>
/// Sessão de sono do histórico (entrada do motor). Instantes em UTC; <see cref="TimeZoneId"/> é o fuso IANA
/// vigente quando o evento foi registrado (RB-014); nulo = fuso do bebê. Eventos excluídos (tombstone) não devem ser enviados.
/// </summary>
public sealed record SleepRecord(
    Guid Id,
    DateTimeOffset StartAt,
    DateTimeOffset? EndAt,
    SleepKind Kind,
    string? TimeZoneId = null);

/// <summary>Despertar noturno (ADR-0009): intervalo acordado dentro de uma sessão de sono.</summary>
public sealed record WakeEventRecord(Guid Id, Guid SleepRecordId, DateTimeOffset StartedAt, DateTimeOffset EndedAt);

/// <summary>Dados do bebê usados pelo motor. A idade corrigida nunca é persistida: é derivada de nascimento e DPP.</summary>
public sealed record BabyProfile(DateOnly BirthDate, DateOnly? DueDate, string TimeZoneId);

/// <summary>
/// Preferências do cuidador (RF-014). Campos nulos = padrão por idade. A meta é um alvo do cuidador,
/// nunca um padrão que a criança "deveria" cumprir (RB-005). Faixa de bedtime em hora local do bebê, sem virada de meia-noite.
/// </summary>
public sealed record SleepPreferences(int? TargetNapCount = null, TimeOnly? BedtimeFrom = null, TimeOnly? BedtimeTo = null)
{
    public static SleepPreferences None { get; } = new();

    public bool HasBedtimeRange => BedtimeFrom is not null && BedtimeTo is not null;
}

/// <summary>Entrada completa do motor (histórico bruto, sem I/O).</summary>
public sealed record SleepPredictionRequest(
    BabyProfile Baby,
    IReadOnlyList<SleepRecord> History,
    SleepPreferences? Preferences = null);
