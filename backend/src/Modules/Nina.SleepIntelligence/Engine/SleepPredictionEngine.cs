using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nina.SleepIntelligence;

/// <summary>
/// Motor determinístico de previsão de sono (ADR-0004; sem ML). Biblioteca pura: sem I/O, sem estado, relógio injetado.
/// Mesma entrada + mesmo instante de referência = mesma saída (RF-011-A3, RF-012-A6). Nunca altera o histórico (RB-001).
/// Especificação: <c>specs/sleep-engine-spec.md</c>.
/// </summary>
public sealed class SleepPredictionEngine : ISleepPredictionEngine
{
    private readonly ReferenceTable _table;
    private readonly SleepEngineOptions _opt;
    private readonly TimeProvider _clock;

    public SleepPredictionEngine(ReferenceTable table, SleepEngineOptions? options = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
        _opt = options ?? SleepEngineOptions.Default;
        _clock = clock ?? TimeProvider.System;
    }

    public SleepPredictionResult Predict(SleepPredictionRequest request) => Predict(request, _clock.GetUtcNow());

    public SleepPredictionResult Predict(SleepPredictionRequest request, DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(request);
        asOf = asOf.ToUniversalTime();
        var fingerprint = Fingerprint(request);
        var prefs = request.Preferences ?? SleepPreferences.None;

        var zone = TimeZones.TryResolve(request.Baby.TimeZoneId);
        if (zone is null)
        {
            return Insufficient(asOf, fingerprint, null, MessageKeys.TimezoneUnknown, EmptyQuality());
        }

        var today = TimeZones.LocalDate(asOf, zone);
        if (today < request.Baby.BirthDate)
        {
            return Insufficient(asOf, fingerprint, null, MessageKeys.AgeOutOfRange, EmptyQuality());
        }

        var age = AgeCalculator.Calculate(request.Baby.BirthDate, request.Baby.DueDate, today, _opt.AgeCorrection);
        var reference = _table.Resolve(age.EffectiveDays);
        var history = HistoryNormalizer.Normalize(request.History ?? [], asOf, zone, _opt);
        if (reference is null)
        {
            return Insufficient(asOf, fingerprint, age, MessageKeys.AgeOutOfRange, Quality(history, 0, 0, false));
        }

        var windowStart = asOf.AddDays(-_opt.HistoryWindowDays);
        var recent = history.Closed.Where(s => s.Start >= windowStart).ToList();
        var tzChanged = recent.Any(s => s.Zone.Id != zone.Id);

        if (history.Open is not null)
        {
            var sleepingQuality = Quality(history, 0, 0, tzChanged);
            return new SleepPredictionResult(
                PredictionStatus.Sleeping,
                SleepEngineInfo.DisclaimerKey,
                [],
                new Explanation(MessageKeys.SleepingNow, EmptyParams()),
                SleepEngineInfo.ModelVersion,
                _table.Version,
                _table.Hash,
                _table.IsDraft,
                asOf,
                fingerprint,
                age,
                null,
                sleepingQuality);
        }

        // --- wake windows observadas (RF-010-A3) ---
        var napObs = new List<Obs>();
        var preBedObs = new List<Obs>();
        var outliers = 0;
        for (var i = 1; i < history.Closed.Count; i++)
        {
            var prev = history.Closed[i - 1];
            var cur = history.Closed[i];
            if (cur.Start < windowStart)
            {
                continue;
            }

            var minutes = Session.Minutes_(prev.End, cur.Start);
            if (minutes <= 0)
            {
                continue;
            }

            var range = cur.Kind == SleepKind.Nap ? reference.NapWakeMinutes : reference.PreBedtimeWakeMinutes;
            var low = range.Min * _opt.OutlierLowPercentOfMin / 100;
            var high = range.Max * _opt.OutlierHighPercentOfMax / 100;
            if (minutes < low || minutes > high)
            {
                outliers++;
                continue;
            }

            var obs = new Obs(minutes, AgeDays(asOf, cur.Start), TimeZones.LocalDate(cur.Start, cur.Zone));
            (cur.Kind == SleepKind.Nap ? napObs : preBedObs).Add(obs);
        }

        var napWake = Personalize(napObs, reference.NapWakeMinutes.Typical, SlackLow(reference.NapWakeMinutes), SlackHigh(reference.NapWakeMinutes), spreadByMedian: true);

        // --- rotina: nº de sonecas (RF-014) ---
        var napsToday = history.Closed.Count(s => s.Kind == SleepKind.Nap && TimeZones.LocalDate(s.Start, zone) == today);
        var (expectedNaps, napSource) = ExpectedNaps(prefs, history, reference, today);
        var napCap = prefs.TargetNapCount ?? Math.Min(expectedNaps ?? reference.NapCount.Max, reference.NapCount.Max);
        var allowMoreNaps = napsToday < napCap;

        // --- âncora: fim do último sono fechado ---
        var lastEnd = history.Closed.Count > 0 ? history.Closed[^1].End : (DateTimeOffset?)null;
        var lastKind = history.Closed.Count > 0 ? history.Closed[^1].Kind : (SleepKind?)null;
        var staleLimit = TimeSpan.FromMinutes(reference.NapWakeMinutes.Max * _opt.StaleAnchorPercentOfMax / 100);
        var anchorAssumed = lastEnd is null || (asOf - lastEnd.Value) > staleLimit;
        var anchor = anchorAssumed ? asOf : lastEnd!.Value;
        var floor = Max(asOf.AddMinutes(_opt.MinLeadMinutes), lastEnd is { } le && !anchorAssumed ? le : asOf);

        // --- bedtime: faixa efetiva e âncora de horário ---
        var bedtimeRange = EffectiveBedtimeRange(prefs, reference);
        var bedtimeObs = BedtimeObservations(recent, zone, asOf);
        Personalized? bedtimeP = null;
        int? anchorMinute = null;
        if (bedtimeRange is { } range0)
        {
            var baselineBed = reference.Bedtime is { } b ? b.TypicalMinuteOfDay : (range0.From + range0.To) / 2;
            var lo = reference.Bedtime?.EarliestMinuteOfDay ?? range0.From;
            var hi = reference.Bedtime?.LatestMinuteOfDay ?? range0.To;
            bedtimeP = Personalize(bedtimeObs, baselineBed - 720, lo - 720, hi - 720, spreadByMedian: false);
            var minute = bedtimeP.Value + 720;
            anchorMinute = Math.Clamp(minute, range0.From, range0.To);
        }

        // --- previsões ---
        var items = new List<SleepPredictionItem>();
        SleepPredictionItem? nap = null;
        DateTimeOffset? bedLatestUtc = null;
        DateOnly bedDate = default;
        if (bedtimeRange is { } br)
        {
            var ref0 = lastEnd is not null && !anchorAssumed ? lastEnd.Value : asOf;
            bedDate = TimeZones.LocalDate(ref0, zone);
            var candidate = TimeZones.ToUtc(bedDate, anchorMinute!.Value, zone);
            if (lastEnd is { } l && candidate <= l)
            {
                bedDate = bedDate.AddDays(1);
            }

            bedLatestUtc = TimeZones.ToUtc(bedDate, br.To, zone);
        }

        if (allowMoreNaps)
        {
            nap = BuildNap(anchor, floor, napWake, napObs, reference, age, anchorAssumed, bedLatestUtc, recent);
            if (nap is not null)
            {
                items.Add(nap);
            }
        }

        if (bedtimeRange is { } bedRange && bedtimeP is not null)
        {
            items.Add(BuildBedtime(
                bedRange, anchorMinute!.Value, bedDate, zone, bedtimeP, bedtimeObs, preBedObs, reference, age, prefs, floor, nap,
                lastKind == SleepKind.Nap && lastEnd is { } e2 && !anchorAssumed && TimeZones.LocalDate(e2, zone) == today ? e2 : null,
                napsToday, recent, reference.PreBedtimeWakeMinutes));
        }

        var quality = Quality(history, napObs.Count + preBedObs.Count, outliers, tzChanged, DistinctDays(recent));
        var plan = new SchedulePlan(
            prefs.TargetNapCount ?? expectedNaps,
            napSource,
            napsToday,
            bedtimeRange?.From,
            bedtimeRange?.To,
            prefs.TargetNapCount is not null || prefs.HasBedtimeRange);

        var status = items.Count > 0 ? PredictionStatus.Available : PredictionStatus.InsufficientData;
        return new SleepPredictionResult(
            status,
            SleepEngineInfo.DisclaimerKey,
            items.OrderBy(i => i.PredictedStart).ThenBy(i => i.Kind).ToList(),
            items.Count > 0 ? null : new Explanation(MessageKeys.NapTargetReached, EmptyParams()),
            SleepEngineInfo.ModelVersion,
            _table.Version,
            _table.Hash,
            _table.IsDraft,
            asOf,
            fingerprint,
            age,
            plan,
            quality);
    }

