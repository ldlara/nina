namespace Nina.SleepIntelligence;

/// <summary>Explicação localizável: chave de mensagem + parâmetros (sem texto pronto). Parâmetros em ordem de chave estável.</summary>
public sealed record Explanation(string Key, IReadOnlyDictionary<string, object> Params);

/// <summary>
/// Uma previsão. É um valor derivado, de tipo distinto de <see cref="SleepRecord"/>: nunca altera nem cria sessão (RB-001, INV-05).
/// <see cref="PredictedStart"/>/<see cref="PredictedEnd"/> formam uma faixa (não uma hora exata), em UTC.
/// </summary>
public sealed record SleepPredictionItem(
    PredictionKind Kind,
    DateTimeOffset PredictedStart,
    DateTimeOffset? PredictedEnd,
    ConfidenceLevel Confidence,
    int ConfidenceScore,
    Explanation Explanation,
    bool BaselineOnly);

/// <summary>Resumo da estrutura de rotina usada (RF-014).</summary>
public sealed record SchedulePlan(
    int? ExpectedNapCount,
    string NapCountSource,
    int NapsCompletedToday,
    int? BedtimeRangeFromMinuteOfDay,
    int? BedtimeRangeToMinuteOfDay,
    bool PreferencesApplied);

/// <summary>Contagens de qualidade de dados (para diagnóstico e explicação; sem PII).</summary>
public sealed record DataQualityReport(
    int ValidSessions,
    int IgnoredInvalid,
    int IgnoredTooShort,
    int IgnoredTooLong,
    int IgnoredFuture,
    int MergedOverlaps,
    bool OpenSessionIgnored,
    bool TimezoneChangedInHistory,
    int WakeWindowObservations,
    int WakeWindowOutliers,
    int DaysUsed);

/// <summary>Saída completa do motor. Equivale a <c>SleepPredictionResponse</c> do contrato, mais metadados de reprodutibilidade.</summary>
public sealed record SleepPredictionResult(
    PredictionStatus Status,
    string DisclaimerKey,
    IReadOnlyList<SleepPredictionItem> Predictions,
    Explanation? StatusExplanation,
    string ModelVersion,
    string ReferenceTableVersion,
    string ReferenceTableHash,
    bool ReferenceTableIsDraft,
    DateTimeOffset ComputedAt,
    string InputsFingerprint,
    AgeCalculation? Age,
    SchedulePlan? Plan,
    DataQualityReport DataQuality);

/// <summary>Nomes do contrato OpenAPI v1.0.1 para os enums do motor (o contrato é a fonte; este mapeamento não o altera).</summary>
public static class ContractNames
{
    public static string Of(PredictionKind kind) => kind switch
    {
        PredictionKind.NextNap => "NEXT_NAP",
        PredictionKind.Bedtime => "BEDTIME",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string Of(PredictionStatus status) => status switch
    {
        PredictionStatus.Available => "AVAILABLE",
        PredictionStatus.Sleeping => "SLEEPING",
        PredictionStatus.InsufficientData => "INSUFFICIENT_DATA",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static string Of(ConfidenceLevel level) => level switch
    {
        ConfidenceLevel.Low => "BUILDING",
        ConfidenceLevel.Medium => "FAIR",
        ConfidenceLevel.High => "GOOD",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };
}

/// <summary>API do motor para o módulo Tracking (e jobs de recálculo). Pura, sem I/O.</summary>
public interface ISleepPredictionEngine
{
    /// <summary>Calcula usando o relógio injetado (<see cref="TimeProvider"/>).</summary>
    SleepPredictionResult Predict(SleepPredictionRequest request);

    /// <summary>Calcula em um instante de referência explícito (recálculo reprodutível; idempotente).</summary>
    SleepPredictionResult Predict(SleepPredictionRequest request, DateTimeOffset asOf);
}
