using Nina.SleepIntelligence.Tests.Support;

namespace Nina.SleepIntelligence.Tests;

/// <summary>SE-OUT-*: dados ruins nunca quebram o motor nem distorcem a previsão.</summary>
public sealed class OutlierTests
{
    private readonly SleepPredictionEngine _engine = Engines.Create();

    private static (SyntheticBaby Baby, DateTimeOffset AsOf) Baseline()
    {
        var baby = new SyntheticBaby().Days(new DateOnly(2026, 9, 26), 12, 2, 150, 60, 150, (19 * 60) + 30, 3, seed: 21);
        return (baby, baby.LastEnd.AddMinutes(20));
    }

    private SleepPredictionResult Run(IEnumerable<SleepRecord> records, DateTimeOffset asOf) =>
        _engine.Predict(Req.For(records, 270, asOf), asOf);

    [Fact]
    public void SE_OUT_01_accidental_touch_of_8_seconds_or_zero_minutes_is_discarded()
    {
        var (baby, asOf) = Baseline();
        var clean = Run(baby.Records, asOf);
        var noisy = baby.Records.ToList();
        var t = Tz.At(Tz.SaoPaulo, 2026, 10, 3, 11);
        noisy.Add(new SleepRecord(Guid.NewGuid(), t, t.AddSeconds(8), SleepKind.Nap));
        noisy.Add(new SleepRecord(Guid.NewGuid(), t.AddHours(1), t.AddHours(1), SleepKind.Nap));

        var r = Run(noisy, asOf);
        Assert.Equal(clean.Nap().PredictedStart, r.Nap().PredictedStart);
        Assert.Equal(clean.Nap().PredictedEnd, r.Nap().PredictedEnd);
        Assert.Equal(1, r.DataQuality.IgnoredTooShort);
        Assert.Equal(1, r.DataQuality.IgnoredInvalid);
    }

    [Fact]
    public void SE_OUT_02_fourteen_hour_nap_and_negative_duration_never_break_the_engine()
    {
        var (baby, asOf) = Baseline();
        var clean = Run(baby.Records, asOf);
        var noisy = baby.Records.ToList();
        var t = Tz.At(Tz.SaoPaulo, 2026, 10, 4, 6);
        noisy.Add(new SleepRecord(Guid.NewGuid(), t, t.AddHours(14), SleepKind.Nap));
        noisy.Add(new SleepRecord(Guid.NewGuid(), t.AddHours(2), t.AddHours(1), SleepKind.Nap)); // fim antes do início

        var r = Run(noisy, asOf);
        Assert.Equal(PredictionStatus.Available, r.Status);
        Assert.Equal(clean.Nap().PredictedStart, r.Nap().PredictedStart);
        Assert.Equal(1, r.DataQuality.IgnoredTooLong);
        Assert.Equal(1, r.DataQuality.IgnoredInvalid);
    }

    [Fact]
    public void SE_OUT_03_one_atypical_day_between_normal_days_barely_moves_the_prediction()
    {
        var (baby, asOf) = Baseline();
        var clean = Run(baby.Records, asOf);
        // dia de vacina: sonecas colapsadas com janelas de 40 min e 6 h
        var noisy = baby.Records.ToList();
        var t = Tz.At(Tz.SaoPaulo, 2026, 10, 2, 7);
        noisy.RemoveAll(x => x.Kind == SleepKind.Nap && x.StartAt >= Tz.At(Tz.SaoPaulo, 2026, 10, 2, 0) && x.StartAt < Tz.At(Tz.SaoPaulo, 2026, 10, 3, 0));
        noisy.Add(new SleepRecord(Guid.NewGuid(), t.AddMinutes(40), t.AddMinutes(60), SleepKind.Nap));
        noisy.Add(new SleepRecord(Guid.NewGuid(), t.AddHours(6).AddMinutes(30), t.AddHours(7), SleepKind.Nap));

        var r = Run(noisy, asOf);
        var delta = Math.Abs((r.Nap().PredictedStart - clean.Nap().PredictedStart).TotalMinutes);
        Assert.True(delta <= 10, $"deslocamento {delta} min");
    }

    [Fact]
    public void SE_OUT_04_forgotten_timer_is_ignored_and_flagged_but_a_fresh_one_means_sleeping()
    {
        var (baby, asOf) = Baseline();
        var forgotten = baby.Records.ToList();
        forgotten.Add(new SleepRecord(Guid.NewGuid(), asOf.AddHours(-20), null, SleepKind.Nap));
        var r = Run(forgotten, asOf);
        Assert.Equal(PredictionStatus.Available, r.Status);
        Assert.True(r.DataQuality.OpenSessionIgnored);

        var fresh = baby.Records.ToList();
        fresh.Add(new SleepRecord(Guid.NewGuid(), asOf.AddMinutes(-15), null, SleepKind.Nap));
        var s = Run(fresh, asOf);
        Assert.Equal(PredictionStatus.Sleeping, s.Status);
        Assert.Empty(s.Predictions);
        Assert.Equal(MessageKeys.SleepingNow, s.StatusExplanation!.Key);
        Assert.False(s.DataQuality.OpenSessionIgnored);
    }