    // ------------------------------------------------------------------ nap

    private SleepPredictionItem? BuildNap(
        DateTimeOffset anchor,
        DateTimeOffset floor,
        Personalized wake,
        List<Obs> napObs,
        AgeReference reference,
        AgeCalculation age,
        bool anchorAssumed,
        DateTimeOffset? bedLatestUtc,
        List<Session> recent)
    {
        var baselineOnly = wake.WeightPercent == 0;
        var conf = ConfidenceEvaluator.Evaluate(
            new ConfidenceInput(napObs.Count, napObs.Select(o => o.LocalDate).Distinct().Count(), wake.SpreadPercent, wake.NewestAgeDays, baselineOnly),
            _opt);

        var half = HalfWidth((reference.NapWakeMinutes.Max - reference.NapWakeMinutes.Min) / 2, conf.Level);
        var center = anchor.AddMinutes(wake.Value);
        var start = Max(center.AddMinutes(-half), floor);
        var end = Max(start.AddMinutes(_opt.MinWindowMinutes), center.AddMinutes(half));
        start = CeilMinute(start);
        end = CeilMinute(end);

        // Soneca que empurraria o bedtime além do limite da faixa não é prevista.
        if (bedLatestUtc is { } latest
            && end.AddMinutes(reference.NapDurationMinutes.Typical + reference.PreBedtimeWakeMinutes.Min) > latest)
        {
            return null;
        }

        var p = EmptyParams();
        p["age_basis"] = age.Basis;
        p["age_months"] = AgeMonths(age);
        p["anchor_assumed"] = anchorAssumed;
        p["typical_wake_minutes"] = wake.Value;
        string key;
        if (baselineOnly)
        {
            key = MessageKeys.AgeOnly;
        }
        else
        {
            p["days_used"] = DistinctDays(recent);
            p["records_used"] = recent.Count;
            key = conf.Irregular ? MessageKeys.IrregularHistory : MessageKeys.HistoryAndAge;
        }

        return new SleepPredictionItem(PredictionKind.NextNap, start, end, conf.Level, conf.Score, new Explanation(key, p), baselineOnly);
    }

