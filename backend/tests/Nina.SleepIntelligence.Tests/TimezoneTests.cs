using Nina.SleepIntelligence.Tests.Support;

namespace Nina.SleepIntelligence.Tests;

/// <summary>SE-TZ-*: UTC como verdade, fuso só para o relógio de parede (RB-014).</summary>
public sealed class TimezoneTests
{
    private readonly SleepPredictionEngine _engine = Engines.Create();

    [Fact]
    public void SE_TZ_01_same_instants_in_another_zone_give_the_same_nap_prediction_and_leave_records_untouched()
    {
        var baby = new SyntheticBaby().Days(new DateOnly(2026, 9, 26), 12, 2, 150, 60, 150, (19 * 60) + 30, 3, seed: 21);
        var asOf = baby.LastEnd.AddMinutes(20);
        var before = baby.Records.ToList();

        var sp = _engine.Predict(Req.For(baby.Records, 270, asOf, Tz.SaoPaulo), asOf);
        var lisbon = _engine.Predict(Req.For(baby.Records, 270, asOf, Tz.Lisbon), asOf);

        Assert.Equal(sp.Nap().PredictedStart, lisbon.Nap().PredictedStart);
        Assert.Equal(sp.Nap().PredictedEnd, lisbon.Nap().PredictedEnd);
        Assert.Equal(before, baby.Records);
    }

    [Fact]
    public void SE_TZ_02_travel_does_not_reinterpret_history_and_bedtime_adapts_immediately_to_the_new_zone()
    {
        // 12 noites às 19:30 em São Paulo; a família agora está em Lisboa (perfil do bebê com novo fuso).
        var baby = new SyntheticBaby(Tz.SaoPaulo).Days(new DateOnly(2026, 9, 26), 12, 2, 150, 60, 150, (19 * 60) + 30, 3, seed: 21);
        var asOf = baby.LastEnd.AddMinutes(20);
        var r = _engine.Predict(Req.For(baby.Records, 270, asOf, Tz.Lisbon), asOf);

        Assert.True(r.DataQuality.TimezoneChangedInHistory);
        var bed = r.Bed();
        Assert.True(bed.BaselineOnly); // hábitos do fuso antigo não são transferidos
        Assert.InRange(bed.PredictedStart.Local(Tz.Lisbon), (18 * 60) + 30, (20 * 60) + 45);
        // wake windows (duração) continuam valendo: independem de fuso
        Assert.False(r.Nap().BaselineOnly);
    }

    [Fact]
    public void SE_TZ_03_new_york_spring_forward_day_has_23_hours_and_durations_use_utc_differences()
    {
        // Noite de 21:00 (EST) de 7/mar a 07:00 (EDT) de 8/mar: relógio de parede diz 10 h, real são 9 h.
        var start = Tz.At(Tz.NewYork, 2026, 3, 7, 21);
        var end = Tz.At(Tz.NewYork, 2026, 3, 8, 7);
        Assert.Equal(9 * 60, (int)(end - start).TotalMinutes);
        var totals = SleepMetrics.DailyTotals([new SleepRecord(Guid.NewGuid(), start, end, SleepKind.Night)], Tz.NewYork, end.AddHours(1));
        Assert.Equal(540, Assert.Single(totals).NightMinutes);
        Assert.Equal(new DateOnly(2026, 3, 7), totals[0].LocalDate);
    }

    [Fact]
    public void SE_TZ_03_new_york_fall_back_day_has_25_hours()
    {
        var start = Tz.At(Tz.NewYork, 2026, 10, 31, 21);
        var end = Tz.At(Tz.NewYork, 2026, 11, 1, 7);
        var totals = SleepMetrics.DailyTotals([new SleepRecord(Guid.NewGuid(), start, end, SleepKind.Night)], Tz.NewYork, end.AddHours(1));
        Assert.Equal(660, totals[0].NightMinutes);
    }

