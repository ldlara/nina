using System.Text.Json.Nodes;
using Nina.Tracking.Tests.Infrastructure;
using Npgsql;

namespace Nina.Tracking.Tests.Tests;

/// <summary>
/// RF-047 / contrato 1.0.1 (SR-014): conflito por campo (<c>field_versions</c>), ordem de chegada ao servidor, tombstone sem ressurreição,
/// exclusão concorrente e despertares.
/// </summary>
public sealed class ConflictTests(PostgresFixture postgres) : TrackingTestBase(postgres)
{
    private sealed record Setup(Session Ana, Session Bia, Guid Baby);

    private async Task<Setup> TwoCaregiversAsync()
    {
        var ana = await UserAsync();
        var bia = await UserAsync();
        var baby = await BabyAsync(ana);
        await MemberAsync(baby, bia, "CAREGIVER");
        return new Setup(ana, bia, baby);
    }

    private async Task<(Guid Id, long Version)> DiaperAsync(Setup s, string type = "WET", string? notes = "original")
    {
        var id = Guid.NewGuid();
        var created = (await PushOkAsync(s.Ana, Mut.Create(Mut.Diaper, s.Baby, id, Mut.DiaperData(T0, type, notes))))[0];
        return (id, created.Version());
    }

    [Fact]
    public async Task Edits_to_different_fields_by_two_devices_are_merged_without_conflict()
    {
        var s = await TwoCaregiversAsync();
        var (id, v) = await DiaperAsync(s);
        var ana = (await PushOkAsync(s.Ana, Mut.Update(Mut.Diaper, s.Baby, id, v, new JsonObject { ["diaper_type"] = "DIRTY" })))[0];
        Assert.Equal("NONE", ana.Resolution());

        var bia = (await PushOkAsync(s.Bia, Mut.Update(Mut.Diaper, s.Baby, id, v, new JsonObject { ["notes"] = "da Bia" })))[0];    // ainda na versão antiga
        Assert.Equal("APPLIED", bia.Status());
        Assert.Equal("MERGED", bia.Resolution());
        Assert.Empty(bia.ConflictFields());
        Assert.Equal("DIRTY", bia.Entity()!["diaper_type"]!.GetValue<string>());                // o estado canônico traz as duas edições
        Assert.Equal("da Bia", bia.Entity()!["notes"]!.GetValue<string>());
        Assert.Equal(bia.Version(), bia.Entity()!["version"]!.GetValue<long>());
    }

    [Fact]
    public async Task Edits_to_the_same_field_resolve_by_arrival_order_and_report_the_conflict()
    {
        var s = await TwoCaregiversAsync();
        var (id, v) = await DiaperAsync(s);
        await PushOkAsync(s.Ana, Mut.Update(Mut.Diaper, s.Baby, id, v, new JsonObject { ["notes"] = "versão da Ana" }));
        var bia = (await PushOkAsync(s.Bia, Mut.Update(Mut.Diaper, s.Baby, id, v, new JsonObject { ["notes"] = "versão da Bia" })))[0];

        Assert.Equal("APPLIED", bia.Status());
        Assert.Equal("LWW_CLIENT_WON", bia.Resolution());                                        // quem chega por último vence
        Assert.Equal(["notes:CLIENT"], bia.ConflictFields());
        Assert.Equal("versão da Bia", await AdminScalarAsync<string>($"SELECT notes FROM nina.diaper_event WHERE id = '{id}'"));

        // a resolução fica auditada só com NOMES de campo (INV-21/INV-28): o texto da nota nunca vai para a auditoria
        var audit = await AdminRowsAsync("SELECT metadata_safe::text, baby_id, actor_user_id FROM nina.audit_event WHERE action = 'sync.conflict_resolved'");
        Assert.Single(audit);
        Assert.Contains("\"fields\": [\"notes\"]", (string)audit[0][0]!, StringComparison.Ordinal);
        Assert.Contains("LWW_CLIENT_WON", (string)audit[0][0]!, StringComparison.Ordinal);
        Assert.DoesNotContain("versão", (string)audit[0][0]!, StringComparison.Ordinal);
        Assert.Equal(s.Baby, (Guid)audit[0][1]!);
        Assert.Equal(s.Bia.UserId, (Guid)audit[0][2]!);
    }

