using Nina.SleepIntelligence.Tests.Support;

namespace Nina.SleepIntelligence.Tests;

/// <summary>SE-CS-*: sem histórico (ou com pouco), usa só a referência por idade com confiança baixa.</summary>
public sealed class ColdStartTests
{
    private static readonly DateTimeOffset AsOf = Tz.At(Tz.SaoPaulo, 2026, 10, 8, 10);
    private readonly SleepPredictionEngine _engine = Engines.Create();

    [Fact]
    public void SE_CS_01_no_history_uses_age_baseline_with_low_confidence()
    {
        var r = _engine.Predict(Req.For([], 270, AsOf), AsOf);

        Assert.Equal(PredictionStatus.Available, r.Status);
        Assert.Equal(2, r.Predictions.Count);
        Assert.All(r.Predictions, p =>
        {
            Assert.True(p.BaselineOnly);
            Assert.Equal(ConfidenceLevel.Low, p.Confidence);
        });
        Assert.Equal(MessageKeys.AgeOnly, r.Nap().Explanation.Key);
        Assert.Equal(MessageKeys.BedtimeAgeOnly, r.Bed().Explanation.Key);
        Assert.Equal(8, r.Nap().Explanation.Params["age_months"]);
        Assert.Equal("CHRONOLOGICAL", r.Nap().Explanation.Params["age_basis"]);
        Assert.Equal(165, r.Nap().Explanation.Params["typical_wake_minutes"]);
        Assert.Equal(true, r.Nap().Explanation.Params["anchor_assumed"]);
        Assert.Equal(MessageKeys.Disclaimer, r.DisclaimerKey);
        Assert.Equal(SleepEngineInfo.ModelVersion, r.ModelVersion);
        Assert.Equal(Engines.Table.Hash, r.ReferenceTableHash);
        Assert.True(r.ReferenceTableIsDraft);
    }

