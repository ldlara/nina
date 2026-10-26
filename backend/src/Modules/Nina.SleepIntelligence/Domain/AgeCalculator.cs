namespace Nina.SleepIntelligence;

/// <summary>Parâmetros da política de idade corrigida (D-01 em aberto; no produto são editáveis no banco, ADR-0005).</summary>
public sealed record AgeCorrectionOptions
{
    /// <summary>Prematuridade mínima (dias entre nascimento e DPP) para aplicar correção. Valor provisório.</summary>
    public int MinPrematurityDays { get; init; } = 1;

    /// <summary>Acima disso a DPP é tratada como provável erro de digitação e a correção não se aplica. Valor provisório.</summary>
    public int MaxPrematurityDays { get; init; } = 180;

    /// <summary>Idade cronológica máxima (dias) em que a correção ainda se aplica. Valor provisório (24 meses).</summary>
    public int MaxChronologicalDays { get; init; } = 730;

    public static AgeCorrectionOptions Default { get; } = new();
}

/// <summary>Cálculo canônico de idade (ADR-0009 / contrato <c>AgeCalculation</c>).</summary>
public sealed record AgeCalculation(int ChronologicalDays, int? CorrectedDays, bool CorrectionApplied)
{
    /// <summary>Idade usada pelas regras (RIC-02): corrigida quando aplicável, senão cronológica; nunca negativa.</summary>
    public int EffectiveDays => Math.Max(0, CorrectedDays ?? ChronologicalDays);

    /// <summary>"CORRECTED" ou "CHRONOLOGICAL" (qual idade determinou a faixa).</summary>
    public string Basis => CorrectionApplied ? "CORRECTED" : "CHRONOLOGICAL";
}

/// <summary>Calcula idade cronológica e corrigida a partir de datas locais (função pura).</summary>
public static class AgeCalculator
{
    public static AgeCalculation Calculate(DateOnly birthDate, DateOnly? dueDate, DateOnly localToday, AgeCorrectionOptions? options = null)
    {
        var opt = options ?? AgeCorrectionOptions.Default;
        var chronological = Math.Max(0, localToday.DayNumber - birthDate.DayNumber);

        if (dueDate is null)
        {
            return new AgeCalculation(chronological, null, false);
        }

        var prematurity = dueDate.Value.DayNumber - birthDate.DayNumber;
        var applies = prematurity >= opt.MinPrematurityDays
            && prematurity <= opt.MaxPrematurityDays
            && chronological <= opt.MaxChronologicalDays;

        return applies
            ? new AgeCalculation(chronological, chronological - prematurity, true)
            : new AgeCalculation(chronological, null, false);
    }
}
