namespace Nina.SleepIntelligence;

/// <summary>Insumos da confiança de uma série de observações.</summary>
internal sealed record ConfidenceInput(
    int Observations,
    int DistinctDays,
    int SpreadPercent,
    int NewestAgeDays,
    bool BaselineOnly);

internal sealed record ConfidenceResult(ConfidenceLevel Level, int Score, bool Irregular);

/// <summary>
/// Confiança determinística por volume, consistência, cobertura de dias e recência (RF-013-A1). Aritmética inteira.
/// Monotônica não decrescente no volume, ceteris paribus (I4). O score numérico é diagnóstico: nunca é mostrado ao usuário (UX 4.5).
/// </summary>
internal static class ConfidenceEvaluator
{
    public static ConfidenceResult Evaluate(ConfidenceInput input, SleepEngineOptions opt)
    {
        if (input.BaselineOnly)
        {
            return new ConfidenceResult(ConfidenceLevel.Low, 0, false);
        }

        var volume = Math.Min(100, input.Observations * 100 / Math.Max(1, opt.FullPersonalizationObservations));
        var consistency = Math.Clamp(100 - ((input.SpreadPercent - 10) * 4), 0, 100);
        var coverage = Math.Min(100, input.DistinctDays * 100 / 5);
        var recency = input.NewestAgeDays <= 1 ? 100 : input.NewestAgeDays <= 3 ? 70 : input.NewestAgeDays <= 7 ? 40 : 0;
        var score = ((volume * 40) + (consistency * 30) + (coverage * 15) + (recency * 15)) / 100;

        var irregular = input.SpreadPercent > opt.IrregularSpreadPercent;
        ConfidenceLevel level;
        if (irregular)
        {
            level = ConfidenceLevel.Low;
        }
        else if (score >= opt.HighConfidenceScore
            && input.Observations >= opt.HighConfidenceMinObservations
            && input.DistinctDays >= opt.HighConfidenceMinDays
            && input.SpreadPercent <= opt.HighConfidenceMaxSpreadPercent)
        {
            level = ConfidenceLevel.High;
        }
        else if (score >= opt.MediumConfidenceScore)
        {
            level = ConfidenceLevel.Medium;
        }
        else
        {
            level = ConfidenceLevel.Low;
        }

        // Dado antigo limita a confiança (SE-OUT-07): sem registros recentes, o histórico pouco diz sobre hoje.
        if (input.NewestAgeDays > opt.StaleDaysForLowConfidence)
        {
            level = ConfidenceLevel.Low;
        }
        else if (input.NewestAgeDays > opt.StaleDaysForMediumCap && level == ConfidenceLevel.High)
        {
            level = ConfidenceLevel.Medium;
        }

        return new ConfidenceResult(level, score, irregular);
    }
}