    // -------------------------------------------------------------- bedtime

    private SleepPredictionItem BuildBedtime(
        MinuteRange range,
        int anchorMinute,
        DateOnly bedDate,
        TimeZoneInfo zone,
        Personalized bedtimeP,
        List<Obs> bedtimeObs,
        List<Obs> preBedObs,
        AgeReference reference,
        AgeCalculation age,
        SleepPreferences prefs,
        DateTimeOffset floor,
        SleepPredictionItem? nap,
        DateTimeOffset? lastNapEnd,
        int napsToday,
        List<Session> recent,
        MinutesRange preBedRange)
    {
        var baselineOnly = bedtimeP.WeightPercent == 0;
        var conf = ConfidenceEvaluator.Evaluate(
            new ConfidenceInput(bedtimeObs.Count, bedtimeObs.Select(o => o.LocalDate).Distinct().Count(), bedtimeP.SpreadPercent, bedtimeP.NewestAgeDays, baselineOnly),
            _opt);

        var earliestUtc = TimeZones.ToUtc(bedDate, range.From, zone);
        var latestUtc = TimeZones.ToUtc(bedDate, range.To, zone);
        var center = TimeZones.ToUtc(bedDate, anchorMinute, zone);

        var preBed = Personalize(preBedObs, preBedRange.Typical, SlackLow(preBedRange), SlackHigh(preBedRange), spreadByMedian: true);
        if (nap is null && lastNapEnd is { } napEnd)
        {
            // Sem mais sonecas hoje: combina o horário habitual com "fim da última soneca + wake window final".
            var byWake = napEnd.AddMinutes(preBed.Value);
            var diff = (int)(byWake - center).TotalMinutes;
            center = center.AddMinutes(diff / 2);
        }

        if (nap is not null)
        {
            var minAfterNap = nap.PredictedEnd!.Value.AddMinutes(reference.NapDurationMinutes.Typical + preBedRange.Min);
            center = Max(center, minAfterNap);
        }

        center = Min(Max(center, earliestUtc), latestUtc);
        var half = HalfWidth((range.To - range.From) / 2, conf.Level);
        var start = Max(center.AddMinutes(-half), earliestUtc);
        var end = Min(center.AddMinutes(half), latestUtc);
        if (end < start.AddMinutes(_opt.MinWindowMinutes))
        {
            // Faixa estreita (ex.: preferência de 15-30 min): alarga para trás antes de passar do limite superior.
            start = Max(earliestUtc, end.AddMinutes(-_opt.MinWindowMinutes));
            if (end < start.AddMinutes(_opt.MinWindowMinutes))
            {
                end = start.AddMinutes(_opt.MinWindowMinutes);
            }
        }

        // Restrições físicas vencem a preferência: nunca antes de agora, do fim do último sono ou da soneca prevista.
        start = Max(start, floor);
        if (nap?.PredictedEnd is { } ne)
        {
            start = Max(start, ne);
        }

        end = Max(end, start.AddMinutes(_opt.MinWindowMinutes));
        start = CeilMinute(start);
        end = CeilMinute(end);

        var p = EmptyParams();
        p["age_basis"] = age.Basis;
        p["age_months"] = AgeMonths(age);
        p["naps_today"] = napsToday;
        string key;
        if (prefs.HasBedtimeRange)
        {
            key = MessageKeys.BedtimePreference;
            p["preference_range_applied"] = true;
        }
        else if (baselineOnly)
        {
            key = MessageKeys.BedtimeAgeOnly;
        }
        else
        {
            key = MessageKeys.BedtimeHistoryAndAge;
        }

        p["typical_bedtime_local"] = FormatMinute(Math.Clamp(bedtimeP.Value + 720, range.From, range.To));
        if (!baselineOnly)
        {
            p["days_used"] = DistinctDays(recent);
            p["records_used"] = bedtimeObs.Count;
        }

        return new SleepPredictionItem(PredictionKind.Bedtime, start, end, conf.Level, conf.Score, new Explanation(key, p), baselineOnly);
    }

