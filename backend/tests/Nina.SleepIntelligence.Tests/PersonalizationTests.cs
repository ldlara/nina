using Nina.SleepIntelligence.Tests.Support;

namespace Nina.SleepIntelligence.Tests;

public sealed class PersonalizationTests
{
    private readonly SleepPredictionEngine _engine = Engines.Create();

    /// <summary>9 meses, 2 sonecas/dia com wake window de 120 min (baseline da tabela: 165), 12 dias até ontem; hoje acordou às 06:30.</summary>
    private static (SyntheticBaby Baby, DateTimeOffset AsOf) RegularNineMonths(int days = 12, int jitter = 4)
    {
        var baby = new SyntheticBaby().Days(new DateOnly(2026, 9, 26 + (12 - days)), days, 2, 120, 60, 150, (19 * 60) + 30, jitter, seed: 11);
        // O último "Days" fecha a noite na manhã seguinte (09/out).
        var asOf = baby.LastEnd.AddMinutes(20);
        return (baby, asOf);
    }

    [Fact]
    public void Regular_history_pulls_the_prediction_from_the_baseline_toward_the_observed_wake_window()
    {
        var (baby, asOf) = RegularNineMonths();
        var r = _engine.Predict(Req.For(baby.Records, 270, asOf), asOf);
        var nap = r.Nap();

        Assert.False(nap.BaselineOnly);
        Assert.Equal(MessageKeys.HistoryAndAge, nap.Explanation.Key);
        var typical = (int)nap.Explanation.Params["typical_wake_minutes"];
        Assert.InRange(typical, 120, 135); // 90% observado (120) + 10% baseline (165) ≈ 125
        Assert.True(typical < 165);
        Assert.Equal(ConfidenceLevel.High, nap.Confidence);
        Assert.True((int)nap.Explanation.Params["days_used"] >= 10);
        Assert.True((int)nap.Explanation.Params["records_used"] >= 20);
    }

    [Fact]
    public void Personalized_value_is_bounded_by_the_slack_around_the_age_band()
    {
        // Baby de 9m cujo wake window real é 30 min: o valor não desce abaixo de 85% do mínimo da faixa (135 -> 114).
        var baby = new SyntheticBaby().Days(new DateOnly(2026, 9, 26), 12, 3, 30, 20, 150, (19 * 60) + 30, 0, seed: 1);
        // janelas de 30 min < 40% do mínimo (54) são outliers; usa 60 (dentro do limite de aceitação, abaixo do mínimo).
        var baby2 = new SyntheticBaby().Days(new DateOnly(2026, 9, 26), 12, 3, 60, 20, 150, (19 * 60) + 30, 0, seed: 1);
        var asOf = baby2.LastEnd.AddMinutes(10);
        var r = _engine.Predict(Req.For(baby2.Records, 270, asOf), asOf);
        Assert.Equal(114, r.Nap().Explanation.Params["typical_wake_minutes"]);
        _ = baby;
    }

    [Fact]
    public void Irregular_history_gives_low_confidence_and_an_explanation_that_says_so()
    {
        // Dias alternando wake windows de 80 e 220 min (dispersão relativa > 30%).
        var baby = new SyntheticBaby();
        for (var d = 0; d < 12; d++)
        {
            baby.Days(new DateOnly(2026, 9, 26).AddDays(d), 1, 2, d % 2 == 0 ? 70 : 230, 50, 150, (19 * 60) + 30, 0, seed: d);
        }

        var asOf = baby.LastEnd.AddMinutes(10);
        var r = _engine.Predict(Req.For(baby.Records, 270, asOf), asOf);
        Assert.Equal(ConfidenceLevel.Low, r.Nap().Confidence);
        Assert.Equal(MessageKeys.IrregularHistory, r.Nap().Explanation.Key);
        Assert.False(r.Nap().BaselineOnly);
    }

