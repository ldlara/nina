using Nina.SleepIntelligence.Tests.Support;

namespace Nina.SleepIntelligence.Tests;

public sealed class PreferencesTests
{
    private static readonly DateTimeOffset Morning = Tz.At(Tz.SaoPaulo, 2026, 10, 8, 7, 30);
    private readonly SleepPredictionEngine _engine = Engines.Create();

    private SleepPredictionResult Run(SleepPreferences? prefs, DateTimeOffset? asOf = null, IEnumerable<SleepRecord>? history = null)
    {
        var at = asOf ?? Morning;
        return _engine.Predict(Req.For(history ?? [], 270, at, prefs: prefs), at);
    }

    [Theory]
    [InlineData(3, null, null, 0)]
    [InlineData(0, "19:00", "20:00", 0)]
    [InlineData(8, null, null, 0)]
    public void Valid_preferences_pass(int? naps, string? from, string? to, int expectedErrors)
    {
        var p = new SleepPreferences(naps, SleepPreferencesValidator.ParseTime(from), SleepPreferencesValidator.ParseTime(to));
        Assert.Equal(expectedErrors, SleepPreferencesValidator.Validate(p, Engines.Table).Count);
        Assert.Empty(SleepPreferencesValidator.Validate(null, Engines.Table));
        Assert.Empty(SleepPreferencesValidator.Validate(SleepPreferences.None, Engines.Table));
    }

    [Theory]
    [InlineData(-1, null, null, SleepPreferencesValidator.NapCountOutOfRange)]
    [InlineData(9, null, null, SleepPreferencesValidator.NapCountOutOfRange)]
    [InlineData(null, "19:00", null, SleepPreferencesValidator.BedtimeIncomplete)]
    [InlineData(null, null, "20:00", SleepPreferencesValidator.BedtimeIncomplete)]
    [InlineData(null, "20:30", "19:30", SleepPreferencesValidator.BedtimeOrder)] // início depois do fim
    [InlineData(null, "20:00", "20:00", SleepPreferencesValidator.BedtimeOrder)]
    [InlineData(null, "22:00", "01:00", SleepPreferencesValidator.BedtimeOrder)] // sem virada de meia-noite
    [InlineData(null, "15:00", "16:00", SleepPreferencesValidator.BedtimeImplausible)]
    [InlineData(null, "22:00", "23:59", SleepPreferencesValidator.BedtimeImplausible)]
    [InlineData(null, "19:00", "19:10", SleepPreferencesValidator.BedtimeTooNarrow)]
    public void Invalid_preferences_are_refused_with_a_stable_code(int? naps, string? from, string? to, string code)
    {
        var p = new SleepPreferences(naps, SleepPreferencesValidator.ParseTime(from), SleepPreferencesValidator.ParseTime(to));
        Assert.Contains(SleepPreferencesValidator.Validate(p, Engines.Table), e => e.Code == code);
    }

    [Fact]
    public void Time_parser_accepts_only_hh_mm()
    {
        Assert.Equal(new TimeOnly(19, 30), SleepPreferencesValidator.ParseTime("19:30"));
        Assert.Null(SleepPreferencesValidator.ParseTime("7:30pm"));
        Assert.Null(SleepPreferencesValidator.ParseTime("24:00"));
        Assert.Null(SleepPreferencesValidator.ParseTime(null));
    }

    [Fact]
    public void Preferred_bedtime_range_bounds_the_predicted_window()
    {
        var early = Run(new SleepPreferences(null, new TimeOnly(18, 45), new TimeOnly(19, 15))).Bed();
        Assert.InRange(early.PredictedStart.Local(), 18 * 60 + 45, 19 * 60 + 15);
        Assert.InRange(early.PredictedEnd!.Value.Local(), 18 * 60 + 45, 19 * 60 + 15);
        Assert.Equal(MessageKeys.BedtimePreference, early.Explanation.Key);

        var late = Run(new SleepPreferences(null, new TimeOnly(20, 30), new TimeOnly(21, 0))).Bed();
        Assert.True(late.PredictedStart.Local() >= (20 * 60) + 30);
        Assert.True(late.PredictedStart.Local() <= 21 * 60);
    }

