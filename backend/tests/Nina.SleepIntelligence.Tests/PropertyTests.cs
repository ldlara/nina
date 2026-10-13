using Nina.SleepIntelligence.Tests.Support;

namespace Nina.SleepIntelligence.Tests;

/// <summary>
/// Propriedades/invariantes do motor (test-strategy 4.1) com geração pseudoaleatória de semente fixa
/// (reprodutível: a mensagem de falha traz a semente).
/// </summary>
public sealed class PropertyTests
{
    private static readonly string[] Zones = [Tz.SaoPaulo, Tz.Lisbon, Tz.NewYork, Tz.Kolkata, Tz.LordHowe];
    private readonly SleepPredictionEngine _engine = Engines.Create();

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 250).Select(i => new object[] { i });

    private static (SleepPredictionRequest Request, DateTimeOffset AsOf, bool Clean) Scenario(int seed, bool allowGarbage)
    {
        var rnd = new Random(seed);
        var tz = Zones[rnd.Next(Zones.Length)];
        var asOf = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(rnd.Next(0, 365 * 1440));
        var age = rnd.Next(0, 1150);
        var today = TimeZones.LocalDate(asOf, Tz.Zone(tz));
        var birth = today.AddDays(-age);
        DateOnly? due = rnd.Next(4) == 0 ? birth.AddDays(rnd.Next(0, 120)) : null;

        var records = new List<SleepRecord>();
        var cursor = asOf.AddMinutes(-rnd.Next(5, 600));
        var count = rnd.Next(0, 90);
        for (var i = 0; i < count; i++)
        {
            var kind = rnd.Next(3) == 0 ? SleepKind.Night : SleepKind.Nap;
            var dur = kind == SleepKind.Night ? rnd.Next(300, 720) : rnd.Next(15, 150);
            var end = cursor;
            var start = end.AddMinutes(-dur);
            records.Add(new SleepRecord(NewGuid(rnd), start, end, kind, rnd.Next(5) == 0 ? Zones[rnd.Next(Zones.Length)] : tz));
            cursor = start.AddMinutes(-rnd.Next(30, 400));
        }

        var clean = true;
        if (allowGarbage)
        {
            var extras = rnd.Next(0, 12);
            for (var i = 0; i < extras; i++)
            {
                clean = false;
                var t = asOf.AddMinutes(-rnd.Next(-120, 6000));
                var kind = rnd.Next(2) == 0 ? SleepKind.Nap : SleepKind.Night;
                switch (rnd.Next(7))
                {
                    case 0: records.Add(new SleepRecord(NewGuid(rnd), t, t.AddSeconds(rnd.Next(0, 30)), kind)); break;
                    case 1: records.Add(new SleepRecord(NewGuid(rnd), t, t.AddHours(rnd.Next(15, 40)), kind)); break;
                    case 2: records.Add(new SleepRecord(NewGuid(rnd), t, t.AddMinutes(-rnd.Next(1, 300)), kind)); break;
                    case 3: records.Add(new SleepRecord(NewGuid(rnd), t, null, kind)); break;
                    case 4 when records.Count > 0: records.Add(records[rnd.Next(records.Count)]); break;
                    case 5 when records.Count > 0:
                        var o = records[rnd.Next(records.Count)];
                        records.Add(new SleepRecord(NewGuid(rnd), o.StartAt.AddMinutes(rnd.Next(-20, 20)), o.EndAt?.AddMinutes(rnd.Next(-20, 20)), o.Kind, "Not/AZone"));
                        break;
                    default: records.Add(new SleepRecord(NewGuid(rnd), t, t.AddMinutes(rnd.Next(10, 90)), kind, tz)); break;
                }
            }
        }

        SleepPreferences? prefs = null;
        if (rnd.Next(3) == 0)
        {
            var from = rnd.Next(18 * 60, 21 * 60);
            prefs = new SleepPreferences(
                rnd.Next(2) == 0 ? rnd.Next(0, 5) : null,
                TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(from)),
                TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(from + rnd.Next(20, 120))));
            if (rnd.Next(4) == 0)
            {
                prefs = prefs with { BedtimeFrom = null, BedtimeTo = null };
            }
        }

        return (new SleepPredictionRequest(new BabyProfile(birth, due, tz), records, prefs), asOf, clean);
    }

    private static Guid NewGuid(Random rnd)
    {
        var b = new byte[16];
        rnd.NextBytes(b);
        return new Guid(b);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void I1_determinism_and_I5_input_order_does_not_matter(int seed)
    {
        var (request, asOf, _) = Scenario(seed, allowGarbage: true);
        var expected = Json.Serialize(_engine.Predict(request, asOf));
        Assert.Equal(expected, Json.Serialize(_engine.Predict(request, asOf)));

        var rnd = new Random(seed);
        var shuffled = request with { History = request.History.OrderBy(_ => rnd.Next()).ToList() };
        Assert.Equal(expected, Json.Serialize(_engine.Predict(shuffled, asOf)));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void I2_the_engine_never_modifies_the_input_history(int seed)
    {
        var (request, asOf, _) = Scenario(seed, allowGarbage: true);
        var historyBefore = request.History.ToList();
        var babyBefore = request.Baby;
        var prefsBefore = request.Preferences;
        _ = _engine.Predict(request, asOf);
        Assert.Equal(historyBefore, request.History);
        Assert.Equal(babyBefore, request.Baby);
        Assert.Equal(prefsBefore, request.Preferences);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void I3_predictions_are_in_the_future_ordered_and_not_before_the_last_sleep_ended(int seed)
    {
        var (request, asOf, clean) = Scenario(seed, allowGarbage: seed % 2 == 0);
        var r = _engine.Predict(request, asOf);
        var lastEnd = request.History.Where(h => h.EndAt is { } e && e <= asOf).Select(h => h.EndAt!.Value).DefaultIfEmpty(DateTimeOffset.MinValue).Max();

        if (r.Status != PredictionStatus.Available)
        {
            Assert.Empty(r.Predictions);
            return;
        }

        foreach (var p in r.Predictions)
        {
            Assert.True(p.PredictedStart > asOf, $"seed {seed}: início {p.PredictedStart:o} <= asOf {asOf:o}");
            Assert.True(p.PredictedEnd > p.PredictedStart, $"seed {seed}: faixa vazia");
            Assert.True(p.PredictedStart >= lastEnd, $"seed {seed}: previsão antes do fim do último sono");
            Assert.Equal(0, p.PredictedStart.Offset.TotalMinutes); // saída em UTC
            Assert.Equal(0, p.PredictedStart.Second);
        }

        Assert.Equal(r.Predictions.OrderBy(p => p.PredictedStart).Select(p => p.Kind), r.Predictions.Select(p => p.Kind));
        Assert.Equal(r.Predictions.Count, r.Predictions.Select(p => p.Kind).Distinct().Count());
        if (r.Predictions.Count == 2)
        {
            Assert.True(r.Predictions[0].PredictedStart < r.Predictions[1].PredictedStart);
        }

        _ = clean;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void I4_I6_confidence_and_explanations_are_well_formed_and_non_diagnostic(int seed)
    {
        var (request, asOf, _) = Scenario(seed, allowGarbage: true);
        var r = _engine.Predict(request, asOf);
        var catalog = MessageCatalog.LoadDefault();
        var guard = new LanguageGuard();

        Assert.Equal(MessageKeys.Disclaimer, r.DisclaimerKey);
        foreach (var p in r.Predictions)
        {
            Assert.True(Enum.IsDefined(p.Confidence));
            Assert.InRange(p.ConfidenceScore, 0, 100);
            Assert.Contains(p.Explanation.Key, MessageKeys.All);
            var text = catalog.Render(p.Explanation);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain("{", text, StringComparison.Ordinal);
            Assert.Empty(guard.FindViolations(text));
            if (p.BaselineOnly)
            {
                Assert.Equal(ConfidenceLevel.Low, p.Confidence);
            }
        }

        if (r.StatusExplanation is { } se)
        {
            Assert.Contains(se.Key, MessageKeys.All);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Status_is_consistent_with_the_data(int seed)
    {
        var (request, asOf, _) = Scenario(seed, allowGarbage: true);
        var r = _engine.Predict(request, asOf);
        switch (r.Status)
        {
            case PredictionStatus.Sleeping:
                Assert.Empty(r.Predictions);
                Assert.Equal(MessageKeys.SleepingNow, r.StatusExplanation!.Key);
                break;
            case PredictionStatus.Available:
                Assert.NotEmpty(r.Predictions);
                Assert.Null(r.StatusExplanation);
                break;
            default:
                Assert.Empty(r.Predictions);
                Assert.NotNull(r.StatusExplanation);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Stability_a_one_minute_change_in_one_record_moves_predictions_only_a_little(int seed)
    {
        var (request, asOf, _) = Scenario(seed, allowGarbage: false);
        var closed = request.History.Where(h => h.EndAt is not null).ToList();
        if (closed.Count == 0)
        {
            return;
        }

        var rnd = new Random(seed);
        var target = closed[rnd.Next(closed.Count)];
        var perturbed = request with
        {
            History = request.History.Select(h => h.Id == target.Id ? h with { EndAt = h.EndAt!.Value.AddMinutes(1) } : h).ToList(),
        };

        var a = _engine.Predict(request, asOf);
        var b = _engine.Predict(perturbed, asOf);
        if (a.Status != PredictionStatus.Available || b.Status != PredictionStatus.Available)
        {
            return;
        }

        foreach (var pa in a.Predictions)
        {
            var pb = b.Predictions.FirstOrDefault(x => x.Kind == pa.Kind);
            if (pb is null)
            {
                continue; // a soneca pode entrar/sair da previsão na fronteira do corte; não é um salto de horário
            }

            var delta = Math.Abs((pb.PredictedStart - pa.PredictedStart).TotalMinutes);
            Assert.True(delta <= 20, $"seed {seed}: {pa.Kind} deslocou {delta} min com 1 min de alteração");
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_input_never_throws_even_with_garbage(int seed)
    {
        var (request, asOf, _) = Scenario(seed, allowGarbage: true);
        var ex = Record.Exception(() => _engine.Predict(request, asOf));
        Assert.Null(ex);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Message_params_are_stable_primitives(int seed)
    {
        var (request, asOf, _) = Scenario(seed, allowGarbage: false);
        var r = _engine.Predict(request, asOf);
        foreach (var p in r.Predictions)
        {
            Assert.Equal(p.Explanation.Params.Keys.OrderBy(k => k, StringComparer.Ordinal), p.Explanation.Params.Keys);
            Assert.All(p.Explanation.Params.Values, v => Assert.True(v is int or string or bool));
        }
    }
}