    [Fact]
    public async Task A_late_offline_edit_wins_over_a_newer_online_edit_because_the_server_arrival_order_decides()
    {
        var s = await TwoCaregiversAsync();
        var (id, v) = await DiaperAsync(s);
        // a Bia edita offline ANTES (relógio dela no passado) mas só sincroniza DEPOIS da Ana (relógio na hora certa): chegou por último, vence
        await PushOkAsync(s.Ana, Mut.Update(Mut.Diaper, s.Baby, id, v, new JsonObject { ["notes"] = "mais nova, online" }, clientAt: DateTimeOffset.UtcNow));
        var bia = (await PushOkAsync(s.Bia, Mut.Update(Mut.Diaper, s.Baby, id, v, new JsonObject { ["notes"] = "mais antiga, offline" }, clientAt: DateTimeOffset.UtcNow.AddHours(-5))))[0];
        Assert.Equal("LWW_CLIENT_WON", bia.Resolution());
        Assert.Equal("mais antiga, offline", await AdminScalarAsync<string>($"SELECT notes FROM nina.diaper_event WHERE id = '{id}'"));
    }

    [Fact]
    public async Task A_stale_base_version_from_the_same_device_is_causal_succession_not_a_conflict()
    {
        var s = await TwoCaregiversAsync();
        var (id, v) = await DiaperAsync(s);
        var first = (await PushOkAsync(s.Ana, Mut.Update(Mut.Diaper, s.Baby, id, v, new JsonObject { ["notes"] = "1" })))[0];
        var second = (await PushOkAsync(s.Ana, Mut.Update(Mut.Diaper, s.Baby, id, v, new JsonObject { ["notes"] = "2" })))[0];   // fila offline: base_version velha
        Assert.Equal("NONE", first.Resolution());
        Assert.Equal("NONE", second.Resolution());
        Assert.Equal("2", await AdminScalarAsync<string>($"SELECT notes FROM nina.diaper_event WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Two_devices_of_the_same_user_conflict_like_two_caregivers()
    {
        var s = await TwoCaregiversAsync();
        var phone = await SecondDeviceAsync(s.Ana);
        var (id, v) = await DiaperAsync(s);
        await PushOkAsync(s.Ana, Mut.Update(Mut.Diaper, s.Baby, id, v, new JsonObject { ["notes"] = "celular 1" }));
        var second = (await PushOkAsync(phone, Mut.Update(Mut.Diaper, s.Baby, id, v, new JsonObject { ["notes"] = "celular 2" })))[0];
        Assert.Equal("LWW_CLIENT_WON", second.Resolution());
        Assert.Equal(["notes:CLIENT"], second.ConflictFields());
    }

    [Fact]
    public async Task An_edit_that_is_up_to_date_with_the_server_is_not_a_conflict()
    {
        var s = await TwoCaregiversAsync();
        var (id, v) = await DiaperAsync(s);
        var ana = (await PushOkAsync(s.Ana, Mut.Update(Mut.Diaper, s.Baby, id, v, new JsonObject { ["notes"] = "da Ana" })))[0];
        var bia = (await PushOkAsync(s.Bia, Mut.Update(Mut.Diaper, s.Baby, id, ana.Version(), new JsonObject { ["notes"] = "da Bia, depois de ler" })))[0];
        Assert.Equal("NONE", bia.Resolution());
        Assert.Empty(bia.ConflictFields());
    }

    [Fact]
    public async Task A_merge_that_breaks_an_interval_invariant_keeps_the_server_value_of_the_atomic_group()
    {
        var s = await TwoCaregiversAsync();
        var id = Guid.NewGuid();
        var created = (await PushOkAsync(s.Ana, Mut.Create(Mut.Sleep, s.Baby, id, Mut.SleepData(T0, null, source: "TIMER"))))[0];
        // a Bia fecha a soneca às T0+1h
        var closed = (await PushOkAsync(s.Bia, Mut.Update(Mut.Sleep, s.Baby, id, created.Version(), new JsonObject { ["end_at"] = Mut.At(T0.AddHours(1)) })))[0];
        // a Ana, sem ter visto, move o início para T0+2h: sozinho valeria, junto com o fim da Bia viraria fim < início
        var moved = (await PushOkAsync(s.Ana, Mut.Update(Mut.Sleep, s.Baby, id, created.Version(), new JsonObject { ["start_at"] = Mut.At(T0.AddHours(2)) })))[0];

        Assert.Equal("APPLIED", moved.Status());
        Assert.Equal("LWW_SERVER_WON", moved.Resolution());                                      // R-12: o valor já aplicado permaneceu
        Assert.Equal(["start_at:SERVER"], moved.ConflictFields());
        Assert.Equal(closed.Version(), moved.Version());                                          // nada novo foi gravado
        Assert.Equal(Mut.At(T0), moved.Entity()!["start_at"]!.GetValue<string>());
        Assert.Equal(Mut.At(T0.AddHours(1)), moved.Entity()!["end_at"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_invalid_edit_that_is_not_caused_by_a_concurrent_edit_is_still_a_validation_error()
    {
        var s = await TwoCaregiversAsync();
        var id = Guid.NewGuid();
        var created = (await PushOkAsync(s.Ana, Mut.Create(Mut.Sleep, s.Baby, id, Mut.SleepData(T0, T0.AddHours(1)))))[0];
        var bad = (await PushOkAsync(s.Ana, Mut.Update(Mut.Sleep, s.Baby, id, created.Version(), new JsonObject { ["end_at"] = Mut.At(T0.AddHours(-1)) })))[0];
        Assert.Equal("REJECTED", bad.Status());
        Assert.Equal("VALIDATION_FAILED", bad.ProblemCode());
        Assert.Equal("data.end_at", bad!["problem"]!["errors"]![0]!["field"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_delete_that_races_with_an_edit_wins_and_is_audited_while_a_plain_delete_is_not()
    {
        var s = await TwoCaregiversAsync();
        var (raced, v) = await DiaperAsync(s);
        var (plain, vPlain) = await DiaperAsync(s);
        await PushOkAsync(s.Bia, Mut.Update(Mut.Diaper, s.Baby, raced, v, new JsonObject { ["notes"] = "edição concorrente" }));

        var results = await PushOkAsync(s.Ana, Mut.Delete(Mut.Diaper, s.Baby, raced, v), Mut.Delete(Mut.Diaper, s.Baby, plain, vPlain));
        Assert.Equal("DELETE_WINS", results[0].Resolution());
        Assert.Equal("NONE", results[1].Resolution());
        Assert.Equal(2L, await AdminScalarAsync<long>("SELECT count(*) FROM nina.diaper_event WHERE deleted_at IS NOT NULL"));
        Assert.Equal(1L, await AdminScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE action = 'sync.conflict_resolved'"));
    }

    [Fact]
    public async Task A_late_update_or_delete_of_a_tombstoned_entity_is_rejected_and_never_resurrects_it()
    {
        var s = await TwoCaregiversAsync();
        var (id, v) = await DiaperAsync(s);
        var deleted = (await PushOkAsync(s.Ana, Mut.Delete(Mut.Diaper, s.Baby, id, v)))[0];
        Assert.Equal("APPLIED", deleted.Status());

        var late = await PushOkAsync(
            s.Bia,
            Mut.Update(Mut.Diaper, s.Baby, id, v, new JsonObject { ["notes"] = "dispositivo atrasado" }),
            Mut.Delete(Mut.Diaper, s.Baby, id, v));
        Assert.All(late, r => Assert.Equal("ENTITY_DELETED", r.ProblemCode()));
        Assert.All(late, r => Assert.False(r!["retryable"]!.GetValue<bool>()));
        Assert.Equal("IGNORED_TOMBSTONE", await AdminScalarAsync<string>($"SELECT outcome FROM nina.sync_mutation WHERE user_id = '{s.Bia.UserId}' ORDER BY received_at LIMIT 1"));

        // a linha continua um tombstone sem conteúdo, e nem o banco aceita ressuscitá-la
        Assert.NotNull(await AdminScalarAsync<DateTime?>($"SELECT deleted_at FROM nina.diaper_event WHERE id = '{id}'"));
        Assert.Null(await AdminScalarAsync<string>($"SELECT notes FROM nina.diaper_event WHERE id = '{id}'"));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => AdminExecAsync($"UPDATE nina.diaper_event SET deleted_at = NULL WHERE id = '{id}'"));
        Assert.Equal("NN002", ex.SqlState);
    }

    [Fact]
    public async Task Deleting_a_sleep_session_tombstones_its_wake_events_and_the_feed_carries_the_three_tombstones()
    {
        var s = await TwoCaregiversAsync();
        var session = Guid.NewGuid();
        var created = await PushOkAsync(
            s.Ana,
            Mut.Create(Mut.Sleep, s.Baby, session, Mut.SleepData(T0, T0.AddHours(9), "NIGHT")),
            Mut.Create(Mut.Wake, s.Baby, Guid.NewGuid(), Mut.WakeData(session, T0.AddHours(2), T0.AddHours(2).AddMinutes(5))),
            Mut.Create(Mut.Wake, s.Baby, Guid.NewGuid(), Mut.WakeData(session, T0.AddHours(5), T0.AddHours(5).AddMinutes(10))));
        var (_, cursor) = await PullAllAsync(s.Bia, s.Baby);

        await PushOkAsync(s.Ana, Mut.Delete(Mut.Sleep, s.Baby, session, created[0].Version()));

        var (delta, _) = await PullAllAsync(s.Bia, s.Baby, cursor);
        Assert.Equal(3, delta.Count);
        Assert.All(delta, c => Assert.Equal("TOMBSTONE", c.Op()));
        Assert.Equal(["SLEEP_SESSION", "WAKE_EVENT", "WAKE_EVENT"], delta.Select(c => c.EntityType()).Order().ToArray());
        Assert.All(delta, c => Assert.Null(c["entity"]));                                         // tombstone não é canal de vazamento
        Assert.Equal(3, delta.Select(c => c["version"]!.GetValue<long>()).Distinct().Count());
        Assert.Equal(0L, await AdminScalarAsync<long>($"SELECT count(*) FROM nina.wake_event WHERE baby_id = '{s.Baby}' AND deleted_at IS NULL"));
        Assert.Equal(3L, await AdminScalarAsync<long>($"SELECT count(*) FROM nina.tombstone WHERE baby_id = '{s.Baby}'"));

        // despertar criado ou editado offline contra a sessão excluída: ENTITY_DELETED
        var late = (await PushOkAsync(s.Bia, Mut.Create(Mut.Wake, s.Baby, Guid.NewGuid(), Mut.WakeData(session, T0.AddHours(3), T0.AddHours(3).AddMinutes(1)))))[0];
        Assert.Equal("ENTITY_DELETED", late.ProblemCode());
    }

    [Fact]
    public async Task A_wake_event_must_live_inside_a_live_session_of_the_same_baby()
    {
        var s = await TwoCaregiversAsync();
        var otherOwner = await UserAsync();
        var otherBaby = await BabyAsync(otherOwner);
        var session = Guid.NewGuid();
        var foreignSession = Guid.NewGuid();
        await PushOkAsync(s.Ana, Mut.Create(Mut.Sleep, s.Baby, session, Mut.SleepData(T0, T0.AddHours(8), "NIGHT")));
        await PushOkAsync(otherOwner, Mut.Create(Mut.Sleep, otherBaby, foreignSession, Mut.SleepData(T0, T0.AddHours(8), "NIGHT")));

        var results = await PushOkAsync(
            s.Ana,
            Mut.Create(Mut.Wake, s.Baby, Guid.NewGuid(), Mut.WakeData(session, T0.AddHours(1), T0.AddHours(1).AddMinutes(5))),     // ok
            Mut.Create(Mut.Wake, s.Baby, Guid.NewGuid(), Mut.WakeData(session, T0.AddMinutes(-30), T0.AddMinutes(10))),             // antes da sessão
            Mut.Create(Mut.Wake, s.Baby, Guid.NewGuid(), Mut.WakeData(session, T0.AddHours(7), T0.AddHours(9))),                    // depois da sessão
            Mut.Create(Mut.Wake, s.Baby, Guid.NewGuid(), Mut.WakeData(foreignSession, T0.AddHours(1), T0.AddHours(1).AddMinutes(5))), // sessão de outro bebê
            Mut.Create(Mut.Wake, s.Baby, Guid.NewGuid(), Mut.WakeData(Guid.NewGuid(), T0.AddHours(1), T0.AddHours(1).AddMinutes(5))), // sessão inexistente
            Mut.Create(Mut.Wake, s.Baby, Guid.NewGuid(), Mut.WakeData(session, T0.AddHours(2), T0.AddHours(1))));                    // fim antes do início
        Assert.Equal("APPLIED", results[0].Status());
        Assert.Equal("VALIDATION_FAILED", results[1].ProblemCode());
        Assert.Equal("OUTSIDE_SESSION", results[1]!["problem"]!["errors"]![0]!["code"]!.GetValue<string>());
        Assert.Equal("VALIDATION_FAILED", results[2].ProblemCode());
        Assert.Equal("ENTITY_NOT_FOUND", results[3].ProblemCode());                               // não revela que existe em outro bebê
        Assert.Equal("ENTITY_NOT_FOUND", results[4].ProblemCode());
        Assert.Equal("VALIDATION_FAILED", results[5].ProblemCode());
        Assert.Equal("END_BEFORE_START", results[5]!["problem"]!["errors"]![0]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Manually_correcting_an_inferred_wake_event_marks_it_manual_and_preserves_the_original()
    {
        var s = await TwoCaregiversAsync();
        var session = Guid.NewGuid();
        var wake = Guid.NewGuid();
        var created = await PushOkAsync(
            s.Ana,
            Mut.Create(Mut.Sleep, s.Baby, session, Mut.SleepData(T0, T0.AddHours(8), "NIGHT")),
            Mut.Create(Mut.Wake, s.Baby, wake, Mut.WakeData(session, T0.AddHours(3), T0.AddHours(3).AddMinutes(20), "INFERRED")));
        Assert.Equal("INFERRED", await AdminScalarAsync<string>($"SELECT source FROM nina.wake_event WHERE id = '{wake}'"));

        var corrected = (await PushOkAsync(s.Ana, Mut.Update(Mut.Wake, s.Baby, wake, created[1].Version(), new JsonObject { ["ended_at"] = Mut.At(T0.AddHours(3).AddMinutes(5)) })))[0];
        Assert.Equal("APPLIED", corrected.Status());
        Assert.Equal("MANUAL", await AdminScalarAsync<string>($"SELECT source FROM nina.wake_event WHERE id = '{wake}'"));
        Assert.True(await AdminScalarAsync<bool>($"SELECT manually_corrected FROM nina.wake_event WHERE id = '{wake}'"));
        Assert.Equal(T0.AddHours(3).AddMinutes(20).UtcDateTime, await AdminScalarAsync<DateTime>($"SELECT original_ended_at FROM nina.wake_event WHERE id = '{wake}'"));
        Assert.Equal(300L, await AdminScalarAsync<int>($"SELECT duration_seconds FROM nina.wake_event WHERE id = '{wake}'"));
    }

    [Fact]
    public async Task Night_awakenings_is_derived_as_null_zero_or_a_count()
    {
        var s = await TwoCaregiversAsync();
        var nap = Guid.NewGuid();
        var longNight = Guid.NewGuid();
        var shortNight = Guid.NewGuid();
        var wakeful = Guid.NewGuid();
        await PushOkAsync(
            s.Ana,
            Mut.Create(Mut.Sleep, s.Baby, nap, Mut.SleepData(T0, T0.AddHours(1), "NAP")),
            Mut.Create(Mut.Sleep, s.Baby, longNight, Mut.SleepData(T0.AddDays(-1), T0.AddDays(-1).AddHours(8), "NIGHT")),          // >= 240 min, sem despertares
            Mut.Create(Mut.Sleep, s.Baby, shortNight, Mut.SleepData(T0.AddDays(-3), T0.AddDays(-3).AddHours(2), "NIGHT")),         // 120 min: acompanhamento insuficiente
            Mut.Create(Mut.Sleep, s.Baby, wakeful, Mut.SleepData(T0.AddDays(-2), T0.AddDays(-2).AddHours(9), "NIGHT")),
            Mut.Create(Mut.Wake, s.Baby, Guid.NewGuid(), Mut.WakeData(wakeful, T0.AddDays(-2).AddHours(2), T0.AddDays(-2).AddHours(2).AddMinutes(5))),
            Mut.Create(Mut.Wake, s.Baby, Guid.NewGuid(), Mut.WakeData(wakeful, T0.AddDays(-2).AddHours(5), T0.AddDays(-2).AddHours(5).AddMinutes(5))));

        async Task<JsonNode?> NightsOf(Guid id) => (await Api.GetAsync($"/v1/babies/{s.Baby}/events/{id}", s.Ana.AccessToken)).Json!["night_awakenings"];
        Assert.Null(await NightsOf(nap));
        Assert.Equal(0, (await NightsOf(longNight))!.GetValue<int>());
        Assert.Null(await NightsOf(shortNight));
        Assert.Equal(2, (await NightsOf(wakeful))!.GetValue<int>());

        // com um despertar a menos o número cai; sem nenhum, volta a 0 (acompanhamento suficiente)
        var wakes = await AdminRowsAsync($"SELECT id, version FROM nina.wake_event WHERE sleep_session_id = '{wakeful}' ORDER BY started_at");
        await PushOkAsync(s.Ana, Mut.Delete(Mut.Wake, s.Baby, (Guid)wakes[0][0]!, (long)wakes[0][1]!));
        Assert.Equal(1, (await NightsOf(wakeful))!.GetValue<int>());
        await PushOkAsync(s.Ana, Mut.Delete(Mut.Wake, s.Baby, (Guid)wakes[1][0]!, (long)wakes[1][1]!));
        Assert.Equal(0, (await NightsOf(wakeful))!.GetValue<int>());
    }
}
