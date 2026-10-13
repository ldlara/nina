namespace Nina.SleepIntelligence;

/// <summary>Sessão fechada válida, já normalizada (UTC) e com o fuso do registro.</summary>
internal sealed record Session(DateTimeOffset Start, DateTimeOffset End, SleepKind Kind, TimeZoneInfo Zone)
{
    public int Minutes => Minutes_(Start, End);

    public static int Minutes_(DateTimeOffset from, DateTimeOffset to) => (int)Math.Floor((to - from).TotalMinutes);
}

internal sealed record OpenSession(DateTimeOffset Start, SleepKind Kind);

internal sealed record NormalizedHistory(
    IReadOnlyList<Session> Closed,
    OpenSession? Open,
    bool OpenIgnored,
    int Invalid,
    int TooShort,
    int TooLong,
    int Future,
    int MergedOverlaps);

/// <summary>
/// Valida, deduplica, ordena e unifica o histórico bruto. Nunca lança por dados ruins (SE-OUT-02) e nunca modifica a entrada (RB-001).
/// O resultado independe da ordem de entrada (I5).
/// </summary>
internal static class HistoryNormalizer
{
    public static NormalizedHistory Normalize(
        IReadOnlyList<SleepRecord> records,
        DateTimeOffset asOf,
        TimeZoneInfo babyZone,
        SleepEngineOptions opt)
    {
        var tolerance = TimeSpan.FromMinutes(opt.FutureToleranceMinutes);
        int invalid = 0, tooShort = 0, tooLong = 0, future = 0, merged = 0;

        // Dedup por Id com desempate determinístico (independe da ordem de chegada).
        var distinct = records
            .OrderBy(r => r.Id)
            .ThenBy(r => r.StartAt.UtcTicks)
            .ThenBy(r => r.EndAt?.UtcTicks ?? long.MaxValue)
            .ThenBy(r => r.Kind)
            .GroupBy(r => r.Id)
            .Select(g => g.First())
            .ToList();
        invalid += records.Count - distinct.Count;

        var closed = new List<Session>();
        var opens = new List<OpenSession>();
        foreach (var r in distinct)
        {
            var zone = TimeZones.TryResolve(r.TimeZoneId) ?? babyZone;
            if (r.EndAt is null)
            {
                if (r.StartAt > asOf + tolerance)
                {
                    future++;
                }
                else
                {
                    opens.Add(new OpenSession(r.StartAt.ToUniversalTime(), r.Kind));
                }

                continue;
            }

            var start = r.StartAt.ToUniversalTime();
            var end = r.EndAt.Value.ToUniversalTime();
            if (end <= start)
            {
                invalid++;
                continue;
            }

            if (end > asOf + tolerance)
            {
                future++;
                continue;
            }

            var minutes = Session.Minutes_(start, end);
            if (minutes < opt.MinValidSleepMinutes)
            {
                tooShort++;
                continue;
            }

            if (minutes > (r.Kind == SleepKind.Nap ? opt.MaxNapMinutes : opt.MaxNightMinutes))
            {
                tooLong++;
                continue;
            }

            closed.Add(new Session(start, end, r.Kind, zone));
        }

        // Sessão em aberto: a mais recente vale; as demais são inválidas. Timer esquecido é ignorado (SE-OUT-04).
        OpenSession? open = null;
        var openIgnored = false;
        if (opens.Count > 0)
        {
            var ordered = opens.OrderByDescending(o => o.Start).ToList();
            invalid += ordered.Count - 1;
            var candidate = ordered[0];
            var limit = candidate.Kind == SleepKind.Nap ? opt.OpenNapMaxMinutes : opt.OpenNightMaxMinutes;
            if ((asOf - candidate.Start).TotalMinutes > limit)
            {
                openIgnored = true;
            }
            else
            {
                open = candidate;
            }
        }

        var sorted = closed.OrderBy(s => s.Start.UtcTicks).ThenBy(s => s.End.UtcTicks).ThenBy(s => s.Kind).ToList();
        var result = new List<Session>();
        var gap = TimeSpan.FromMinutes(opt.MergeGapMinutes);
        foreach (var s in sorted)
        {
            if (result.Count == 0)
            {
                result.Add(s);
                continue;
            }

            var prev = result[^1];
            if (s.Start < prev.End)
            {
                merged++;
                if (s.Kind == prev.Kind)
                {
                    result[^1] = prev with { End = s.End > prev.End ? s.End : prev.End };
                }
                else if (s.End > prev.End)
                {
                    result.Add(s with { Start = prev.End });
                }

                // sessão de outro tipo totalmente contida: descartada (sobreposição, SE-OUT-05)
            }
            else if (s.Kind == prev.Kind && s.Start - prev.End < gap)
            {
                merged++;
                result[^1] = prev with { End = s.End };
            }
            else
            {
                result.Add(s);
            }
        }

        return new NormalizedHistory(result, open, openIgnored, invalid, tooShort, tooLong, future, merged);
    }
}
