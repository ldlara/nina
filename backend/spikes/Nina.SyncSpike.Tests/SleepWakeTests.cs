using Nina.SyncSpike.Harness;
using Nina.SyncSpike.Server;
using Xunit;

namespace Nina.SyncSpike.Tests;

[Collection("pg")]
public sealed class SleepWakeTests(SpikeFixture fx)
{
    private SpikeEnv Env => fx.Env;
    private static DateTimeOffset T(int m) => Data.T0.AddMinutes(m);

    // ------------------------------------------------------------ sono sobreposto (ADR-0009 item 5)

    [Fact]
    public async Task Overlapping_sleep_is_accepted_with_warning_by_default()
    {
        var t = await Trio.CreateAsync(Env);
        var a = t.A.Create("SLEEP_SESSION", Data.Sleep(T(0), T(60), "NAP"));
        t.B.Online = false;
        var b = t.B.Create("SLEEP_SESSION", Data.Sleep(T(30), T(90), "NAP"));            // sobrepõe A em 30 min
        var adj = t.B.Create("SLEEP_SESSION", Data.Sleep(T(60), T(70), "NAP"));          // adjacente a A: não é sobreposição de A
        await t.A.SyncAsync();
        t.B.Online = true;
        var rep = await t.B.SyncAsync();
        var rb = rep.Results.First(r => r.EntityId == b);
        Assert.Equal(("APPLIED", "KEPT_BOTH"), (rb.Status, rb.Resolution));
        var w = Assert.Single(rb.Warnings!);
        Assert.Equal("SLEEP_OVERLAP", w.Code);
        Assert.Contains(a, w.RelatedEntityIds!);
        Assert.NotNull(rb.Entity);
        var radj = rep.Results.First(r => r.EntityId == adj);
        Assert.Contains(radj.Warnings ?? [], x => x.RelatedEntityIds!.Contains(b));      // adj só sobrepõe B (60-90), não A
        Assert.DoesNotContain(radj.Warnings ?? [], x => x.RelatedEntityIds!.Contains(a));
        await t.SyncBothTwiceAsync();
        Assert.Equal(3, await Env.ScalarAsync<long>("SELECT count(*) FROM nina.sleep_session WHERE baby_id=@b AND deleted_at IS NULL", ("b", t.Baby.BabyId)));
        Assert.True(await Env.ScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE baby_id=@b AND metadata_safe->>'resolution'='KEPT_BOTH'", ("b", t.Baby.BabyId)) >= 1);
        await t.AssertConvergedAsync(t.A, t.B);
    }

    [Fact]
    public async Task Overlapping_sleep_is_rejected_when_flag_is_reject_and_consumes_no_sequence()
    {
        var t = await Trio.CreateAsync(Env);
        t.A.Create("SLEEP_SESSION", Data.Sleep(T(0), T(60)));
        await t.A.SyncAsync();
        await Env.SetOverlapPolicyAsync("REJECT", t.UserA);
        try
        {
            var head = (await Env.LogStatsAsync(t.Baby.BabyId)).Head;
            var b = t.B.Create("SLEEP_SESSION", Data.Sleep(T(30), T(90)));
            var rep = await t.B.SyncAsync();
            var r = Assert.Single(rep.Results);
            Assert.Equal(("REJECTED", "SLEEP_OVERLAP", false), (r.Status, r.Problem!.Code, r.Retryable ?? true));
            Assert.Equal(409, r.Problem.Status);
            Assert.Equal(0, await Env.ScalarAsync<long>("SELECT count(*) FROM nina.sleep_session WHERE id=@i", ("i", b)));
            var st = await Env.LogStatsAsync(t.Baby.BabyId);
            Assert.Equal(head, st.Head);                                                  // rollback do savepoint desfaz também o contador
            Assert.True(st.Contiguous);
            // sono adjacente continua aceito
            t.B.Create("SLEEP_SESSION", Data.Sleep(T(60), T(80)));
            Assert.Equal("APPLIED", Assert.Single((await t.B.SyncAsync()).Results).Status);
        }
        finally { await Env.SetOverlapPolicyAsync("ACCEPT_AND_WARN", t.UserA); }
        Assert.Equal(2, await Env.ScalarAsync<long>("SELECT count(*) FROM nina.config_change WHERE record_key='sleep.overlap_policy' AND actor_user_id=@u", ("u", t.UserA)));   // flag auditada
    }

    [Fact]
    public async Task Update_that_creates_overlap_warns()
    {
        var t = await Trio.CreateAsync(Env);
        t.A.Create("SLEEP_SESSION", Data.Sleep(T(0), T(60)));
        var x = t.A.Create("SLEEP_SESSION", Data.Sleep(T(100), T(120)));
        await t.A.SyncAsync();
        t.A.Update("SLEEP_SESSION", x, FakeDevice.D(("start_at", T(50))));
        var r = Assert.Single((await t.A.SyncAsync()).Results);
        Assert.Equal("KEPT_BOTH", r.Resolution);
        Assert.Equal("SLEEP_OVERLAP", Assert.Single(r.Warnings!).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_devices_start_timers_offline_keep_both_and_close_the_older_in_any_arrival_order(bool bArrivesFirst)
    {
        var t = await Trio.CreateAsync(Env);
        t.A.Online = false; t.B.Online = false;
        var sa = t.A.Create("SLEEP_SESSION", Data.Sleep(T(0), null));
        var sb = t.B.Create("SLEEP_SESSION", Data.Sleep(T(5), null));
        t.A.Online = true; t.B.Online = true;
        var (first, second) = bArrivesFirst ? (t.B, t.A) : (t.A, t.B);
        await first.SyncAsync();
        var r = (await second.SyncAsync()).Results.Single();
        Assert.Equal(("APPLIED", "KEPT_BOTH"), (r.Status, r.Resolution));
        Assert.Contains(r.Warnings!, w => w.Code == "OPEN_SLEEP_EXISTS");
        await t.SyncBothTwiceAsync();
        Assert.Equal(1, await Env.ScalarAsync<long>("SELECT count(*) FROM nina.sleep_session WHERE baby_id=@b AND end_at IS NULL", ("b", t.Baby.BabyId)));
        Assert.True(await Env.ScalarAsync<bool>("SELECT end_at IS NOT NULL FROM nina.sleep_session WHERE id=@i", ("i", sa)));   // a mais antiga fecha no início da mais nova
        Assert.True(await Env.ScalarAsync<bool>("SELECT end_at IS NULL FROM nina.sleep_session WHERE id=@i", ("i", sb)));
        await t.AssertConvergedAsync(t.A, t.B);
        // o usuário que parar o timer da sessão auto-fechada vence a heurística do servidor
        t.A.Now = () => DateTimeOffset.UtcNow.AddMinutes(-1);
        t.A.Update("SLEEP_SESSION", sa, FakeDevice.D(("end_at", T(3))));
        var u = (await t.A.SyncAsync()).Results.Single();
        Assert.Equal("LWW_CLIENT_WON", u.Resolution);
    }

    [Fact]
    public async Task Concurrent_open_timers_never_leave_two_open_sessions()
    {
        var t = await Trio.CreateAsync(Env);
        var ms = Enumerable.Range(0, 6).Select(i => (i, id: Guid.NewGuid(), dev: i % 2 == 0 ? t.A : t.B)).ToList();
        var res = await Task.WhenAll(ms.Select(x => Task.Run(() => Env.Service.PushAsync(x.dev.Auth,
            new PushRequest(x.dev.DeviceId, [new Mutation(Guid.NewGuid(), "CREATE", "SLEEP_SESSION", x.id, t.Baby.BabyId, 0, Data.T0, Data.Sleep(T(x.i * 7), null))])))));
        Assert.All(res, r => Assert.Equal("APPLIED", r.Results.Single().Status));
        Assert.Equal(1, await Env.ScalarAsync<long>("SELECT count(*) FROM nina.sleep_session WHERE baby_id=@b AND end_at IS NULL", ("b", t.Baby.BabyId)));
        var open = await Env.ScalarAsync<Guid>("SELECT id FROM nina.sleep_session WHERE baby_id=@b AND end_at IS NULL", ("b", t.Baby.BabyId));
        Assert.Equal(ms.Last().id, open);                                                 // fica aberta a de início mais recente
        Assert.True((await Env.LogStatsAsync(t.Baby.BabyId)).Contiguous);
    }

    // ------------------------------------------------------------ WakeEvent

    [Fact]
    public async Task WakeEvent_syncs_derives_night_awakenings_and_cascades_tombstones_with_the_session()
    {
        var t = await Trio.CreateAsync(Env);
        var s = t.A.Create("SLEEP_SESSION", Data.Sleep(T(0), T(480), "NIGHT"));
        var w1 = t.A.Create("WAKE_EVENT", Data.Wake(s, T(120), T(135), "INFERRED"));
        var w2 = t.A.Create("WAKE_EVENT", Data.Wake(s, T(240), T(250)));
        Assert.All((await t.A.SyncAsync()).Results, r => Assert.Equal("APPLIED", r.Status));
        await t.B.SyncAsync();
        Assert.NotNull(t.B.Get("WAKE_EVENT", w1));
        Assert.Equal(2, await Env.ScalarAsync<int>("SELECT nina.night_awakenings(@s)", ("s", s)));               // derivado: 2 despertares
        Assert.Equal(900, await Env.ScalarAsync<int>("SELECT duration_seconds FROM nina.wake_event WHERE id=@i", ("i", w1)));

        // correção manual de despertar INFERRED preserva o original
        t.B.Update("WAKE_EVENT", w1, FakeDevice.D(("ended_at", T(140))));
        await t.B.SyncAsync();
        Assert.True(await Env.ScalarAsync<bool>("SELECT manually_corrected AND source='MANUAL' AND original_ended_at = @o FROM nina.wake_event WHERE id=@i",
            ("o", T(135).UtcDateTime), ("i", w1)));

        // fora dos limites da sessão => rejeitado pela API (o banco não impõe)
        var bad = t.B.Create("WAKE_EVENT", Data.Wake(s, T(470), T(500)));
        Assert.Equal("VALIDATION_FAILED", Assert.Single((await t.B.SyncAsync()).Results).Problem!.Code);
        _ = bad;

        // B (offline, desatualizado) cria outro despertar e edita w2 enquanto A exclui a sessão
        t.B.Online = false;
        t.B.Create("WAKE_EVENT", Data.Wake(s, T(300), T(310)));
        t.B.Update("WAKE_EVENT", w2, FakeDevice.D(("ended_at", T(255))));
        t.A.Delete("SLEEP_SESSION", s);
        await t.A.SyncAsync();

        // feed de B: tombstone da sessão E de cada despertar (cascata), sem conteúdo
        var page = await Env.Service.PullAsync(t.B.Auth, t.Baby.BabyId, t.B.Cursor);
        var tombs = page.Changes.Where(c => c.Op == "TOMBSTONE").ToList();
        Assert.Equal(3, tombs.Count);
        Assert.Equal(2, tombs.Count(c => c.EntityType == "WAKE_EVENT"));
        Assert.Contains(tombs, c => c.EntityType == "SLEEP_SESSION" && c.EntityId == s);
        Assert.All(tombs, c => Assert.Null(c.Entity));
        Assert.Equal(3, tombs.Select(c => c.Version).Distinct().Count());
        Assert.Equal(3, await Env.ScalarAsync<long>("SELECT count(*) FROM nina.tombstone WHERE baby_id=@b AND entity_type IN ('SLEEP_SESSION','WAKE_EVENT')", ("b", t.Baby.BabyId)));
        Assert.Equal(0, await Env.ScalarAsync<long>("SELECT count(*) FROM nina.wake_event WHERE sleep_session_id=@s AND deleted_at IS NULL", ("s", s)));
        Assert.True(await Env.ScalarAsync<bool>("SELECT nina.night_awakenings(@s) IS NULL", ("s", s)));            // sessão excluída => dados insuficientes

        // B volta: criar despertar em sessão com tombstone e editar despertar com tombstone => ENTITY_DELETED (o banco aceitaria o INSERT)
        t.B.Online = true;
        var rep = await t.B.SyncAsync();
        Assert.All(rep.Results, r => Assert.Equal("ENTITY_DELETED", r.Problem!.Code));
        Assert.Equal(0, await Env.ScalarAsync<long>("SELECT count(*) FROM nina.wake_event WHERE sleep_session_id=@s AND deleted_at IS NULL", ("s", s)));
        await t.SyncBothTwiceAsync();
        Assert.Null(t.B.Get("WAKE_EVENT", w1));
        await t.AssertConvergedAsync(t.A, t.B);
    }

    [Fact]
    public async Task Direct_database_insert_of_wake_event_on_tombstoned_session_is_not_blocked_by_the_schema()
    {
        // Divergência registrada em specs/sync-spike.md: o FK composto só exige que a linha da sessão exista (tombstone também é linha).
        var t = await Trio.CreateAsync(Env);
        var s = t.A.Create("SLEEP_SESSION", Data.Sleep(T(0), T(60)));
        await t.A.SyncAsync();
        t.A.Delete("SLEEP_SESSION", s);
        await t.A.SyncAsync();
        await Env.NonQueryAsync("INSERT INTO nina.wake_event (id, baby_id, sleep_session_id, started_at, ended_at, tz, source) VALUES (@i, @b, @s, @st, @en, 'UTC', 'MANUAL')",
            ("i", Guid.NewGuid()), ("b", t.Baby.BabyId), ("s", s), ("st", T(10).UtcDateTime), ("en", T(20).UtcDateTime));
        Assert.Equal(1, await Env.ScalarAsync<long>("SELECT count(*) FROM nina.wake_event WHERE sleep_session_id=@s AND deleted_at IS NULL", ("s", s)));
    }
}