    [Fact]
    public void Removing_preferences_restores_the_age_default()
    {
        var withPref = Run(new SleepPreferences(null, new TimeOnly(20, 30), new TimeOnly(21, 0)));
        var cleared = Run(SleepPreferences.None);
        var never = Run(null);
        Assert.Equal(Json.Serialize(never.Predictions), Json.Serialize(cleared.Predictions));
        Assert.NotEqual(Json.Serialize(withPref.Predictions), Json.Serialize(cleared.Predictions));
        Assert.False(cleared.Plan!.PreferencesApplied);
    }

    [Fact]
    public void Nap_target_of_one_stops_nap_predictions_after_the_first_nap_of_the_day()
    {
        var nap = new SleepRecord(Guid.NewGuid(), Tz.At(Tz.SaoPaulo, 2026, 10, 8, 9, 30), Tz.At(Tz.SaoPaulo, 2026, 10, 8, 10, 30), SleepKind.Nap);
        var asOf = Tz.At(Tz.SaoPaulo, 2026, 10, 8, 10, 40);

        var one = Run(new SleepPreferences(1), asOf, [nap]);
        Assert.DoesNotContain(one.Predictions, p => p.Kind == PredictionKind.NextNap);
        Assert.Contains(one.Predictions, p => p.Kind == PredictionKind.Bedtime);
        Assert.Equal(1, one.Plan!.NapsCompletedToday);

        var three = Run(new SleepPreferences(3), asOf, [nap]);
        Assert.Contains(three.Predictions, p => p.Kind == PredictionKind.NextNap);
    }

    [Fact]
    public void Nap_target_zero_means_no_nap_predictions()
    {
        var r = Run(new SleepPreferences(0));
        Assert.DoesNotContain(r.Predictions, p => p.Kind == PredictionKind.NextNap);
    }

    [Fact]
    public void When_nothing_can_be_predicted_the_status_explains_the_target_instead_of_a_failure()
    {
        var asOf = Tz.At(Tz.SaoPaulo, 2026, 10, 8, 10);
        var r = _engine.Predict(Req.For([], 20, asOf, prefs: new SleepPreferences(0)), asOf); // recém-nascido: sem bedtime na tabela
        Assert.Equal(PredictionStatus.InsufficientData, r.Status);
        Assert.Equal(MessageKeys.NapTargetReached, r.StatusExplanation!.Key);
    }

    [Fact]
    public void A_late_afternoon_nap_that_would_crowd_bedtime_is_not_predicted()
    {
        var woke = Tz.At(Tz.SaoPaulo, 2026, 10, 8, 17, 0);
        var history = new[] { new SleepRecord(Guid.NewGuid(), woke.AddMinutes(-60), woke, SleepKind.Nap) };
        var r = Run(new SleepPreferences(3, new TimeOnly(19, 0), new TimeOnly(19, 45)), woke.AddMinutes(5), history);
        Assert.DoesNotContain(r.Predictions, p => p.Kind == PredictionKind.NextNap);
        Assert.Contains(r.Predictions, p => p.Kind == PredictionKind.Bedtime);
    }

    [Fact]
    public void When_a_nap_is_predicted_bedtime_comes_after_it_with_room_to_wind_down()
    {
        var woke = Tz.At(Tz.SaoPaulo, 2026, 10, 8, 13, 0);
        var history = new[] { new SleepRecord(Guid.NewGuid(), woke.AddMinutes(-50), woke, SleepKind.Nap) };
        var r = Run(null, woke.AddMinutes(5), history);
        var nap = r.Nap();
        var bed = r.Bed();
        Assert.True(bed.PredictedStart >= nap.PredictedEnd);
        Assert.Equal(PredictionKind.NextNap, r.Predictions[0].Kind);
    }

    [Fact]
    public void Physical_constraints_beat_preference_the_prediction_is_never_in_the_past()
    {
        var asOf = Tz.At(Tz.SaoPaulo, 2026, 10, 8, 21, 30); // depois do fim da faixa preferida
        var r = Run(new SleepPreferences(null, new TimeOnly(19, 0), new TimeOnly(20, 0)), asOf);
        Assert.True(r.Bed().PredictedStart > asOf);
        Assert.True(r.Bed().PredictedStart <= asOf.AddMinutes(2));
    }
}