    // -------------------------------------------------------------- helpers

    private int HalfWidth(int halfAmplitude, ConfidenceLevel level)
    {
        var pct = level switch
        {
            ConfidenceLevel.High => _opt.WindowPercentHigh,
            ConfidenceLevel.Medium => _opt.WindowPercentMedium,
            _ => _opt.WindowPercentLow,
        };
        return Math.Max(_opt.MinWindowMinutes / 2, halfAmplitude * pct / 100);
    }

    private int SlackLow(MinutesRange r) => r.Min * (100 - _opt.PersonalizationSlackPercent) / 100;

    private int SlackHigh(MinutesRange r) => r.Max * (100 + _opt.PersonalizationSlackPercent) / 100;

    private Personalized Personalize(List<Obs> obs, int baseline, int low, int high, bool spreadByMedian)
    {
        var n = obs.Count;
        var distinct = obs.Select(o => o.LocalDate).Distinct().Count();
        var newest = n == 0 ? int.MaxValue : obs.Min(o => o.AgeDays);
        if (n < _opt.MinObservationsForPersonalization)
        {
            return new Personalized(baseline, 0, n, 0, distinct, newest);
        }

        var items = obs.Select(o => (o.Value, Statistics.RecencyWeight(o.AgeDays))).ToList();
        var median = Statistics.WeightedMedian(items);
        var mad = Statistics.MeanAbsoluteDeviation(obs.Select(o => o.Value).ToList(), median); // sem peso de recência: mede a regularidade da janela inteira
        var spread = spreadByMedian ? mad * 100 / Math.Max(1, Math.Abs(median)) : mad * 100 / 120;

        var span = Math.Max(1, _opt.FullPersonalizationObservations - _opt.MinObservationsForPersonalization);
        var progress = Math.Clamp(n - _opt.MinObservationsForPersonalization, 0, span);
        var weight = _opt.MinPersonalWeightPercent + ((_opt.MaxPersonalWeightPercent - _opt.MinPersonalWeightPercent) * progress / span);
        var blended = ((weight * median) + ((100 - weight) * baseline) + 50) / 100;
        return new Personalized(Math.Clamp(blended, low, high), weight, n, spread, distinct, newest);
    }

