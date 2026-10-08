using Nina.SleepIntelligence.Tests.Support;

namespace Nina.SleepIntelligence.Tests;

/// <summary>SE-ED-*: criar/editar/excluir evento passado = recalcular a partir do estado final, sem tocar nos eventos (RB-001/003).</summary>
public sealed class RetroactiveEditTests
{
    private readonly SleepPredictionEngine _engine = Engines.Create();

    private static (List<SleepRecord> Records, DateTimeOffset AsOf) Setup()
    {
        var baby = new SyntheticBaby().Days(new DateOnly(2026, 9, 26), 12, 2, 150, 60, 150, (19 * 60) + 30, 3, seed: 21);
        // hoje: acordou da noite e fez a 1ª soneca da manhã
        var today = baby.LastEnd.AddMinutes(150);
        baby.Add(today, today.AddMinutes(45), SleepKind.Nap);
        return (baby.Records, today.AddMinutes(45 + 20));
    }

    private SleepPredictionResult Run(IReadOnlyList<SleepRecord> records, DateTimeOffset asOf) =>
        _engine.Predict(Req.For(records, 270, asOf), asOf);

    [Fact]
    public void SE_ED_01_editing_the_end_of_the_last_nap_moves_the_next_prediction_and_changes_nothing_else()
    {
        var (records, asOf) = Setup();
        var before = Run(records, asOf);
        var last = records[^1];
        var edited = records.Select(r => r.Id == last.Id ? r with { EndAt = last.EndAt!.Value.AddMinutes(20) } : r).ToList();
        var after = Run(edited, asOf);

        Assert.Equal(20, (after.Nap().PredictedStart - before.Nap().PredictedStart).TotalMinutes);
        Assert.Equal(last.EndAt!.Value.AddMinutes(20), edited.Single(r => r.Id == last.Id).EndAt); // exatamente como informado
        Assert.Equal(records.Where(r => r.Id != last.Id), edited.Where(r => r.Id != last.Id)); // nenhum outro evento alterado
    }

    [Fact]
    public void SE_ED_02_deleting_a_nap_recalculates_without_it()
    {
        var (records, asOf) = Setup();
        var before = Run(records, asOf);
        var without = records.Take(records.Count - 1).ToList();
        var after = Run(without, asOf);
        Assert.NotEqual(before.InputsFingerprint, after.InputsFingerprint);
        Assert.Equal(before.Plan!.NapsCompletedToday - 1, after.Plan!.NapsCompletedToday);
    }

    [Fact]
    public void SE_ED_03_inserting_a_forgotten_nap_in_the_past_updates_neighbours_deterministically()
    {
        var (records, asOf) = Setup();
        var t = Tz.At(Tz.SaoPaulo, 2026, 10, 2, 12, 0);
        var withExtra = records.Append(new SleepRecord(Guid.NewGuid(), t, t.AddMinutes(30), SleepKind.Nap)).ToList();
        var a = Run(withExtra, asOf);
        var b = Run(withExtra.AsEnumerable().Reverse().ToList(), asOf);
        Assert.Equal(Json.Serialize(a), Json.Serialize(b));
        Assert.NotEqual(Run(records, asOf).InputsFingerprint, a.InputsFingerprint);
    }

    [Fact]
    public void SE_ED_04_an_edit_that_creates_an_overlap_leaves_a_consistent_state()
    {
        var (records, asOf) = Setup();
        var last = records[^1];
        var overlapping = records.Append(new SleepRecord(Guid.NewGuid(), last.StartAt.AddMinutes(10), last.EndAt!.Value.AddMinutes(10), SleepKind.Nap)).ToList();
        var r = Run(overlapping, asOf);
        Assert.Equal(Run(records, asOf).Plan!.NapsCompletedToday, r.Plan!.NapsCompletedToday);
        Assert.True(r.DataQuality.MergedOverlaps >= 1);
    }

    [Fact]
    public void SE_ED_05_editing_an_event_older_than_the_window_does_not_change_the_prediction()
    {
        var (records, asOf) = Setup();
        var old = new SleepRecord(Guid.NewGuid(), asOf.AddDays(-40), asOf.AddDays(-40).AddMinutes(60), SleepKind.Nap);
        var with40 = records.Append(old).ToList();
        var edited = records.Append(old with { EndAt = old.EndAt!.Value.AddMinutes(30) }).ToList();
        Assert.Equal(Json.Serialize(Run(with40, asOf).Predictions), Json.Serialize(Run(edited, asOf).Predictions));
    }

    [Fact]
    public void SE_ED_06_edit_and_revert_gives_the_identical_prediction()
    {
        var (records, asOf) = Setup();
        var original = Run(records, asOf);
        var last = records[^1];
        var changed = records.Select(r => r.Id == last.Id ? r with { EndAt = last.EndAt!.Value.AddMinutes(25) } : r).ToList();
        _ = Run(changed, asOf);
        var reverted = changed.Select(r => r.Id == last.Id ? last : r).ToList();
        Assert.Equal(Json.Serialize(original), Json.Serialize(Run(reverted, asOf)));
    }

    [Fact]
    public void SE_ED_07_result_does_not_depend_on_the_arrival_order_of_concurrent_edits()
    {
        var (records, asOf) = Setup();
        var rnd = new Random(7);
        var expected = Json.Serialize(Run(records, asOf));
        for (var i = 0; i < 20; i++)
        {
            var shuffled = records.OrderBy(_ => rnd.Next()).ToList();
            Assert.Equal(expected, Json.Serialize(Run(shuffled, asOf)));
        }
    }

    [Fact]
    public void SE_ED_08_recompute_is_idempotent_and_the_fingerprint_ignores_order_and_reference_time()
    {
        var (records, asOf) = Setup();
        var first = Run(records, asOf);
        var second = Run(records, asOf);
        Assert.Equal(Json.Serialize(first), Json.Serialize(second));
        var later = Run(records, asOf.AddMinutes(5));
        Assert.Equal(first.InputsFingerprint, later.InputsFingerprint);
        Assert.Equal(first.InputsFingerprint, Run(records.AsEnumerable().Reverse().ToList(), asOf).InputsFingerprint);
    }

    [Fact]
    public void Engine_uses_the_injected_clock_and_matches_the_explicit_reference_time()
    {
        var (records, asOf) = Setup();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(asOf);
        var engine = new SleepPredictionEngine(Engines.Table, null, clock);
        var request = Req.For(records, 270, asOf);
        Assert.Equal(Json.Serialize(_engine.Predict(request, asOf)), Json.Serialize(engine.Predict(request)));
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(asOf.AddMinutes(30), engine.Predict(request).ComputedAt);
    }

    [Fact]
    public void RB_001_a_prediction_is_a_distinct_type_that_never_carries_or_mutates_sleep_records()
    {
        var (records, asOf) = Setup();
        var snapshot = records.ToList();
        var result = Run(records, asOf);

        Assert.Equal(snapshot, records);
        Assert.DoesNotContain(typeof(SleepPredictionItem).GetProperties(), p => p.PropertyType == typeof(SleepRecord));
        Assert.False(typeof(SleepRecord).IsAssignableFrom(typeof(SleepPredictionItem)));
        Assert.All(result.Predictions, p => Assert.True(p.PredictedStart > asOf));
    }
}
