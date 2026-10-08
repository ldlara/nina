using System.Text.Json;
using System.Text.Json.Nodes;
using Nina.SyncSpike.Harness;
using Nina.SyncSpike.Server;
using Xunit;

namespace Nina.SyncSpike.Tests;

[Collection("pg")]
public sealed class ScenarioTests(SpikeFixture fx)
{
    private SpikeEnv Env => fx.Env;
    private static DateTimeOffset T(int minutes) => Data.T0.AddMinutes(minutes);

    // ---------------------------------------------------------------- 1) dois cuidadores offline

    [Fact]
    public async Task Two_caregivers_edit_offline_and_converge()
    {
        var t = await Trio.CreateAsync(Env);
        t.A.Online = false; t.B.Online = false;
        t.A.Create("DIAPER_EVENT", Data.Diaper(T(10), "WET"));
        var sa = t.A.Create("SLEEP_SESSION", Data.Sleep(T(20), T(80), "NAP", "A dormiu"));
        t.A.Create("FEEDING_SESSION", Data.Bottle(T(100)));
        t.B.Create("DIAPER_EVENT", Data.Diaper(T(15), "DIRTY"));
        var sb = t.B.Create("SLEEP_SESSION", Data.Sleep(T(200), T(260), "NAP"));
        t.B.Create("PUMPING_SESSION", Data.Pump(T(300)));
        Assert.Equal("OFFLINE", (await t.A.SyncAsync()).PullError);   // offline: nada vai ao servidor
        Assert.Equal(0, await Env.ScalarAsync<long>("SELECT count(*) FROM nina.change_log WHERE baby_id = @b AND entity_type <> 'BABY'", ("b", t.Baby.BabyId)));

        t.A.Online = true; t.B.Online = true;
        await t.SyncBothTwiceAsync();

        Assert.Equal(6 + 1, t.A.Local.Count);                         // 6 eventos + BABY
        Assert.NotNull(t.B.Get("SLEEP_SESSION", sa));
        Assert.NotNull(t.A.Get("SLEEP_SESSION", sb));
        await t.AssertConvergedAsync(t.A, t.B);
        var st = await Env.LogStatsAsync(t.Baby.BabyId);
        Assert.True(st.Contiguous && st.Min == 1 && st.Max == st.Head);
        // version da entidade = sync_sequence da última mudança (INV-19): únicas por bebê
        Assert.Equal(await Env.ScalarAsync<long>("SELECT count(*) FROM nina.change_log WHERE baby_id=@b", ("b", t.Baby.BabyId)), st.Head);
        // autoria/dispositivo gravados pelo servidor a partir do contexto da requisição
        Assert.Equal(0, await Env.ScalarAsync<long>("SELECT count(*) FROM nina.change_log WHERE baby_id=@b AND entity_type<>'BABY' AND (device_id IS NULL OR actor_user_id IS NULL)", ("b", t.Baby.BabyId)));
        Assert.Equal(new[] { t.A.DeviceId, t.B.DeviceId }.OrderBy(x => x),
            (await Env.ScalarAsync<Guid[]>("SELECT array_agg(DISTINCT device_id) FROM nina.change_log WHERE baby_id=@b AND entity_type<>'BABY'", ("b", t.Baby.BabyId))).OrderBy(x => x));
    }

    // ---------------------------------------------------------------- 2) conflito LWW por campo + auditoria

    private async Task<(Trio T, Guid S)> ConflictSetupAsync(ConflictOrder order = ConflictOrder.ServerArrival)
    {
        var t = await Trio.CreateAsync(Env, svc: Env.NewService(new SyncOptions { ConflictOrder = order }));
        t.A.Now = () => T(1);
        var s = t.A.Create("SLEEP_SESSION", Data.Sleep(T(0), T(30), "NAP", "original", null));
        await t.SyncBothTwiceAsync();
        t.A.Online = false; t.B.Online = false;
        t.A.Now = () => T(100); t.A.Update("SLEEP_SESSION", s, FakeDevice.D(("notes", "nota de A"), ("end_at", T(45))));
        t.B.Now = () => T(110); t.B.Update("SLEEP_SESSION", s, FakeDevice.D(("notes", "nota de B"), ("method_or_place", "colo")));
        t.A.Online = true; t.B.Online = true;
        return (t, s);
    }