    [Fact]
    public void Confidence_is_monotonic_non_decreasing_in_data_volume()
    {
        var full = new SyntheticBaby().Days(new DateOnly(2026, 9, 25), 14, 2, 150, 60, 150, (19 * 60) + 30, 3, seed: 3);
        var asOf = Tz.At(Tz.SaoPaulo, 2026, 10, 9, 9);
        var previous = ConfidenceLevel.Low;
        var previousScore = -1;
        for (var days = 0; days <= 14; days++)
        {
            var cutoff = Tz.At(Tz.SaoPaulo, 2026, 10, 9, 0).AddDays(-days);
            var subset = full.Records.Where(x => x.StartAt >= cutoff).ToList();
            var r = _engine.Predict(Req.For(subset, 270, asOf), asOf);
            var nap = r.Nap();
            Assert.True(nap.Confidence >= previous, $"days={days}");
            Assert.True(nap.ConfidenceScore >= previousScore, $"days={days} score={nap.ConfidenceScore} prev={previousScore}");
            previous = nap.Confidence;
            previousScore = nap.ConfidenceScore;
        }

        Assert.Equal(ConfidenceLevel.High, previous);
    }

    [Fact]
    public void Old_history_outside_the_window_falls_back_to_baseline_and_assumes_awake_now()
    {
        var (baby, _) = RegularNineMonths();
        var asOf = baby.LastEnd.AddDays(20);
        var r = _engine.Predict(Req.For(baby.Records, 270, asOf), asOf);
        Assert.True(r.Nap().BaselineOnly);
        Assert.Equal(true, r.Nap().Explanation.Params["anchor_assumed"]);
        Assert.Equal(0, r.DataQuality.WakeWindowObservations);
    }

    [Fact]
    public void Recent_data_weighs_more_than_old_data()
    {
        // 7 dias antigos com janela 105 e 7 recentes com janela 150: a mediana ponderada fica mais perto do recente.
        var older = new SyntheticBaby().Days(new DateOnly(2026, 9, 22), 7, 2, 105, 50, 150, (19 * 60) + 30, 0, seed: 1);
        var newer = new SyntheticBaby().Days(new DateOnly(2026, 9, 29), 7, 2, 150, 50, 150, (19 * 60) + 30, 0, seed: 1);
        var all = older.Records.Concat(newer.Records.Select(r => r with { Id = Guid.NewGuid() })).ToList();
        var asOf = newer.LastEnd.AddMinutes(10);
        var r = _engine.Predict(Req.For(all, 270, asOf), asOf);
        Assert.True((int)r.Nap().Explanation.Params["typical_wake_minutes"] >= 140);
    }

    [Fact]
    public void Learned_nap_count_comes_from_history_and_target_preference_overrides_it()
    {
        var (baby, asOf) = RegularNineMonths();
        var byHistory = _engine.Predict(Req.For(baby.Records, 270, asOf), asOf);
        Assert.Equal(2, byHistory.Plan!.ExpectedNapCount);
        Assert.Equal("HISTORY", byHistory.Plan.NapCountSource);

        var byPref = _engine.Predict(Req.For(baby.Records, 270, asOf, prefs: new SleepPreferences(3)), asOf);
        Assert.Equal(3, byPref.Plan!.ExpectedNapCount);
        Assert.Equal("PREFERENCE", byPref.Plan.NapCountSource);
        Assert.True(byPref.Plan.PreferencesApplied);
    }

    [Fact]
    public void Statistics_weighted_median_is_exact_and_handles_ties()
    {
        Assert.Equal(20, Statistics.WeightedMedian([(10, 1), (20, 1), (30, 1)]));
        Assert.Equal(15, Statistics.WeightedMedian([(10, 1), (20, 1)])); // empate exato: média
        Assert.Equal(10, Statistics.WeightedMedian([(10, 5), (20, 1), (30, 1)]));
        Assert.Equal(30, Statistics.WeightedMedian([(10, 1), (20, 1), (30, 5)]));
        Assert.Equal(7, Statistics.WeightedMedian([(7, 3)]));
        Assert.Equal(0, Statistics.WeightedMad([(10, 1), (10, 1)], 10));
        Assert.Equal(80, Statistics.MeanAbsoluteDeviation([70, 230], 150));
        Assert.Equal(0, Statistics.MeanAbsoluteDeviation([], 0));
        Assert.Throws<ArgumentException>(() => Statistics.WeightedMedian([]));
    }

    [Fact]
    public void Recency_weight_halves_every_four_days_and_never_reaches_zero()
    {
        Assert.Equal(1000, Statistics.RecencyWeight(0));
        Assert.Equal(500, Statistics.RecencyWeight(4));
        Assert.Equal(250, Statistics.RecencyWeight(8));
        Assert.True(Statistics.RecencyWeight(1) > Statistics.RecencyWeight(2));
        Assert.Equal(1, Statistics.RecencyWeight(400));
    }
}