    private static (int? Expected, string Source) ExpectedNaps(
        SleepPreferences prefs, NormalizedHistory history, AgeReference reference, DateOnly today)
    {
        if (prefs.TargetNapCount is { } t)
        {
            return (t, "PREFERENCE");
        }

        var counts = history.Closed
            .Select(s => (Date: TimeZones.LocalDate(s.Start, s.Zone), s.Kind))
            .Where(x => x.Date < today && x.Date >= today.AddDays(-7))
            .GroupBy(x => x.Date)
            .Select(g => g.Count(x => x.Kind == SleepKind.Nap))
            .ToList();
        if (counts.Count >= 3)
        {
            return (Math.Clamp(Statistics.Median(counts), reference.NapCount.Min, reference.NapCount.Max), "HISTORY");
        }

        return (reference.NapCount.Typical, "AGE");
    }

    private static MinuteRange? EffectiveBedtimeRange(SleepPreferences prefs, AgeReference reference)
    {
        if (prefs.HasBedtimeRange)
        {
            var from = (prefs.BedtimeFrom!.Value.Hour * 60) + prefs.BedtimeFrom.Value.Minute;
            var to = (prefs.BedtimeTo!.Value.Hour * 60) + prefs.BedtimeTo.Value.Minute;
            if (from < to)
            {
                return new MinuteRange(from, to);
            }
        }

        return reference.Bedtime is { } b ? new MinuteRange(b.EarliestMinuteOfDay, b.LatestMinuteOfDay) : null;
    }

    private List<Obs> BedtimeObservations(List<Session> recent, TimeZoneInfo zone, DateTimeOffset asOf)
    {
        var lo = _table.Plausibility.BedtimeEarliestMinuteOfDay - 60 - 720;
        var hi = _table.Plausibility.BedtimeLatestMinuteOfDay + 90 - 720;
        var list = new List<Obs>();
        foreach (var s in recent)
        {
            // Só noites registradas no fuso vigente: hábitos de outro fuso não são transferidos (adaptação imediata, SE-TZ-02).
            if (s.Kind != SleepKind.Night || s.Zone.Id != zone.Id)
            {
                continue;
            }

            var sinceNoon = (TimeZones.LocalMinuteOfDay(s.Start, s.Zone) - 720 + 1440) % 1440;
            if (sinceNoon < lo || sinceNoon > hi)
            {
                continue;
            }

            list.Add(new Obs(sinceNoon, AgeDays(asOf, s.Start), TimeZones.LocalDate(s.Start, s.Zone)));
        }

        return list;
    }