    [Theory]
    [InlineData(ConflictOrder.ServerArrival)]
    [InlineData(ConflictOrder.ClientClockClamped)]
    public async Task Conflict_lww_per_field_a_first_then_b_newer_edit_wins_contested_field(ConflictOrder order)
    {
        var (t, s) = await ConflictSetupAsync(order);
        await t.A.SyncAsync();
        var rb = (await t.B.SyncAsync()).Results.Single();
        Assert.Equal("APPLIED", rb.Status);
        Assert.Equal("LWW_CLIENT_WON", rb.Resolution);
        var c = Assert.Single(rb.Conflicts!);
        Assert.Equal(("notes", "CLIENT"), (c.Field, c.Kept));
        Assert.NotNull(rb.Entity);                                      // estado canônico volta ao cliente
        await t.SyncBothTwiceAsync();
        var fin = t.A.Get("SLEEP_SESSION", s)!;
        Assert.Equal("nota de B", Data.S(fin, "notes"));               // LWW: B é mais novo (T+110 > T+100)
        Assert.Equal("colo", Data.S(fin, "method_or_place"));           // campo só de B
        Assert.Equal(T(45).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss"), DateTimeOffset.Parse(Data.S(fin, "end_at")).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss")); // campo só de A
        await t.AssertConvergedAsync(t.A, t.B);
        await AssertAuditWithoutValuesAsync(t, s, "nota de A", "nota de B");
    }

    [Fact]
    public async Task Conflict_with_client_clock_policy_is_independent_of_arrival_order()
    {
        var (t, s) = await ConflictSetupAsync(ConflictOrder.ClientClockClamped);
        await t.B.SyncAsync();
        var ra = (await t.A.SyncAsync()).Results.Single();               // A chega depois, mas é mais velho
        Assert.Equal("LWW_SERVER_WON", ra.Resolution);
        var c = Assert.Single(ra.Conflicts!);
        Assert.Equal(("notes", "SERVER"), (c.Field, c.Kept));
        await t.SyncBothTwiceAsync();
        var fin = t.B.Get("SLEEP_SESSION", s)!;
        Assert.Equal("nota de B", Data.S(fin, "notes"));               // mesmo resultado final da ordem inversa
        Assert.Equal("colo", Data.S(fin, "method_or_place"));
        Assert.Equal(T(45).UtcDateTime, DateTimeOffset.Parse(Data.S(fin, "end_at")).UtcDateTime);   // end_at de A aplicado mesmo chegando por último
        await t.AssertConvergedAsync(t.A, t.B);
    }

    [Fact]
    public async Task Conflict_with_server_arrival_policy_last_to_arrive_wins_even_if_edited_earlier()
    {
        // Contrato v1.0.1 (SR-014): A editou ANTES (T+100) mas chega DEPOIS de B (T+110) => A vence o campo em conflito.
        var (t, s) = await ConflictSetupAsync(ConflictOrder.ServerArrival);
        await t.B.SyncAsync();
        var ra = (await t.A.SyncAsync()).Results.Single();
        Assert.Equal("LWW_CLIENT_WON", ra.Resolution);                              // "a mutação recebida agora prevaleceu"
        Assert.Equal(("notes", "CLIENT"), (ra.Conflicts![0].Field, ra.Conflicts[0].Kept));
        Assert.NotNull(ra.ServerReceivedAt);
        await t.SyncBothTwiceAsync();
        var fin = t.B.Get("SLEEP_SESSION", s)!;
        Assert.Equal("nota de A", Data.S(fin, "notes"));                           // a edição mais antiga venceu: consequência de produto aceita no contrato
        Assert.Equal("colo", Data.S(fin, "method_or_place"));                      // campos disjuntos continuam combinados
        await t.AssertConvergedAsync(t.A, t.B);
    }

    [Fact]
    public async Task Disjoint_fields_merge_without_conflict()
    {
        var t = await Trio.CreateAsync(Env);
        var s = t.A.Create("SLEEP_SESSION", Data.Sleep(T(0), T(30)));
        await t.SyncBothTwiceAsync();
        t.A.Update("SLEEP_SESSION", s, FakeDevice.D(("notes", "x")));
        t.B.Update("SLEEP_SESSION", s, FakeDevice.D(("method_or_place", "berco")));
        await t.A.SyncAsync();
        var rb = (await t.B.SyncAsync()).Results.Single();
        Assert.Equal("MERGED", rb.Resolution);
        Assert.Null(rb.Conflicts);
        await t.SyncBothTwiceAsync();
        await t.AssertConvergedAsync(t.A, t.B);
        Assert.Equal("x", Data.S(t.B.Get("SLEEP_SESSION", s), "notes"));
        Assert.Equal("berco", Data.S(t.A.Get("SLEEP_SESSION", s), "method_or_place"));
    }

    [Fact]
    public async Task Field_level_merge_can_violate_a_cross_field_invariant_and_the_whole_mutation_is_rejected()
    {
        // Limitação documentada: LWW por CAMPO não enxerga INV-01 (end_at >= start_at). Cada edição é válida isoladamente,
        // a combinação não é. O banco recusa (CHECK) e a edição do segundo dispositivo é perdida com VALIDATION_FAILED.
        var t = await Trio.CreateAsync(Env);
        var s = t.A.Create("SLEEP_SESSION", Data.Sleep(T(0), T(60)));
        await t.SyncBothTwiceAsync();
        t.A.Update("SLEEP_SESSION", s, FakeDevice.D(("end_at", T(30))));        // [0,30]: válido
        t.B.Update("SLEEP_SESSION", s, FakeDevice.D(("start_at", T(45))));      // [45,60]: válido
        await t.A.SyncAsync();
        var rb = (await t.B.SyncAsync()).Results.Single();
        Assert.Equal(("REJECTED", "VALIDATION_FAILED", false), (rb.Status, rb.Problem!.Code, rb.Retryable ?? true));
        // achado: o erro vem do trigger sleep_overlap_guard (tstzrange invertido => SQLSTATE 22000) ANTES do CHECK sleep_interval_ck (23514),
        // mesmo com a política ACCEPT_AND_WARN; o servidor mapeia ambos para VALIDATION_FAILED, mas o `field` do erro fica pouco útil.
        Assert.Contains(Assert.Single(rb.Problem.Errors!).Code, new[] { "22000", "23514" });
        await t.SyncBothTwiceAsync();
        Assert.Equal(T(0).UtcDateTime, DateTimeOffset.Parse(Data.S(t.B.Get("SLEEP_SESSION", s), "start_at")).UtcDateTime);   // edição de B perdida; B converge para o estado de A
        await t.AssertConvergedAsync(t.A, t.B);
    }

    [Fact]
    public async Task Same_device_stale_base_version_is_not_a_conflict()
    {
        var t = await Trio.CreateAsync(Env);
        var s = t.A.Create("SLEEP_SESSION", Data.Sleep(T(0), null));
        await t.A.SyncAsync();
        t.A.Online = false;
        t.A.Now = () => T(10); t.A.Update("SLEEP_SESSION", s, FakeDevice.D(("notes", "primeira")));
        t.A.Now = () => T(20); t.A.Update("SLEEP_SESSION", s, FakeDevice.D(("notes", "segunda"), ("end_at", T(30))));   // mesmo base_version
        t.A.Online = true;
        var rep = await t.A.SyncAsync();
        Assert.All(rep.Results, r => { Assert.Equal("APPLIED", r.Status); Assert.Equal("NONE", r.Resolution); Assert.Null(r.Conflicts); });
        Assert.Equal("segunda", Data.S(t.A.Get("SLEEP_SESSION", s), "notes"));
    }

    [Fact]
    public async Task Concurrent_delete_wins_over_edit_and_edit_after_delete_is_rejected()
    {
        var t = await Trio.CreateAsync(Env);
        var d = t.A.Create("DIAPER_EVENT", Data.Diaper(T(5), "WET", "n"));
        await t.SyncBothTwiceAsync();
        t.B.Update("DIAPER_EVENT", d, FakeDevice.D(("diaper_type", "MIXED")));       // B edita (offline)
        await t.B.SyncAsync();                                                       // edição já no servidor
        t.A.Delete("DIAPER_EVENT", d);                                               // A deleta com base antigo
        var ra = (await t.A.SyncAsync()).Results.Single();
        Assert.Equal("DELETE_WINS", ra.Resolution);                                  // D-36
        Assert.Equal(1, await Env.ScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE action='sync.conflict_resolved' AND entity_id=@e AND metadata_safe->>'resolution'='DELETE_WINS'", ("e", d)));
    }

    private async Task AssertAuditWithoutValuesAsync(Trio t, Guid entity, params string[] forbidden)
    {
        var rows = await Env.ScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE action='sync.conflict_resolved' AND entity_id=@e AND baby_id=@b",
            ("e", entity), ("b", t.Baby.BabyId));
        Assert.True(rows >= 1, "conflito deve gerar audit_event (INV-21)");
        var all = await Env.ScalarAsync<string>("SELECT coalesce(string_agg(metadata_safe::text, ' '), '') FROM nina.audit_event WHERE entity_id=@e", ("e", entity));
        foreach (var f in forbidden) Assert.DoesNotContain(f, all);                  // C3: nunca valores de texto livre na auditoria
        Assert.Contains("\"notes\"", all);                                           // só o NOME do campo
    }

    // ---------------------------------------------------------------- 3) idempotência com retry

    [Fact]
    public async Task Retry_after_lost_response_is_duplicate_without_effect()
    {
        var t = await Trio.CreateAsync(Env);
        var s = t.A.Create("SLEEP_SESSION", Data.Sleep(T(0), null));
        t.A.Update("SLEEP_SESSION", s, FakeDevice.D(("end_at", T(40))));
        var first = await t.A.SyncAsync(dropFirstPushResponse: true);                // servidor aplicou, cliente não soube
        Assert.Equal(2, t.A.Outbox.Count);
        var logBefore = (await Env.LogStatsAsync(t.Baby.BabyId)).Head;
        var rep = await t.A.SyncAsync();                                             // reenvio
        Assert.All(rep.Results, r => Assert.Equal("DUPLICATE", r.Status));
        Assert.Empty(t.A.Outbox);
        Assert.Equal(logBefore, (await Env.LogStatsAsync(t.Baby.BabyId)).Head);     // sem efeito: nenhuma sequência nova
        var dupVersion = rep.Results[1].Version!.Value;
        Assert.Equal(await Env.ScalarAsync<long>("SELECT version FROM nina.sleep_session WHERE id=@i", ("i", s)), dupVersion);
        Assert.Equal(2, await Env.ScalarAsync<long>("SELECT count(*) FROM nina.sync_mutation WHERE entity_id=@i", ("i", s)));
        _ = first;
    }

    [Fact]
    public async Task Concurrent_identical_pushes_apply_exactly_once()
    {
        var t = await Trio.CreateAsync(Env);
        var id = Guid.NewGuid();
        var m = new Mutation(Guid.NewGuid(), "CREATE", "DIAPER_EVENT", id, t.Baby.BabyId, 0, Data.T0, Data.Diaper(T(1)));
        var req = new PushRequest(t.A.DeviceId, [m]);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => Env.Service.PushAsync(t.A.Auth, req))));
        var statuses = results.Select(r => r.Results.Single().Status).OrderBy(x => x).ToArray();
        Assert.Equal(1, statuses.Count(s => s == "APPLIED"));
        Assert.Equal(11, statuses.Count(s => s == "DUPLICATE"));
        Assert.Equal(1, await Env.ScalarAsync<long>("SELECT count(*) FROM nina.change_log WHERE entity_id=@i", ("i", id)));
        Assert.Single(results.Select(r => r.Results.Single().Version).Distinct());
        Assert.Equal(1, await Env.ScalarAsync<long>("SELECT count(*) FROM nina.sync_mutation WHERE mutation_id=@m", ("m", m.MutationId)));
    }

    [Fact]
    public async Task Mutation_id_reused_for_a_different_mutation_is_refused()
    {
        var t = await Trio.CreateAsync(Env);
        var mid = Guid.NewGuid();
        var m1 = new Mutation(mid, "CREATE", "DIAPER_EVENT", Guid.NewGuid(), t.Baby.BabyId, 0, Data.T0, Data.Diaper(T(1)));
        await Env.Service.PushAsync(t.A.Auth, new PushRequest(t.A.DeviceId, [m1]));
        var m2 = m1 with { EntityId = Guid.NewGuid() };
        var r = (await Env.Service.PushAsync(t.A.Auth, new PushRequest(t.A.DeviceId, [m2]))).Results.Single();
        Assert.Equal("REJECTED", r.Status);
        Assert.Equal("MUTATION_ID_REUSE", r.Problem!.Code);
    }

    [Fact]
    public async Task Rejected_mutations_are_recorded_and_replayed_deterministically()
    {
        var t = await Trio.CreateAsync(Env);
        var bad = new Mutation(Guid.NewGuid(), "CREATE", "SLEEP_SESSION", Guid.NewGuid(), t.Baby.BabyId, 0, Data.T0, Data.Sleep(T(60), T(10)));  // end < start
        var req = new PushRequest(t.A.DeviceId, [bad]);
        var r1 = (await Env.Service.PushAsync(t.A.Auth, req)).Results.Single();
        Assert.Equal(("REJECTED", "VALIDATION_FAILED", false), (r1.Status, r1.Problem!.Code, r1.Retryable ?? true));
        var r2 = (await Env.Service.PushAsync(t.A.Auth, req)).Results.Single();
        Assert.Equal("VALIDATION_FAILED", r2.Problem!.Code);
        Assert.Equal("REJECTED", await Env.ScalarAsync<string>("SELECT outcome FROM nina.sync_mutation WHERE mutation_id=@m", ("m", bad.MutationId)));
    }

    // ---------------------------------------------------------------- 4) tombstone no feed / 5) ressurreição bloqueada

    [Fact]
    public async Task Delete_becomes_tombstone_in_feed_without_content()
    {
        var t = await Trio.CreateAsync(Env);
        var d = t.A.Create("DIAPER_EVENT", Data.Diaper(T(5), "DIRTY", "SEGREDO-DO-BEBE"));
        await t.SyncBothTwiceAsync();
        Assert.NotNull(t.B.Get("DIAPER_EVENT", d));
        t.A.Delete("DIAPER_EVENT", d);
        await t.A.SyncAsync();
        var page = await Env.Service.PullAsync(t.B.Auth, t.Baby.BabyId, t.B.Cursor);
        var ch = Assert.Single(page.Changes);
        Assert.Equal(("TOMBSTONE", "DIAPER_EVENT", d), (ch.Op, ch.EntityType, ch.EntityId));
        Assert.Null(ch.Entity);
        Assert.NotNull(ch.DeletedAt);
        Assert.DoesNotContain("SEGREDO", JsonSerializer.Serialize(page, Json.Options));
        await t.B.SyncAsync();
        Assert.Null(t.B.Get("DIAPER_EVENT", d));
        // banco: tombstone com expiração de 90 dias; notas limpas na linha-casca
        Assert.Equal(90, await Env.ScalarAsync<int>("SELECT (expires_at::date - deleted_at::date) FROM nina.tombstone WHERE entity_id=@i", ("i", d)));
        Assert.True(await Env.ScalarAsync<bool>("SELECT notes IS NULL FROM nina.diaper_event WHERE id=@i", ("i", d)));
        await t.AssertConvergedAsync(t.A, t.B);
    }

    [Fact]
    public async Task Resurrection_is_blocked_for_update_delete_create_and_direct_sql()
    {
        var t = await Trio.CreateAsync(Env);
        var d = t.A.Create("DIAPER_EVENT", Data.Diaper(T(5)));
        await t.SyncBothTwiceAsync();
        t.A.Delete("DIAPER_EVENT", d);
        await t.A.SyncAsync();
        // B ainda não viu o tombstone e edita offline
        t.B.Update("DIAPER_EVENT", d, FakeDevice.D(("diaper_type", "DRY")));
        t.B.Delete("DIAPER_EVENT", d);
        var rep = await t.B.SyncAsync();
        Assert.All(rep.Results, r => { Assert.Equal("REJECTED", r.Status); Assert.Equal("ENTITY_DELETED", r.Problem!.Code); Assert.False(r.Retryable); });
        Assert.Equal("IGNORED_TOMBSTONE", await Env.ScalarAsync<string>("SELECT outcome FROM nina.sync_mutation WHERE mutation_id=@m", ("m", rep.Results[0].MutationId)));
        // recriar com o mesmo id também não ressuscita
        var c = (await Env.Service.PushAsync(t.A.Auth, new PushRequest(t.A.DeviceId,
            [new Mutation(Guid.NewGuid(), "CREATE", "DIAPER_EVENT", d, t.Baby.BabyId, 0, Data.T0, Data.Diaper(T(5)))]))).Results.Single();
        Assert.Equal("ENTITY_DELETED", c.Problem!.Code);
        Assert.True(await Env.ScalarAsync<bool>("SELECT deleted_at IS NOT NULL FROM nina.diaper_event WHERE id=@i", ("i", d)));
        // última barreira no banco: UPDATE direto como nina_app (trigger NN002)
        await using var conn = await Env.App.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var s = new Npgsql.NpgsqlCommand($"SELECT set_config('nina.user_id', '{t.UserA}', true)", conn, tx)) await s.ExecuteNonQueryAsync();
        await using var u = new Npgsql.NpgsqlCommand("UPDATE nina.diaper_event SET deleted_at = NULL WHERE id = @i", conn, tx);
        u.Parameters.AddWithValue("i", d);
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => u.ExecuteNonQueryAsync());
        Assert.Equal("NN002", ex.SqlState);
    }
}
