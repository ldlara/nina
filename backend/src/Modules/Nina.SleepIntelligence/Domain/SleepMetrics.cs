namespace Nina.SleepIntelligence;

/// <summary>Wake window observada entre duas sessões consecutivas (RF-010-A3).</summary>
public sealed record WakeWindow(DateTimeOffset From, DateTimeOffset To, int Minutes, SleepKind NextKind);

/// <summary>Totais de um dia local do bebê.</summary>
public sealed record DailySleepTotal(DateOnly LocalDate, int NapCount, int NapMinutes, int NightMinutes, int NightSessions);

/// <summary>Política de "acompanhamento suficiente" de despertares (parâmetro editável, ADR-0009).</summary>
public sealed record NightAwakeningsPolicy
{
    public int MinNightMinutes { get; init; } = 180;

    public int LookbackNights { get; init; } = 14;

    public int MinWakeSeconds { get; init; } = 60;

    public static NightAwakeningsPolicy Default { get; } = new();
}

/// <summary>Métricas derivadas (RF-010). Sempre recomputáveis a partir dos eventos (INV-04); nada é persistido.</summary>
public static class SleepMetrics
{
    /// <summary>
    /// Wake windows entre sessões fechadas válidas, em minutos por diferença de instantes UTC (correto em dias de 23/25 h).
    /// Sessão em aberto não entra (RF-010-A6).
    /// </summary>
    public static IReadOnlyList<WakeWindow> WakeWindows(IReadOnlyList<SleepRecord> history, DateTimeOffset asOf, SleepEngineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        var norm = HistoryNormalizer.Normalize(history, asOf.ToUniversalTime(), TimeZoneInfo.Utc, options ?? SleepEngineOptions.Default);
        var list = new List<WakeWindow>();
        for (var i = 1; i < norm.Closed.Count; i++)
        {
            var minutes = Session.Minutes_(norm.Closed[i - 1].End, norm.Closed[i].Start);
            if (minutes > 0)
            {
                list.Add(new WakeWindow(norm.Closed[i - 1].End, norm.Closed[i].Start, minutes, norm.Closed[i].Kind));
            }
        }

        return list;
    }

    /// <summary>
    /// Totais por dia local. Regra provisória (D-13): a sessão pertence ao dia local do seu <b>início</b>, no fuso do registro
    /// (RF-010-A8: fuso vigente na data do evento); uma noite que cruza a meia-noite conta inteira no dia em que começou.
    /// </summary>
    public static IReadOnlyList<DailySleepTotal> DailyTotals(
        IReadOnlyList<SleepRecord> history, string babyTimeZoneId, DateTimeOffset asOf, SleepEngineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        var zone = TimeZones.TryResolve(babyTimeZoneId) ?? TimeZoneInfo.Utc;
        var norm = HistoryNormalizer.Normalize(history, asOf.ToUniversalTime(), zone, options ?? SleepEngineOptions.Default);
        return norm.Closed
            .GroupBy(s => TimeZones.LocalDate(s.Start, s.Zone))
            .OrderBy(g => g.Key)
            .Select(g => new DailySleepTotal(
                g.Key,
                g.Count(s => s.Kind == SleepKind.Nap),
                g.Where(s => s.Kind == SleepKind.Nap).Sum(s => s.Minutes),
                g.Where(s => s.Kind == SleepKind.Night).Sum(s => s.Minutes),
                g.Count(s => s.Kind == SleepKind.Night)))
            .ToList();
    }

    /// <summary>
    /// Despertares noturnos derivados (ADR-0009): <c>null</c> = não se aplica ou dados insuficientes; <c>0</c> = acompanhamento
    /// suficiente e nenhum despertar; <c>N</c> = quantidade. "Suficiente" = noite fechada com duração mínima e o cuidador já
    /// registrou despertares em alguma das últimas noites (<paramref name="trackingActive"/>, ver <see cref="IsAwakeningTrackingActive"/>).
    /// </summary>
    public static int? NightAwakenings(
        SleepRecord night, IReadOnlyList<WakeEventRecord> wakeEvents, bool trackingActive, NightAwakeningsPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(night);
        ArgumentNullException.ThrowIfNull(wakeEvents);
        var p = policy ?? NightAwakeningsPolicy.Default;
        if (night.Kind != SleepKind.Night || night.EndAt is not { } end || !trackingActive)
        {
            return null;
        }

        if ((end - night.StartAt).TotalMinutes < p.MinNightMinutes)
        {
            return null;
        }

        return wakeEvents.Count(w =>
            w.SleepRecordId == night.Id
            && w.StartedAt >= night.StartAt
            && w.EndedAt <= end
            && (w.EndedAt - w.StartedAt).TotalSeconds >= p.MinWakeSeconds);
    }

    /// <summary>Verdadeiro se houve ao menos um despertar registrado nas últimas noites (o recurso está em uso).</summary>
    public static bool IsAwakeningTrackingActive(
        IReadOnlyList<SleepRecord> history, IReadOnlyList<WakeEventRecord> wakeEvents, NightAwakeningsPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(wakeEvents);
        var p = policy ?? NightAwakeningsPolicy.Default;
        var recentNights = history
            .Where(s => s.Kind == SleepKind.Night && s.EndAt is not null)
            .OrderByDescending(s => s.StartAt.UtcTicks)
            .ThenBy(s => s.Id)
            .Take(p.LookbackNights)
            .Select(s => s.Id)
            .ToHashSet();
        return wakeEvents.Any(w => recentNights.Contains(w.SleepRecordId));
    }
}