    private static int DistinctDays(List<Session> sessions) =>
        sessions.Select(s => TimeZones.LocalDate(s.Start, s.Zone)).Distinct().Count();

    private static int AgeDays(DateTimeOffset asOf, DateTimeOffset at) => Math.Max(0, (int)((asOf - at).TotalMinutes / 1440));

    private static int AgeMonths(AgeCalculation age) => (int)(age.EffectiveDays * 10000L / 304375);

    private static string FormatMinute(int minuteOfDay) =>
        string.Create(CultureInfo.InvariantCulture, $"{minuteOfDay / 60:00}:{minuteOfDay % 60:00}");

    private static DateTimeOffset CeilMinute(DateTimeOffset t)
    {
        var ticks = t.UtcTicks;
        var rem = ticks % TimeSpan.TicksPerMinute;
        return rem == 0 ? t.ToUniversalTime() : new DateTimeOffset(ticks + (TimeSpan.TicksPerMinute - rem), TimeSpan.Zero);
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private static SortedDictionary<string, object> EmptyParams() => new(StringComparer.Ordinal);

    private static DataQualityReport EmptyQuality() => new(0, 0, 0, 0, 0, 0, false, false, 0, 0, 0);

    private static DataQualityReport Quality(NormalizedHistory h, int observations, int outliers, bool tzChanged, int days = 0) =>
        new(h.Closed.Count, h.Invalid, h.TooShort, h.TooLong, h.Future, h.MergedOverlaps, h.OpenIgnored, tzChanged, observations, outliers, days);

    private SleepPredictionResult Insufficient(DateTimeOffset asOf, string fingerprint, AgeCalculation? age, string key, DataQualityReport quality) =>
        new(
            PredictionStatus.InsufficientData,
            SleepEngineInfo.DisclaimerKey,
            [],
            new Explanation(key, EmptyParams()),
            SleepEngineInfo.ModelVersion,
            _table.Version,
            _table.Hash,
            _table.IsDraft,
            asOf,
            fingerprint,
            age,
            null,
            quality);

    /// <summary>
    /// Impressão digital estável da entrada (independe da ordem dos eventos e do instante de referência): permite ao Tracking
    /// detectar recálculo sem mudança e manter a idempotência (RF-012-A6, SE-ED-08).
    /// </summary>
    internal static string Fingerprint(SleepPredictionRequest r)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"v1|{r.Baby.BirthDate:yyyy-MM-dd}|{r.Baby.DueDate:yyyy-MM-dd}|{r.Baby.TimeZoneId}\n");
        var p = r.Preferences ?? SleepPreferences.None;
        sb.Append(CultureInfo.InvariantCulture, $"p|{p.TargetNapCount}|{p.BedtimeFrom:HH:mm}|{p.BedtimeTo:HH:mm}\n");
        foreach (var s in (r.History ?? []).OrderBy(x => x.Id).ThenBy(x => x.StartAt.UtcTicks))
        {
            sb.Append(CultureInfo.InvariantCulture, $"s|{s.Id}|{s.StartAt.UtcTicks}|{s.EndAt?.UtcTicks}|{(int)s.Kind}|{s.TimeZoneId}\n");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant()[..16];
    }

    private sealed record Obs(int Value, int AgeDays, DateOnly LocalDate);

    private sealed record Personalized(int Value, int WeightPercent, int Observations, int SpreadPercent, int DistinctDays, int NewestAgeDays);

    private readonly record struct MinuteRange(int From, int To);
}
