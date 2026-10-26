using Microsoft.Extensions.DependencyInjection;
using Nina.SleepIntelligence.Tests.Support;

namespace Nina.SleepIntelligence.Tests;

public sealed class MetricsAndModuleTests
{
    [Fact]
    public void RF_010_A2_three_naps_of_45_60_30_give_135_minutes_and_a_count_of_3()
    {
        var day = new DateOnly(2026, 10, 8);
        var z = Tz.Zone(Tz.SaoPaulo);
        DateTimeOffset At(int h) => TimeZones.ToUtc(day, h * 60, z);
        List<SleepRecord> records =
        [
            new(Guid.NewGuid(), At(9), At(9).AddMinutes(45), SleepKind.Nap),
            new(Guid.NewGuid(), At(12), At(12).AddMinutes(60), SleepKind.Nap),
            new(Guid.NewGuid(), At(15), At(15).AddMinutes(30), SleepKind.Nap),
        ];
        var total = Assert.Single(SleepMetrics.DailyTotals(records, Tz.SaoPaulo, At(20)));
        Assert.Equal(3, total.NapCount);
        Assert.Equal(135, total.NapMinutes);
    }

    [Fact]
    public void RF_010_A3_wake_window_is_the_gap_between_the_end_of_one_session_and_the_start_of_the_next()
    {
        var t = Tz.At(Tz.SaoPaulo, 2026, 10, 8, 9);
        List<SleepRecord> records =
        [
            new(Guid.NewGuid(), t, t.AddMinutes(45), SleepKind.Nap),
            new(Guid.NewGuid(), t.AddMinutes(45 + 130), t.AddMinutes(45 + 130 + 60), SleepKind.Nap),
        ];
        var w = Assert.Single(SleepMetrics.WakeWindows(records, t.AddHours(5)));
        Assert.Equal(130, w.Minutes);
        Assert.Equal(SleepKind.Nap, w.NextKind);
    }

    [Fact]
    public void RF_010_A6_open_session_is_not_part_of_closed_totals_or_wake_windows()
    {
        var t = Tz.At(Tz.SaoPaulo, 2026, 10, 8, 9);
        List<SleepRecord> records =
        [
            new(Guid.NewGuid(), t, t.AddMinutes(45), SleepKind.Nap),
            new(Guid.NewGuid(), t.AddMinutes(200), null, SleepKind.Nap),
        ];
        Assert.Empty(SleepMetrics.WakeWindows(records, t.AddMinutes(210)));
        Assert.Equal(45, Assert.Single(SleepMetrics.DailyTotals(records, Tz.SaoPaulo, t.AddMinutes(210))).NapMinutes);
    }

    private static (SleepRecord Night, SleepRecord Nap) NightAndNap()
    {
        var s = Tz.At(Tz.SaoPaulo, 2026, 10, 7, 20);
        return (new SleepRecord(Guid.NewGuid(), s, s.AddHours(10), SleepKind.Night), new SleepRecord(Guid.NewGuid(), s.AddHours(14), s.AddHours(15), SleepKind.Nap));
    }

    [Fact]
    public void ADR_0009_night_awakenings_null_means_insufficient_zero_means_none_and_n_is_the_count()
    {
        var (night, nap) = NightAndNap();
        WakeEventRecord Wake(int minuteFromStart, int minutes) =>
            new(Guid.NewGuid(), night.Id, night.StartAt.AddMinutes(minuteFromStart), night.StartAt.AddMinutes(minuteFromStart + minutes));
        var wakes = new List<WakeEventRecord> { Wake(120, 10), Wake(300, 5), Wake(400, 0) /* < 60 s: ignorado */ };

        Assert.Null(SleepMetrics.NightAwakenings(night, wakes, trackingActive: false)); // sem acompanhamento: desconhecido
        Assert.Null(SleepMetrics.NightAwakenings(nap, wakes, trackingActive: true)); // soneca: não se aplica
        Assert.Equal(2, SleepMetrics.NightAwakenings(night, wakes, trackingActive: true));
        Assert.Equal(0, SleepMetrics.NightAwakenings(night, [], trackingActive: true));

        var open = night with { EndAt = null };
        Assert.Null(SleepMetrics.NightAwakenings(open, wakes, true));
        var shortNight = night with { EndAt = night.StartAt.AddMinutes(90) };
        Assert.Null(SleepMetrics.NightAwakenings(shortNight, [], true));
    }

    [Fact]
    public void Awakening_tracking_is_active_only_when_recent_nights_have_wake_events()
    {
        var (night, nap) = NightAndNap();
        var wake = new WakeEventRecord(Guid.NewGuid(), night.Id, night.StartAt.AddHours(2), night.StartAt.AddHours(2).AddMinutes(10));
        Assert.False(SleepMetrics.IsAwakeningTrackingActive([night, nap], []));
        Assert.True(SleepMetrics.IsAwakeningTrackingActive([night, nap], [wake]));
        Assert.False(SleepMetrics.IsAwakeningTrackingActive([nap], [wake])); // despertar de sessão fora das últimas noites
        Assert.False(SleepMetrics.IsAwakeningTrackingActive([night], [wake], new NightAwakeningsPolicy { LookbackNights = 0 }));
    }

    [Fact]
    public void Module_registers_a_working_engine_driven_by_the_injected_clock()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 8, 13, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSleepIntelligenceModule();
        using var provider = services.BuildServiceProvider();

        var engine = provider.GetRequiredService<ISleepPredictionEngine>();
        var r = engine.Predict(new SleepPredictionRequest(new BabyProfile(new DateOnly(2026, 1, 10), null, Tz.SaoPaulo), []));
        Assert.Equal(PredictionStatus.Available, r.Status);
        Assert.Equal(clock.GetUtcNow(), r.ComputedAt);
        Assert.Same(engine, provider.GetRequiredService<ISleepPredictionEngine>());
    }

    [Fact]
    public void Reference_table_can_be_swapped_without_touching_code_validated_table_replaces_the_draft()
    {
        var validated = Engines.TableJson().Replace("RASCUNHO — requer validação por especialista clínico", "Validado por especialista (ata 2026-11-01)", StringComparison.Ordinal);
        var table = ReferenceTable.Parse(validated);
        Assert.False(table.IsDraft);
        var r = new SleepPredictionEngine(table).Predict(new SleepPredictionRequest(new BabyProfile(new DateOnly(2026, 1, 10), null, Tz.SaoPaulo), []), new DateTimeOffset(2026, 10, 8, 13, 0, 0, TimeSpan.Zero));
        Assert.False(r.ReferenceTableIsDraft);
        Assert.Equal(table.Hash, r.ReferenceTableHash);
    }
}