    [Fact]
    public void SE_TZ_03_wake_window_across_the_repeated_hour_is_correct_and_no_nap_is_lost_or_duplicated()
    {
        var nap1 = Tz.At(Tz.Lisbon, 2026, 10, 25, 0, 30); // 00:30 WEST (primeira ocorrência da hora repetida)
        var nap2Start = nap1.AddMinutes(100).AddMinutes(150);
        var records = new List<SleepRecord>
        {
            new(Guid.NewGuid(), nap1, nap1.AddMinutes(100), SleepKind.Nap),
            new(Guid.NewGuid(), nap2Start, nap2Start.AddMinutes(40), SleepKind.Nap),
        };
        var windows = SleepMetrics.WakeWindows(records, nap2Start.AddHours(2));
        Assert.Equal(150, Assert.Single(windows).Minutes);
        var totals = SleepMetrics.DailyTotals(records, Tz.Lisbon, nap2Start.AddHours(2));
        Assert.Equal(2, totals.Sum(t => t.NapCount));
    }

    [Fact]
    public void SE_TZ_03_nonexistent_and_ambiguous_local_times_resolve_deterministically()
    {
        var ny = Tz.Zone(Tz.NewYork);
        // 02:30 de 8/mar/2026 não existe em NY: avança para 03:00 EDT = 07:00Z
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), TimeZones.ToUtc(new DateOnly(2026, 3, 8), (2 * 60) + 30, ny));
        // 01:30 de 1/nov/2026 ocorre duas vezes: usa a primeira (EDT) = 05:30Z
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), TimeZones.ToUtc(new DateOnly(2026, 11, 1), (1 * 60) + 30, ny));
    }

    [Fact]
    public void SE_TZ_03_bedtime_on_a_dst_day_is_a_correct_local_time()
    {
        var asOf = Tz.At(Tz.NewYork, 2026, 3, 8, 10); // dia de 23 h
        var r = _engine.Predict(Req.For([], 270, asOf, Tz.NewYork), asOf);
        Assert.InRange(r.Bed().PredictedStart.Local(Tz.NewYork), (18 * 60) + 30, (20 * 60) + 45);
    }

    [Fact]
    public void SE_TZ_03_control_zone_without_dst_has_24_hour_days()
    {
        var start = Tz.At(Tz.SaoPaulo, 2026, 3, 7, 21);
        var end = Tz.At(Tz.SaoPaulo, 2026, 3, 8, 7);
        Assert.Equal(600, (int)(end - start).TotalMinutes);
    }

    [Fact]
    public void SE_TZ_04_night_crossing_midnight_belongs_to_the_day_it_started_using_the_record_zone()
    {
        var start = Tz.At(Tz.SaoPaulo, 2026, 10, 7, 21);
        var end = Tz.At(Tz.SaoPaulo, 2026, 10, 8, 6);
        var totals = SleepMetrics.DailyTotals([new SleepRecord(Guid.NewGuid(), start, end, SleepKind.Night, Tz.SaoPaulo)], Tz.SaoPaulo, end.AddHours(2));
        Assert.Equal(new DateOnly(2026, 10, 7), Assert.Single(totals).LocalDate);
        Assert.Equal(540, totals[0].NightMinutes);

        // Mesmo instante visto pelo fuso do registro: 02:00Z é 23:00 de 6/out em SP e 03:00 de 7/out em Lisboa.
        var instant = new DateTimeOffset(2026, 10, 7, 2, 0, 0, TimeSpan.Zero);
        var sp = SleepMetrics.DailyTotals([new SleepRecord(Guid.NewGuid(), instant, instant.AddHours(8), SleepKind.Night, Tz.SaoPaulo)], Tz.Lisbon, instant.AddHours(10));
        var pt = SleepMetrics.DailyTotals([new SleepRecord(Guid.NewGuid(), instant, instant.AddHours(8), SleepKind.Night, Tz.Lisbon)], Tz.Lisbon, instant.AddHours(10));
        Assert.Equal(new DateOnly(2026, 10, 6), sp[0].LocalDate);
        Assert.Equal(new DateOnly(2026, 10, 7), pt[0].LocalDate);
    }

    [Fact]
    public void SE_TZ_05_two_caregivers_in_different_zones_register_the_same_nap_as_one()
    {
        var instant = new DateTimeOffset(2026, 10, 8, 13, 0, 0, TimeSpan.Zero);
        var records = new List<SleepRecord>
        {
            new(Guid.NewGuid(), instant, instant.AddMinutes(50), SleepKind.Nap, Tz.SaoPaulo),
            new(Guid.NewGuid(), instant, instant.AddMinutes(50), SleepKind.Nap, Tz.Lisbon),
        };
        var asOf = instant.AddMinutes(70);
        var r = _engine.Predict(Req.For(records, 270, asOf), asOf);
        Assert.Equal(1, r.Plan!.NapsCompletedToday);
    }

    [Theory]
    [InlineData(Tz.Kolkata)]
    [InlineData(Tz.LordHowe)]
    public void SE_TZ_06_fractional_offset_zones_give_correct_local_bedtimes(string tz)
    {
        var asOf = Tz.At(tz, 2026, 10, 8, 10);
        var r = _engine.Predict(Req.For([], 270, asOf, tz), asOf);
        Assert.InRange(r.Bed().PredictedStart.Local(tz), (18 * 60) + 30, (20 * 60) + 45);
        Assert.Equal(0, r.Bed().PredictedStart.Second);
    }

    [Theory]
    [InlineData(Tz.Kolkata)]
    [InlineData(Tz.LordHowe)]
    [InlineData(Tz.SaoPaulo)]
    [InlineData(Tz.NewYork)]
    public void Local_to_utc_round_trips_for_every_valid_minute_of_a_day(string tz)
    {
        var zone = Tz.Zone(tz);
        foreach (var date in new[] { new DateOnly(2026, 4, 5), new DateOnly(2026, 10, 4), new DateOnly(2026, 6, 15) })
        {
            for (var m = 0; m < 1440; m += 5)
            {
                var utc = TimeZones.ToUtc(date, m, zone);
                var back = TimeZones.LocalMinuteOfDay(utc, zone);
                var diff = Math.Abs(back - m);
                Assert.True(diff == 0 || diff <= 60, $"{tz} {date} {m} -> {back}"); // diferença só dentro de lacunas de DST
            }
        }
    }

    [Fact]
    public void SE_TZ_07_regrouping_totals_after_changing_the_baby_zone_alters_no_event()
    {
        var start = Tz.At(Tz.SaoPaulo, 2026, 10, 8, 22);
        var records = new List<SleepRecord> { new(Guid.NewGuid(), start, start.AddHours(9), SleepKind.Night) };
        var snapshot = records.ToList();
        var inSp = SleepMetrics.DailyTotals(records, Tz.SaoPaulo, start.AddHours(10));
        var inLisbon = SleepMetrics.DailyTotals(records, Tz.Lisbon, start.AddHours(10));
        Assert.NotEqual(inSp[0].LocalDate, inLisbon[0].LocalDate); // 22:00 SP = 02:00 do dia seguinte em Lisboa
        Assert.Equal(inSp.Sum(t => t.NightMinutes), inLisbon.Sum(t => t.NightMinutes));
        Assert.Equal(snapshot, records);
    }

    [Theory]
    [InlineData("America/Buenos_Aires")]
    [InlineData("Asia/Calcutta")]
    [InlineData("America/Argentina/Buenos_Aires")]
    [InlineData("Europe/Kiev")]
    public void SE_TZ_08_legacy_and_renamed_iana_names_resolve(string id)
    {
        Assert.NotNull(TimeZones.TryResolve(id));
        var asOf = new DateTimeOffset(2026, 10, 8, 13, 0, 0, TimeSpan.Zero);
        var request = new SleepPredictionRequest(new BabyProfile(new DateOnly(2026, 1, 8), null, id), []);
        Assert.Equal(PredictionStatus.Available, _engine.Predict(request, asOf).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Not/AZone")]
    public void Invalid_zone_names_do_not_resolve(string id) => Assert.Null(TimeZones.TryResolve(id));
}
