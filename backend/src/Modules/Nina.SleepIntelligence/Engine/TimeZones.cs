namespace Nina.SleepIntelligence;

/// <summary>Conversões de fuso determinísticas. Durações sempre por diferença de instantes UTC (SE-TZ-03).</summary>
internal static class TimeZones
{
    /// <summary>Nomes IANA legados/renomeados que o tzdata do host pode não conhecer (SE-TZ-08).</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["America/Buenos_Aires"] = "America/Argentina/Buenos_Aires",
        ["America/Catamarca"] = "America/Argentina/Catamarca",
        ["America/Cordoba"] = "America/Argentina/Cordoba",
        ["America/Jujuy"] = "America/Argentina/Jujuy",
        ["America/Mendoza"] = "America/Argentina/Mendoza",
        ["Asia/Calcutta"] = "Asia/Kolkata",
        ["Asia/Katmandu"] = "Asia/Kathmandu",
        ["Asia/Saigon"] = "Asia/Ho_Chi_Minh",
        ["Asia/Rangoon"] = "Asia/Yangon",
        ["Europe/Kiev"] = "Europe/Kyiv",
        ["Atlantic/Faeroe"] = "Atlantic/Faroe",
        ["Pacific/Truk"] = "Pacific/Chuuk",
        ["US/Eastern"] = "America/New_York",
        ["US/Pacific"] = "America/Los_Angeles",
        ["Brazil/East"] = "America/Sao_Paulo",
    };

    public static TimeZoneInfo? TryResolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (TryFind(id, out var tz))
        {
            return tz;
        }

        if (Aliases.TryGetValue(id, out var alias) && TryFind(alias, out tz))
        {
            return tz;
        }

        // Reverso: o host pode conhecer só o nome legado.
        foreach (var (legacy, current) in Aliases)
        {
            if (string.Equals(current, id, StringComparison.Ordinal) && TryFind(legacy, out tz))
            {
                return tz;
            }
        }

        return null;
    }

    public static DateTime LocalDateTime(DateTimeOffset utc, TimeZoneInfo tz) => TimeZoneInfo.ConvertTime(utc, tz).DateTime;

    public static DateOnly LocalDate(DateTimeOffset utc, TimeZoneInfo tz) => DateOnly.FromDateTime(LocalDateTime(utc, tz));

    public static int LocalMinuteOfDay(DateTimeOffset utc, TimeZoneInfo tz)
    {
        var t = LocalDateTime(utc, tz);
        return (t.Hour * 60) + t.Minute;
    }

    /// <summary>
    /// Converte data + minuto do dia locais em instante UTC. Hora inexistente (início do horário de verão) avança até a
    /// primeira hora válida; hora repetida usa a primeira ocorrência. Funciona com offsets fracionários (ex.: +05:30, Lord Howe).
    /// </summary>
    public static DateTimeOffset ToUtc(DateOnly date, int minuteOfDay, TimeZoneInfo tz)
    {
        var clamped = Math.Clamp(minuteOfDay, 0, (24 * 60) - 1);
        var local = DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(clamped / 60, clamped % 60)), DateTimeKind.Unspecified);
        for (var i = 0; i < 16 && tz.IsInvalidTime(local); i++)
        {
            local = local.AddMinutes(15);
        }

        var offset = tz.IsAmbiguousTime(local)
            ? tz.GetAmbiguousTimeOffsets(local).Max()
            : tz.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static bool TryFind(string id, out TimeZoneInfo? tz)
    {
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            tz = null;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            tz = null;
            return false;
        }
    }
}
