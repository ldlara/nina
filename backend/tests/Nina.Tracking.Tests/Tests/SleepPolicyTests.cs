using System.Text.Json.Nodes;
using Nina.Tracking.Tests.Infrastructure;

namespace Nina.Tracking.Tests.Tests;

/// <summary>ADR-0009 decisão 5: sono sobreposto governado pela flag <c>sleep.overlap_policy</c>; relógio errado só avisa (SR-014).</summary>
public sealed class SleepPolicyTests(PostgresFixture postgres) : TrackingTestBase(postgres)
{
    [Fact]
    public async Task Accept_and_warn_keeps_both_sessions_flags_the_overlap_and_ignores_adjacent_sessions()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var a = Guid.NewGuid();
        var first = (await PushOkAsync(ana, Mut.Create(Mut.Sleep, baby, a, Mut.SleepData(T0, T0.AddHours(2)))))[0];
        Assert.Equal("NONE", first.Resolution());

        var adjacent = (await PushOkAsync(ana, Mut.Create(Mut.Sleep, baby, Guid.NewGuid(), Mut.SleepData(T0.AddHours(2), T0.AddHours(3)))))[0];
        Assert.Equal("NONE", adjacent.Resolution());
        Assert.Empty(adjacent.WarningCodes());

        var overlap = (await PushOkAsync(ana, Mut.Create(Mut.Sleep, baby, Guid.NewGuid(), Mut.SleepData(T0.AddMinutes(30), T0.AddHours(1)))))[0];
        Assert.Equal("APPLIED", overlap.Status());
        Assert.Equal("KEPT_BOTH", overlap.Resolution());
        Assert.Equal(["SLEEP_OVERLAP"], overlap.WarningCodes());
        Assert.Equal(a.ToString(), overlap["warnings"]![0]!["related_entity_ids"]![0]!.GetValue<string>());
        Assert.NotNull(overlap.Entity());
        Assert.Equal(3L, await AdminScalarAsync<long>($"SELECT count(*) FROM nina.sleep_session WHERE baby_id = '{baby}'"));
    }

    [Fact]
    public async Task An_update_that_creates_an_overlap_also_warns()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var b = Guid.NewGuid();
        await PushOkAsync(ana, Mut.Create(Mut.Sleep, baby, Guid.NewGuid(), Mut.SleepData(T0, T0.AddHours(2))));
        var created = (await PushOkAsync(ana, Mut.Create(Mut.Sleep, baby, b, Mut.SleepData(T0.AddHours(3), T0.AddHours(4)))))[0];
        var moved = (await PushOkAsync(ana, Mut.Update(Mut.Sleep, baby, b, created.Version(), new JsonObject { ["start_at"] = Mut.At(T0.AddHours(1)) })))[0];
        Assert.Equal("KEPT_BOTH", moved.Resolution());
        Assert.Equal(["SLEEP_OVERLAP"], moved.WarningCodes());
    }

    [Fact]
    public async Task Two_open_timers_from_two_devices_are_both_kept_with_OPEN_SLEEP_EXISTS()
    {
        var ana = await UserAsync();
        var bia = await UserAsync();
        var baby = await BabyAsync(ana);
        await MemberAsync(baby, bia, "CAREGIVER");
        var first = Guid.NewGuid();
        await PushOkAsync(ana, Mut.Create(Mut.Sleep, baby, first, Mut.SleepData(T0, null, source: "TIMER")));
        var second = (await PushOkAsync(bia, Mut.Create(Mut.Sleep, baby, Guid.NewGuid(), Mut.SleepData(T0.AddMinutes(5), null, source: "TIMER"))))[0];
        Assert.Equal("APPLIED", second.Status());
        Assert.Equal("KEPT_BOTH", second.Resolution());
        Assert.Equal(["OPEN_SLEEP_EXISTS"], second.WarningCodes());
        Assert.Equal(first.ToString(), second["warnings"]![0]!["related_entity_ids"]![0]!.GetValue<string>());
        Assert.Equal(2L, await AdminScalarAsync<long>($"SELECT count(*) FROM nina.sleep_session WHERE baby_id = '{baby}' AND end_at IS NULL"));
    }

    [Fact]
    public async Task Reject_policy_refuses_the_overlap_changes_nothing_and_leaves_the_sequence_untouched()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        await PushOkAsync(ana, Mut.Create(Mut.Sleep, baby, Guid.NewGuid(), Mut.SleepData(T0, T0.AddHours(2))));
        await SetParameterAsync("sleep.overlap_policy", "\"REJECT\"", "STRING");
        var head = await AdminScalarAsync<long>($"SELECT last_sequence FROM nina.baby_sync_head WHERE baby_id = '{baby}'");

        var results = await PushOkAsync(
            ana,
            Mut.Create(Mut.Sleep, baby, Guid.NewGuid(), Mut.SleepData(T0.AddHours(1), T0.AddHours(3))),
            Mut.Create(Mut.Sleep, baby, Guid.NewGuid(), Mut.SleepData(T0.AddHours(2), T0.AddHours(3))));            // adjacente: passa
        Assert.Equal("REJECTED", results[0].Status());
        Assert.Equal("SLEEP_OVERLAP", results[0].ProblemCode());
        Assert.False(results[0]!["retryable"]!.GetValue<bool>());
        Assert.Equal("APPLIED", results[1].Status());
        Assert.Equal(head + 1, await AdminScalarAsync<long>($"SELECT last_sequence FROM nina.baby_sync_head WHERE baby_id = '{baby}'"));   // só a aceita consumiu sequência
        Assert.Equal(2L, await AdminScalarAsync<long>($"SELECT count(*) FROM nina.sleep_session WHERE baby_id = '{baby}'"));
    }

    [Fact]
    public async Task A_wrong_device_clock_only_warns_and_the_client_time_is_preserved_but_never_decides_the_order()
    {
        var ana = await UserAsync();
        var bia = await BabyAsync(ana);
        var id = Guid.NewGuid();
        var skewed = DateTimeOffset.UtcNow.AddDays(3);
        var result = (await PushOkAsync(ana, Mut.Create(Mut.Diaper, bia, id, Mut.DiaperData(T0, notes: "a"), clientAt: skewed)))[0];
        Assert.Equal("APPLIED", result.Status());
        Assert.Equal(["CLIENT_CLOCK_SKEW"], result.WarningCodes());
        var warning = result["warnings"]![0]!;
        Assert.InRange(warning["skew_seconds"]!.GetValue<long>(), 3 * 86400 - 60, 3 * 86400 + 60);
        Assert.Equal(86400, warning["tolerance_seconds"]!.GetValue<int>());
        Assert.Equal(skewed.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"), (await AdminScalarAsync<DateTime>($"SELECT client_created_at FROM nina.sync_mutation WHERE entity_id = '{id}'")).ToString("yyyy-MM-dd HH:mm:ss"));

        // uma edição posterior com relógio correto vence mesmo que a anterior declarasse o futuro
        var later = (await PushOkAsync(ana, Mut.Update(Mut.Diaper, bia, id, result.Version(), new JsonObject { ["notes"] = "b" })))[0];
        Assert.Empty(later.WarningCodes());
        Assert.Equal("b", await AdminScalarAsync<string>($"SELECT notes FROM nina.diaper_event WHERE id = '{id}'"));
    }
}