    [Fact]
    public void SE_OUT_05_the_same_nap_registered_twice_does_not_double_the_count()
    {
        var (baby, _) = Baseline();
        var nap1 = Tz.At(Tz.SaoPaulo, 2026, 10, 9, 9, 0);
        var nap2 = Tz.At(Tz.SaoPaulo, 2026, 10, 9, 12, 30);
        var records = baby.Records.ToList();
        records.Add(new SleepRecord(Guid.NewGuid(), nap1, nap1.AddMinutes(60), SleepKind.Nap));
        records.Add(new SleepRecord(Guid.NewGuid(), nap1.AddMinutes(2), nap1.AddMinutes(58), SleepKind.Nap)); // outro cuidador
        records.Add(new SleepRecord(Guid.NewGuid(), nap2, nap2.AddMinutes(45), SleepKind.Nap));
        var asOf = nap2.AddMinutes(60);

        var r = Run(records, asOf);
        Assert.Equal(2, r.Plan!.NapsCompletedToday);
        Assert.True(r.DataQuality.MergedOverlaps >= 1);
        Assert.DoesNotContain(r.Predictions, p => p.Kind == PredictionKind.NextNap); // meta histórica = 2
    }

    [Fact]
    public void SE_OUT_06_chaotic_schedule_lowers_confidence()
    {
        var baby = new SyntheticBaby().Days(new DateOnly(2026, 9, 26), 12, 2, 150, 60, 150, (19 * 60) + 30, 90, seed: 8);
        var asOf = baby.LastEnd.AddMinutes(20);
        var chaotic = Run(baby.Records, asOf);
        var (regular, regularAsOf) = Baseline();
        Assert.True(chaotic.Nap().Confidence < Run(regular.Records, regularAsOf).Nap().Confidence
            || chaotic.Nap().ConfidenceScore < Run(regular.Records, regularAsOf).Nap().ConfidenceScore);
        Assert.NotEqual(ConfidenceLevel.High, chaotic.Nap().Confidence);
    }

    [Fact]
    public void SE_OUT_07_five_day_gap_in_recording_keeps_working_with_older_window_weights()
    {
        var baby = new SyntheticBaby().Days(new DateOnly(2026, 9, 26), 12, 2, 150, 60, 150, (19 * 60) + 30, 3, seed: 21);
        var asOf = baby.LastEnd.AddDays(5);
        var r = Run(baby.Records, asOf);
        Assert.Equal(PredictionStatus.Available, r.Status);
        Assert.Equal(true, r.Nap().Explanation.Params["anchor_assumed"]);
        Assert.True(r.Nap().PredictedStart > asOf);
        // recência baixa => confiança não pode ser alta
        Assert.NotEqual(ConfidenceLevel.High, r.Nap().Confidence);
    }

    [Fact]
    public void SE_OUT_08_never_predicts_beyond_the_table_ceiling_of_naps_per_day()
    {
        var t = Tz.At(Tz.SaoPaulo, 2026, 10, 8, 6);
        var baby = new SyntheticBaby();
        for (var i = 0; i < 3; i++) // teto da faixa 8m-11m é 3
        {
            baby.Add(t.AddMinutes(150), t.AddMinutes(190), SleepKind.Nap);
            t = t.AddMinutes(190);
        }

        var asOf = t.AddMinutes(10);
        var r = Run(baby.Records, asOf);
        Assert.DoesNotContain(r.Predictions, p => p.Kind == PredictionKind.NextNap);
        Assert.Equal(3, r.Plan!.NapsCompletedToday);
    }

    [Fact]
    public void Future_dated_records_are_ignored()
    {
        var (baby, asOf) = Baseline();
        var records = baby.Records.ToList();
        records.Add(new SleepRecord(Guid.NewGuid(), asOf.AddHours(2), asOf.AddHours(3), SleepKind.Nap));
        records.Add(new SleepRecord(Guid.NewGuid(), asOf.AddHours(2), null, SleepKind.Nap));
        var r = Run(records, asOf);
        Assert.Equal(2, r.DataQuality.IgnoredFuture);
        Assert.Equal(PredictionStatus.Available, r.Status);
    }

    [Fact]
    public void Duplicate_ids_are_counted_once()
    {
        var (baby, asOf) = Baseline();
        var records = baby.Records.Concat(baby.Records.Take(5)).ToList();
        var r = Run(records, asOf);
        Assert.Equal(5, r.DataQuality.IgnoredInvalid);
        Assert.Equal(Run(baby.Records, asOf).Nap().PredictedStart, r.Nap().PredictedStart);
    }

    [Fact]
    public void Adjacent_sessions_of_the_same_kind_with_a_tiny_gap_are_merged()
    {
        var t = Tz.At(Tz.SaoPaulo, 2026, 10, 8, 9);
        var baby = new SyntheticBaby()
            .Add(t, t.AddMinutes(20), SleepKind.Nap)
            .Add(t.AddMinutes(25), t.AddMinutes(60), SleepKind.Nap);
        var r = Run(baby.Records, t.AddMinutes(90));
        Assert.Equal(1, r.Plan!.NapsCompletedToday);
        Assert.Equal(1, r.DataQuality.MergedOverlaps);
    }
}
