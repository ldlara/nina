using System.Globalization;

namespace Nina.SleepIntelligence;

/// <summary>Erro de validação de preferências (código estável, mapeável a <c>400</c> pelo Tracking/Api).</summary>
public sealed record PreferenceError(string Code, string Field);

/// <summary>Valida meta de sonecas e faixa de bedtime (RF-014-A2; limites plausíveis D-21 vêm da tabela de referência).</summary>
public static class SleepPreferencesValidator
{
    public const string NapCountOutOfRange = "SLEEP_PREF_NAP_COUNT_OUT_OF_RANGE";
    public const string BedtimeIncomplete = "SLEEP_PREF_BEDTIME_INCOMPLETE";
    public const string BedtimeOrder = "SLEEP_PREF_BEDTIME_ORDER";
    public const string BedtimeImplausible = "SLEEP_PREF_BEDTIME_IMPLAUSIBLE";
    public const string BedtimeTooNarrow = "SLEEP_PREF_BEDTIME_TOO_NARROW";

    /// <summary>Largura mínima da faixa preferida (minutos).</summary>
    public const int MinRangeMinutes = 15;

    public static IReadOnlyList<PreferenceError> Validate(SleepPreferences? preferences, ReferenceTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var errors = new List<PreferenceError>();
        if (preferences is null)
        {
            return errors;
        }

        if (preferences.TargetNapCount is { } n && (n < 0 || n > table.Plausibility.MaxNapCount))
        {
            errors.Add(new PreferenceError(NapCountOutOfRange, "target_nap_count"));
        }

        var hasFrom = preferences.BedtimeFrom is not null;
        var hasTo = preferences.BedtimeTo is not null;
        if (hasFrom != hasTo)
        {
            errors.Add(new PreferenceError(BedtimeIncomplete, hasFrom ? "bedtime_to" : "bedtime_from"));
        }
        else if (hasFrom)
        {
            var from = Minutes(preferences.BedtimeFrom!.Value);
            var to = Minutes(preferences.BedtimeTo!.Value);
            if (from >= to)
            {
                errors.Add(new PreferenceError(BedtimeOrder, "bedtime_from"));
            }
            else
            {
                if (from < table.Plausibility.BedtimeEarliestMinuteOfDay || to > table.Plausibility.BedtimeLatestMinuteOfDay)
                {
                    errors.Add(new PreferenceError(BedtimeImplausible, "bedtime_from"));
                }

                if (to - from < MinRangeMinutes)
                {
                    errors.Add(new PreferenceError(BedtimeTooNarrow, "bedtime_to"));
                }
            }
        }

        return errors;
    }

    /// <summary>Interpreta "HH:mm" (formato do contrato); nulo se inválido.</summary>
    public static TimeOnly? ParseTime(string? value) =>
        value is not null && TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    private static int Minutes(TimeOnly t) => (t.Hour * 60) + t.Minute;
}
