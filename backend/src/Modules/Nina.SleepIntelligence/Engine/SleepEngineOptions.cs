namespace Nina.SleepIntelligence;

/// <summary>
/// Parâmetros determinísticos do motor (não clínicos: tratamento de dados e confiança). Valores provisórios,
/// ajustáveis sem alterar código; qualquer mudança altera o <see cref="SleepEngineInfo.ModelVersion"/> e exige revisão dos golden tests.
/// </summary>
public sealed record SleepEngineOptions
{
    /// <summary>Janela do histórico recente usada nas estatísticas (dias).</summary>
    public int HistoryWindowDays { get; init; } = 14;

    /// <summary>Volume mínimo de wake windows observadas para sair do cold start (D-20, provisório).</summary>
    public int MinObservationsForPersonalization { get; init; } = 5;

    /// <summary>Observações a partir das quais o peso do histórico atinge o máximo.</summary>
    public int FullPersonalizationObservations { get; init; } = 15;

    public int MinPersonalWeightPercent { get; init; } = 50;

    public int MaxPersonalWeightPercent { get; init; } = 90;

    /// <summary>Folga (%) além de [mín, máx] da faixa etária que a previsão personalizada pode alcançar.</summary>
    public int PersonalizationSlackPercent { get; init; } = 15;

    /// <summary>Wake window observada abaixo de (mín × este %) é descartada como outlier.</summary>
    public int OutlierLowPercentOfMin { get; init; } = 40;

    /// <summary>Wake window observada acima de (máx × este %) é descartada como outlier (inclui lacunas de registro).</summary>
    public int OutlierHighPercentOfMax { get; init; } = 250;

    public int MinValidSleepMinutes { get; init; } = 5;

    public int MaxNapMinutes { get; init; } = 300;

    public int MaxNightMinutes { get; init; } = 960;

    /// <summary>Sono em aberto há mais que isso (soneca) é tratado como timer esquecido (SE-OUT-04).</summary>
    public int OpenNapMaxMinutes { get; init; } = 360;

    public int OpenNightMaxMinutes { get; init; } = 960;

    /// <summary>Sessões do mesmo tipo com intervalo menor que isso são unificadas.</summary>
    public int MergeGapMinutes { get; init; } = 10;

    /// <summary>Tolerância a relógios adiantados ao detectar registros no futuro.</summary>
    public int FutureToleranceMinutes { get; init; } = 5;

    /// <summary>Antecedência mínima de qualquer previsão em relação ao instante de referência.</summary>
    public int MinLeadMinutes { get; init; } = 1;

    public int MinWindowMinutes { get; init; } = 10;

    /// <summary>Se o último sono terminou há mais que (máx da faixa × este %), a âncora é descartada (assume-se "acordado agora").</summary>
    public int StaleAnchorPercentOfMax { get; init; } = 250;

    /// <summary>Dias distintos com dados exigidos para confiança alta.</summary>
    public int HighConfidenceMinObservations { get; init; } = 12;

    public int HighConfidenceMinDays { get; init; } = 4;

    /// <summary>Se a observação mais recente tem mais dias que isso, a confiança não passa de média.</summary>
    public int StaleDaysForMediumCap { get; init; } = 3;

    /// <summary>Se a observação mais recente tem mais dias que isso, a confiança é baixa.</summary>
    public int StaleDaysForLowConfidence { get; init; } = 7;

    public int HighConfidenceScore { get; init; } = 75;

    public int MediumConfidenceScore { get; init; } = 45;

    /// <summary>Dispersão relativa (MAD/mediana, %) acima da qual o histórico é considerado irregular.</summary>
    public int IrregularSpreadPercent { get; init; } = 25;

    /// <summary>Dispersão relativa máxima (%) para confiança alta.</summary>
    public int HighConfidenceMaxSpreadPercent { get; init; } = 15;

    /// <summary>Largura da faixa prevista (% da meia-amplitude da tabela) por nível de confiança.</summary>
    public int WindowPercentLow { get; init; } = 50;

    public int WindowPercentMedium { get; init; } = 35;

    public int WindowPercentHigh { get; init; } = 25;

    public AgeCorrectionOptions AgeCorrection { get; init; } = AgeCorrectionOptions.Default;

    public static SleepEngineOptions Default { get; } = new();
}

/// <summary>Identificação do modelo. Muda sempre que as regras mudam (RF-011-A7).</summary>
public static class SleepEngineInfo
{
    public const string ModelVersion = "rules-2026.10.1";

    public const string DisclaimerKey = "prediction.estimate_not_diagnosis";
}