    [Fact]
    public void SE_CS_01_baseline_windows_match_the_table()
    {
        var r = _engine.Predict(Req.For([], 270, AsOf), AsOf);
        var nap = r.Nap();
        // sem âncora real: assume acordado agora; faixa centrada em agora + 165 min (12:45 local), meia-largura 18 min
        Assert.Equal(Tz.At(Tz.SaoPaulo, 2026, 10, 8, 12, 27), nap.PredictedStart);
        Assert.Equal(Tz.At(Tz.SaoPaulo, 2026, 10, 8, 13, 3), nap.PredictedEnd);
        var bed = r.Bed();
        Assert.InRange(bed.PredictedStart.Local(), (18 * 60) + 30, (20 * 60) + 45);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SE_CS_02_a_few_naps_stay_baseline_with_low_confidence(int sessions)
    {
        var baby = new SyntheticBaby();
        for (var i = 0; i < sessions; i++)
        {
            baby.Add(Tz.At(Tz.SaoPaulo, 2026, 10, 8, 7 + (i * 3)), Tz.At(Tz.SaoPaulo, 2026, 10, 8, 8 + (i * 3)), SleepKind.Nap);
        }

        var r = _engine.Predict(Req.For(baby.Records, 270, AsOf), AsOf);
        Assert.True(r.Nap().BaselineOnly);
        Assert.Equal(ConfidenceLevel.Low, r.Nap().Confidence);
    }

    [Fact]
    public void SE_CS_03_threshold_boundary_n_minus_1_vs_n_observations()
    {
        // Cada dia traz 2 sonecas = 1 wake window observada. 4 dias = 4 observações (baseline); 5 dias = 5 (personalizado).
        SleepPredictionResult Run(int days)
        {
            var baby = new SyntheticBaby();
            for (var d = 0; d < days; d++)
            {
                var day = 8 - days + d;
                baby.Add(Tz.At(Tz.SaoPaulo, 2026, 10, day, 10), Tz.At(Tz.SaoPaulo, 2026, 10, day, 10, 40), SleepKind.Nap);
                baby.Add(Tz.At(Tz.SaoPaulo, 2026, 10, day, 13, 40), Tz.At(Tz.SaoPaulo, 2026, 10, day, 14, 20), SleepKind.Nap);
            }

            var asOf = Tz.At(Tz.SaoPaulo, 2026, 10, 8, 8);
            return _engine.Predict(Req.For(baby.Records, 270, asOf), asOf);
        }

        var below = Run(4);
        var at = Run(5);
        Assert.True(below.Nap().BaselineOnly);
        Assert.False(at.Nap().BaselineOnly);
        Assert.Equal(MessageKeys.AgeOnly, below.Nap().Explanation.Key);
        Assert.Equal(MessageKeys.HistoryAndAge, at.Nap().Explanation.Key);
        Assert.Equal(4, below.DataQuality.WakeWindowObservations);
        Assert.Equal(5, at.DataQuality.WakeWindowObservations);
    }

    [Fact]
    public void SE_CS_04_newborn_has_no_fixed_nap_count_and_no_bedtime_prediction()
    {
        var r = _engine.Predict(Req.For([], 20, AsOf), AsOf);
        Assert.Single(r.Predictions);
        Assert.Equal(PredictionKind.NextNap, r.Predictions[0].Kind);
        Assert.Null(r.Plan!.ExpectedNapCount);
        Assert.Equal(45, r.Nap().Explanation.Params["typical_wake_minutes"]);
    }

    [Fact]
    public void SE_CS_04_newborn_naps_are_never_capped_by_a_default_count()
    {
        var baby = new SyntheticBaby();
        var t = Tz.At(Tz.SaoPaulo, 2026, 10, 8, 0, 30);
        for (var i = 0; i < 7; i++)
        {
            baby.Add(t.AddMinutes(50), t.AddMinutes(100), SleepKind.Nap);
            t = t.AddMinutes(100);
        }

        var asOf = t.AddMinutes(5);
        var r = _engine.Predict(Req.For(baby.Records, 20, asOf), asOf);
        Assert.Equal(PredictionStatus.Available, r.Status);
        Assert.Equal(PredictionKind.NextNap, r.Predictions[0].Kind);
    }

    [Fact]
    public void SE_CS_05_premature_baby_uses_corrected_age_and_differs_from_chronological()
    {
        // 150 dias cronológicos, 60 dias de prematuridade -> corrigida 90 (faixa 6w-3m) vs cronológica (faixa 5m-8m, transição)
        var corrected = _engine.Predict(Req.For([], 150, AsOf, dueOffsetDays: 60), AsOf);
        var chrono = _engine.Predict(Req.For([], 150, AsOf), AsOf);

        Assert.True(corrected.Age!.CorrectionApplied);
        Assert.Equal(90, corrected.Age.CorrectedDays);
        Assert.Equal("CORRECTED", corrected.Nap().Explanation.Params["age_basis"]);
        Assert.Equal(2, corrected.Nap().Explanation.Params["age_months"]);
        Assert.True((int)corrected.Nap().Explanation.Params["typical_wake_minutes"] < (int)chrono.Nap().Explanation.Params["typical_wake_minutes"]);
        Assert.False(chrono.Age!.CorrectionApplied);
    }

    [Fact]
    public void SE_CS_05_birth_and_due_dates_are_never_changed_by_the_engine()
    {
        var request = Req.For([], 150, AsOf, dueOffsetDays: 60);
        var copy = request with { };
        _ = _engine.Predict(request, AsOf);
        Assert.Equal(copy.Baby, request.Baby);
        Assert.NotEqual(request.Baby.BirthDate, request.Baby.DueDate);
    }

    [Fact]
    public void SE_CS_06_adjacent_days_around_a_band_boundary_do_not_jump()
    {
        var before = _engine.Predict(Req.For([], 239, AsOf), AsOf).Nap();
        var after = _engine.Predict(Req.For([], 240, AsOf), AsOf).Nap();
        var diff = Math.Abs((int)before.Explanation.Params["typical_wake_minutes"] - (int)after.Explanation.Params["typical_wake_minutes"]);
        Assert.True(diff <= 8, $"salto {diff}");
    }

    [Fact]
    public void SE_CS_07_only_night_sleep_keeps_baseline_for_naps_but_personalizes_bedtime()
    {
        var baby = new SyntheticBaby();
        // 8 noites começando às 21:00 (baseline 19:30)
        for (var d = 1; d <= 8; d++)
        {
            baby.Add(Tz.At(Tz.SaoPaulo, 2026, 10, d, 21), Tz.At(Tz.SaoPaulo, 2026, 10, d + 1, 7), SleepKind.Night);
        }

        var asOf = Tz.At(Tz.SaoPaulo, 2026, 10, 9, 8);
        var r = _engine.Predict(Req.For(baby.Records, 270, asOf), asOf);

        Assert.True(r.Nap().BaselineOnly);
        Assert.Equal(ConfidenceLevel.Low, r.Nap().Confidence);
        var bed = r.Bed();
        Assert.False(bed.BaselineOnly);
        // personalizado: puxado para 21:00 mas limitado pelo teto da faixa (20:45)
        Assert.True(bed.PredictedStart.Local() > (19 * 60) + 30, bed.PredictedStart.ToString());
        Assert.Equal(MessageKeys.BedtimeHistoryAndAge, bed.Explanation.Key);
    }

    [Fact]
    public void Age_beyond_table_coverage_is_insufficient_data_with_an_explanation()
    {
        var r = _engine.Predict(Req.For([], Engines.Table.MaxAgeDays + 10, AsOf), AsOf);
        Assert.Equal(PredictionStatus.InsufficientData, r.Status);
        Assert.Empty(r.Predictions);
        Assert.Equal(MessageKeys.AgeOutOfRange, r.StatusExplanation!.Key);
    }

    [Fact]
    public void Baby_not_yet_born_is_insufficient_data()
    {
        var request = new SleepPredictionRequest(new BabyProfile(new DateOnly(2027, 1, 1), null, Tz.SaoPaulo), []);
        var r = _engine.Predict(request, AsOf);
        Assert.Equal(PredictionStatus.InsufficientData, r.Status);
    }

    [Fact]
    public void Unknown_timezone_is_insufficient_data_not_an_exception()
    {
        var request = new SleepPredictionRequest(new BabyProfile(new DateOnly(2026, 1, 1), null, "Mars/Olympus"), []);
        var r = _engine.Predict(request, AsOf);
        Assert.Equal(PredictionStatus.InsufficientData, r.Status);
        Assert.Equal(MessageKeys.TimezoneUnknown, r.StatusExplanation!.Key);
    }
}
