using System.Diagnostics;
using Nina.Database.Tests.Infrastructure;
using Npgsql;

namespace Nina.Database.Tests.Tests;

/// <summary>Recomendações R-01..R-08 e R-10 do spike de sync (ARCH-003) e SR-014 (PKs escopadas), executadas como <c>nina_app</c>.</summary>
[Collection(PgClusterGroup.Name)]
public sealed class SyncSchemaTests(PgCluster cluster) : DbTestBase(cluster)
{
    private static string Sleep(Guid baby, string extra = "", Guid? id = null, string start = "now() - interval '2 hours'", string? end = "now() - interval '1 hour'") =>
        $"INSERT INTO nina.sleep_session (id, baby_id, start_at, end_at, tz, sleep_type, source{(extra.Length > 0 ? ", " + extra.Split('=')[0] : string.Empty)}) " +
        $"VALUES ('{id ?? Guid.NewGuid()}', '{baby}', {start}, {end ?? "NULL"}, 'UTC', 'NAP', 'MANUAL'{(extra.Length > 0 ? ", " + extra.Split('=', 2)[1] : string.Empty)})";

    // ------------------------------------------------------------------ R-01

    [Fact]
    public async Task R01_every_mutable_entity_has_field_versions_defaulting_to_an_empty_object()
    {
        foreach (var table in new[] { "baby", "sleep_session", "feeding_session", "pumping_session", "diaper_event", "wake_event", "sleep_schedule_preference" })
        {
            Assert.Equal("jsonb|NO", await OwnerScalarAsync<string>(
                $"SELECT data_type || '|' || is_nullable FROM information_schema.columns WHERE table_schema = 'nina' AND table_name = '{table}' AND column_name = 'field_versions'"));
        }

        var id = Guid.NewGuid();
        await using var erin = await AsApp(W.Erin);
        await erin.ExecAsync(Sleep(W.BabyA, id: id));
        Assert.Equal("{}", await erin.ScalarAsync<string>($"SELECT field_versions::text FROM nina.sleep_session WHERE baby_id = '{W.BabyA}' AND id = '{id}'"));
        Assert.Equal(1, await erin.ExecAsync(
            $"UPDATE nina.sleep_session SET notes = 'x', field_versions = '{{\"notes\": [7, \"0b8f3c1e\"]}}' WHERE baby_id = '{W.BabyA}' AND id = '{id}'"));
        Assert.Equal("[7, \"0b8f3c1e\"]", await erin.ScalarAsync<string>($"SELECT (field_versions->'notes')::text FROM nina.sleep_session WHERE baby_id = '{W.BabyA}' AND id = '{id}'"));
    }

    [Fact]
    public async Task R01_field_versions_is_bounded_must_be_an_object_and_is_dropped_with_the_content_on_delete()
    {
        var id = Guid.NewGuid();
        await OwnerAsync(Sleep(W.BabyA, id: id));
        Assert.Equal("23514", await FailsAsync(Role.App, W.Erin, $"UPDATE nina.sleep_session SET field_versions = '[1]' WHERE baby_id = '{W.BabyA}' AND id = '{id}'"));
        Assert.Equal("23514", await FailsAsync(Role.App, W.Erin, $"UPDATE nina.sleep_session SET field_versions = jsonb_build_object('k', repeat('x', 3000)) WHERE baby_id = '{W.BabyA}' AND id = '{id}'"));
        await using (var erin = await AsApp(W.Erin))
        {
            await erin.ExecAsync($"UPDATE nina.sleep_session SET notes = 'segredo', field_versions = '{{\"notes\": [3, \"d\"]}}' WHERE baby_id = '{W.BabyA}' AND id = '{id}'");
            await erin.ExecAsync($"UPDATE nina.sleep_session SET deleted_at = now() WHERE baby_id = '{W.BabyA}' AND id = '{id}'");
            await erin.CommitAsync();
        }

        Assert.Equal("{}", await OwnerScalarAsync<string>($"SELECT field_versions::text FROM nina.sleep_session WHERE id = '{id}'"));
        Assert.Null(await OwnerScalarAsync<string>($"SELECT notes FROM nina.sleep_session WHERE id = '{id}'"));
    }

    // ------------------------------------------------------------------ R-02

    [Fact]
    public async Task R02_the_app_reads_the_sync_head_only_for_babies_it_can_read()
    {
        await OwnerAsync(Sleep(W.BabyA));
        await using (var carol = await AsApp(W.Carol))
        {
            var head = (await carol.RowsAsync($"SELECT last_sequence, purged_through FROM nina.sync_head('{W.BabyA}')")).Single();
            Assert.True((long)head[0]! >= 1);
            Assert.Equal(0L, head[1]);
            Assert.Empty(await carol.RowsAsync($"SELECT * FROM nina.sync_head('{W.BabyB}')"));
        }

        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol, "SELECT * FROM nina.baby_sync_head"));
        await using var none = await AsApp(null);
        Assert.Empty(await none.RowsAsync($"SELECT * FROM nina.sync_head('{W.BabyA}')"));
    }

    // ------------------------------------------------------------------ R-03

    [Fact]
    public async Task R03_set_based_policies_keep_the_same_semantics_and_scale_with_the_number_of_events()
    {
        await OwnerAsync(
            $"""
            INSERT INTO nina.sleep_session (id, baby_id, start_at, end_at, tz, sleep_type, source)
            SELECT gen_random_uuid(), '{W.BabyA}', now() - (g || ' minutes')::interval - interval '30 seconds', now() - (g || ' minutes')::interval, 'UTC', 'NAP', 'MANUAL'
              FROM generate_series(1, 4000) g;
            """);
        Assert.Equal(4000, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.sleep_session WHERE baby_id = '{W.BabyA}'"));
        foreach (var (user, expected) in new[] { (W.Alice, 4000L), (W.Erin, 4000L), (W.Carol, 4000L), (W.Bob, 0L), (W.Dave, 0L) })
        {
            await using var s = await AsApp(user);
            Assert.Equal(expected, await s.ScalarAsync<long>($"SELECT count(*) FROM nina.sleep_session WHERE baby_id = '{W.BabyA}'"));
        }

        await using var carol = await AsApp(W.Carol);
        var plan = string.Join('\n', (await carol.RowsAsync($"EXPLAIN SELECT * FROM nina.sleep_session WHERE baby_id = '{W.BabyA}'")).Select(r => (string)r[0]!));
        Assert.DoesNotContain("can_read_baby", plan, StringComparison.Ordinal);          // nada de funcao por linha
        Assert.Contains("SubPlan", plan, StringComparison.Ordinal);                      // conjunto avaliado uma vez (hashed SubPlan)

        var watch = Stopwatch.StartNew();
        Assert.Equal(4000, (await carol.RowsAsync($"SELECT id FROM nina.sleep_session WHERE baby_id = '{W.BabyA}' ORDER BY id")).Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"leitura de 4000 linhas levou {watch.Elapsed}");

        // escrita: Carol (READ_ONLY) segue sem escrever; Erin (CAREGIVER) escreve; o conjunto vale para INSERT e UPDATE
        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol, Sleep(W.BabyA)));
        Assert.Null(await FailsAsync(Role.App, W.Erin, Sleep(W.BabyA)));
        await using var erin = await AsApp(W.Erin);
        Assert.Equal(4000, await erin.ExecAsync($"UPDATE nina.sleep_session SET notes = 'x' WHERE baby_id = '{W.BabyA}'"));
    }

    [Fact]
    public async Task R03_tombstones_and_the_change_feed_follow_the_same_membership_rules()
    {
        var id = Guid.NewGuid();
        await OwnerAsync(Sleep(W.BabyA, id: id));
        await OwnerAsync($"UPDATE nina.sleep_session SET deleted_at = now() WHERE baby_id = '{W.BabyA}' AND id = '{id}'");
        foreach (var (user, expected) in new[] { (W.Carol, 1L), (W.Dave, 0L), (W.Bob, 0L) })
        {
            await using var s = await AsApp(user);
            Assert.Equal(expected, await s.ScalarAsync<long>($"SELECT count(*) FROM nina.tombstone WHERE baby_id = '{W.BabyA}'"));
            Assert.Equal(expected * 3, await s.ScalarAsync<long>($"SELECT count(*) FROM nina.change_log WHERE baby_id = '{W.BabyA}'"));   // bebe + criacao + exclusao
        }

        Assert.Equal("42501", await FailsAsync(Role.App, W.Erin, $"DELETE FROM nina.change_log WHERE baby_id = '{W.BabyA}'"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Erin, $"INSERT INTO nina.tombstone (baby_id, entity_type, entity_id, deleted_at, version, expires_at) VALUES ('{W.BabyA}', 'SLEEP_SESSION', gen_random_uuid(), now(), 1, now())"));
    }

    // ------------------------------------------------------------------ R-04 / SR-014

    [Fact]
    public async Task R04_an_entity_id_is_scoped_by_baby_so_it_never_reveals_or_collides_with_another_tenant()
    {
        var shared = Guid.NewGuid();
        Assert.Null(await FailsAsync(Role.App, W.Alice, Sleep(W.BabyA, id: shared)));
        Assert.Null(await FailsAsync(Role.App, W.Bob, Sleep(W.BabyB, id: shared)));                  // mesmo id em outro bebe: sem 23505 nem oraculo
        Assert.Equal("23505", await FailsAsync(Role.App, W.Alice, Sleep(W.BabyA, id: W.SleepB) + "; " + Sleep(W.BabyA, id: W.SleepB)));      // mesmo id no MESMO bebe: ENTITY_ID_UNAVAILABLE
        Assert.Equal("{baby_id,id}", await OwnerScalarAsync<string>(
            "SELECT array_agg(a.attname ORDER BY k.ord)::text FROM pg_index i CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY k(attnum, ord) JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum WHERE i.indrelid = 'nina.sleep_session'::regclass AND i.indisprimary"));
    }

    [Fact]
    public async Task R04_the_mutation_id_is_scoped_by_user_and_device_and_a_collision_is_never_invisible()
    {
        var mutation = Guid.NewGuid();
        var device = Guid.NewGuid();
        string Insert(Guid user, Guid baby, string conflict = "") =>
            $"INSERT INTO nina.sync_mutation (mutation_id, baby_id, user_id, device_id, entity_type, entity_id, op, client_created_at, outcome) VALUES ('{mutation}', '{baby}', '{user}', '{device}', 'SLEEP_SESSION', gen_random_uuid(), 'CREATE', now(), 'APPLIED') {conflict}";
        await using (var bob = await AsApp(W.Bob))
        {
            await bob.ExecAsync(Insert(W.Bob, W.BabyB));
            await bob.CommitAsync();
        }

        // Alice usa o mesmo mutation_id (e ate o mesmo device_id): nao "queima" o do Bob nem descobre que existe
        await using (var alice = await AsApp(W.Alice))
        {
            await alice.ExecAsync(Insert(W.Alice, W.BabyA));
            await alice.CommitAsync();
        }

        Assert.Equal("23505", await FailsAsync(Role.App, W.Alice, Insert(W.Alice, W.BabyA)));                                // replay do proprio usuario/dispositivo
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, Insert(W.Bob, W.BabyA)));                                 // nao grava em nome de outro
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, Insert(W.Alice, W.BabyB)));                                // nem em bebe alheio

        // a linha propria segue visivel mesmo apos perder o acesso ao bebe (o replay nunca fica invisivel), mas nao se grava de novo
        await using (var erin = await AsApp(W.Erin))
        {
            Assert.Equal(1, await erin.ExecAsync(Insert(W.Erin, W.BabyA, "ON CONFLICT DO NOTHING")));
            Assert.Equal(0, await erin.ExecAsync(Insert(W.Erin, W.BabyA, "ON CONFLICT DO NOTHING")));         // replay: 0 linhas = DUPLICATE
            await erin.CommitAsync();
        }

        await using (var leave = await AsApp(W.Erin))
        {
            await leave.ExecAsync($"SELECT nina.leave_baby('{W.BabyA}')");
            await leave.CommitAsync();
        }

        await using var erinAfter = await AsApp(W.Erin);
        Assert.Equal(1, await erinAfter.ScalarAsync<long>($"SELECT count(*) FROM nina.sync_mutation WHERE mutation_id = '{mutation}'"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Erin, Insert(W.Erin, W.BabyA, "ON CONFLICT DO NOTHING")));
    }

    // ------------------------------------------------------------------ R-05

    [Fact]
    public async Task R05_two_open_timers_are_kept_and_flagged_instead_of_rejected_by_a_unique_index()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await using (var alice = await AsApp(W.Alice))
        {
            await alice.ExecAsync(Sleep(W.BabyA, id: a, start: "now() - interval '30 minutes'", end: null));
            await alice.ExecAsync(Sleep(W.BabyA, id: b, start: "now() - interval '10 minutes'", end: null));      // segundo aparelho: KEPT_BOTH
            await alice.CommitAsync();
        }

        await using var carol = await AsApp(W.Carol);
        Assert.Equal(2, await carol.ScalarAsync<long>($"SELECT count(*) FROM nina.sleep_session WHERE baby_id = '{W.BabyA}' AND end_at IS NULL AND deleted_at IS NULL"));
        Assert.Equal(b, await carol.ScalarAsync<Guid>($"SELECT * FROM nina.sleep_overlaps('{W.BabyA}', '{a}')"));        // a API sinaliza OPEN_SLEEP_EXISTS com estes ids
        Assert.Equal(a, await carol.ScalarAsync<Guid>($"SELECT * FROM nina.sleep_overlaps('{W.BabyA}', '{b}')"));
        Assert.Equal(0, await OwnerScalarAsync<long>("SELECT count(*) FROM pg_indexes WHERE schemaname = 'nina' AND tablename = 'sleep_session' AND indexdef ILIKE '%UNIQUE%WHERE%end_at IS NULL%'"));
    }

    [Fact]
    public async Task R05_with_the_reject_policy_the_second_open_timer_and_any_overlap_are_refused_but_adjacent_sessions_are_not()
    {
        await using (var admin = await OpenAsync(Role.Config, W.Alice))
        {
            await admin.ExecAsync("UPDATE nina.app_parameter SET value = '\"REJECT\"'::jsonb WHERE param_key = 'sleep.overlap_policy'");
            await admin.CommitAsync();
        }

        await using (var alice = await AsApp(W.Alice))
        {
            await alice.ExecAsync(Sleep(W.BabyA, start: "now() - interval '10 hours'", end: "now() - interval '9 hours'"));
            await alice.ExecAsync(Sleep(W.BabyA, start: "now() - interval '9 hours'", end: "now() - interval '8 hours'"));      // adjacente (fim = inicio): ok
            await alice.ExecAsync(Sleep(W.BabyA, start: "now() - interval '30 minutes'", end: null));
            await alice.CommitAsync();
        }

        Assert.Equal("NN006", await FailsAsync(Role.App, W.Alice, Sleep(W.BabyA, start: "now() - interval '10 minutes'", end: null)));
        Assert.Equal("NN006", await FailsAsync(Role.App, W.Alice, Sleep(W.BabyA, start: "now() - interval '9 hours 30 minutes'", end: "now() - interval '9 hours 10 minutes'")));
    }

    // ------------------------------------------------------------------ R-06 / R-07 / R-08

    [Fact]
    public async Task R06_a_live_wake_event_requires_a_live_sleep_session_of_the_same_baby()
    {
        var session = Guid.NewGuid();
        var dead = Guid.NewGuid();
        await OwnerAsync(Sleep(W.BabyA, id: session));
        await OwnerAsync(Sleep(W.BabyA, id: dead));
        await OwnerAsync($"UPDATE nina.sleep_session SET deleted_at = now() WHERE baby_id = '{W.BabyA}' AND id = '{dead}'");
        string Wake(Guid baby, Guid sess, string extra = "") =>
            $"INSERT INTO nina.wake_event (id, baby_id, sleep_session_id, started_at, ended_at, tz, source{extra}) VALUES (gen_random_uuid(), '{baby}', '{sess}', now() - interval '90 minutes', now() - interval '80 minutes', 'UTC', 'MANUAL')";

        Assert.Null(await FailsAsync(Role.App, W.Erin, Wake(W.BabyA, session)));
        Assert.Equal("NN002", await FailsAsync(Role.App, W.Erin, Wake(W.BabyA, dead)));              // sessao com tombstone: ultima barreira no banco
        Assert.Equal("NN002", await FailsAsync(Role.App, W.Erin, Wake(W.BabyA, W.SleepB)));          // sessao de OUTRO bebe: igual a inexistente (nao revela)
        Assert.Equal("NN002", await FailsAsync(Role.App, W.Erin, Wake(W.BabyA, Guid.NewGuid())));
        Assert.Equal("23503", await FailsAsync(Role.Owner, null, Wake(W.BabyA, W.SleepB).Replace("INSERT INTO nina.wake_event", "ALTER TABLE nina.wake_event DISABLE TRIGGER wake_event_a_session_guard; INSERT INTO nina.wake_event")));   // sem o gatilho, a FK composta ainda barra
    }

    [Fact]
    public async Task R06_deleting_the_session_tombstones_its_wake_events_and_they_cannot_be_revived()
    {
        var session = Guid.NewGuid();
        var wake = Guid.NewGuid();
        await OwnerAsync(
            $"""
            {Sleep(W.BabyA, id: session)};
            INSERT INTO nina.wake_event (id, baby_id, sleep_session_id, started_at, ended_at, tz, source) VALUES ('{wake}', '{W.BabyA}', '{session}', now() - interval '90 minutes', now() - interval '80 minutes', 'UTC', 'MANUAL');
            UPDATE nina.sleep_session SET deleted_at = now() WHERE baby_id = '{W.BabyA}' AND id = '{session}';
            """);
        Assert.NotNull(await OwnerScalarAsync<DateTime?>($"SELECT deleted_at FROM nina.wake_event WHERE id = '{wake}'"));
        Assert.Equal("NN002", await FailsAsync(Role.App, W.Erin, $"UPDATE nina.wake_event SET deleted_at = NULL WHERE baby_id = '{W.BabyA}' AND id = '{wake}'"));
    }

    [Fact]
    public async Task R07_the_wake_event_inherits_tz_from_its_sleep_session_when_omitted()
    {
        var session = Guid.NewGuid();
        await OwnerAsync($"INSERT INTO nina.sleep_session (id, baby_id, start_at, end_at, tz, sleep_type, source) VALUES ('{session}', '{W.BabyA}', now() - interval '3 hours', now() - interval '1 hour', 'America/Sao_Paulo', 'NIGHT', 'MANUAL')");
        await using var erin = await AsApp(W.Erin);
        var wake = await erin.ScalarAsync<string>(
            $"INSERT INTO nina.wake_event (id, baby_id, sleep_session_id, started_at, ended_at, source) VALUES (gen_random_uuid(), '{W.BabyA}', '{session}', now() - interval '2 hours', now() - interval '110 minutes', 'MANUAL') RETURNING tz");
        Assert.Equal("America/Sao_Paulo", wake);
        Assert.Equal(1, await erin.ScalarAsync<int>($"SELECT nina.night_awakenings('{W.BabyA}', '{session}')"));
    }

    [Fact]
    public async Task R08_an_inverted_interval_fails_on_the_check_constraint_even_when_overlaps_are_accepted()
    {
        await using var erin = await AsApp(W.Erin);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => erin.ExecAsync(Sleep(W.BabyA, start: "now()", end: "now() - interval '1 hour'")));
        Assert.Equal("23514", ex.SqlState);
        Assert.Equal("sleep_interval_ck", ex.ConstraintName);                      // antes: 22000 sem o nome da constraint
    }

    // ------------------------------------------------------------------ R-10

    [Fact]
    public async Task R10_text_limits_match_the_v1_0_1_contract_and_the_baby_sex_enum_includes_other()
    {
        string Method(int n) => Sleep(W.BabyA, extra: $"method_or_place='{new string('m', n)}'");
        Assert.Null(await FailsAsync(Role.App, W.Erin, Method(80)));
        Assert.Equal("23514", await FailsAsync(Role.App, W.Erin, Method(81)));
        foreach (var (table, columns, values) in new[]
        {
            ("sleep_session", "start_at, tz, sleep_type, source", "now(), 'UTC', 'NAP', 'MANUAL'"),
            ("feeding_session", "start_at, tz, feeding_type", "now(), 'UTC', 'SOLID'"),
            ("pumping_session", "start_at, tz", "now(), 'UTC'"),
            ("diaper_event", "occurred_at, tz, diaper_type", "now(), 'UTC', 'WET'"),
        })
        {
            string Insert(int n) => $"INSERT INTO nina.{table} (id, baby_id, {columns}, notes) VALUES (gen_random_uuid(), '{W.BabyA}', {values}, '{new string('n', n)}')";
            Assert.Null(await FailsAsync(Role.App, W.Erin, Insert(500)));
            Assert.Equal("23514", await FailsAsync(Role.App, W.Erin, Insert(501)));
        }

        Assert.Null(await FailsAsync(Role.App, W.Alice, $"UPDATE nina.baby SET sex = 'OTHER' WHERE id = '{W.BabyA}'"));
        Assert.Equal("23514", await FailsAsync(Role.App, W.Alice, $"UPDATE nina.baby SET sex = 'ALIEN' WHERE id = '{W.BabyA}'"));
    }

    [Fact]
    public async Task The_app_cannot_rewrite_identity_version_authorship_or_timestamps_of_a_synced_entity()
    {
        var id = Guid.NewGuid();
        await OwnerAsync(Sleep(W.BabyA, id: id));
        foreach (var set in new[] { $"baby_id = '{W.BabyB}'", $"id = '{Guid.NewGuid()}'", "version = 999", "created_by = NULL", "created_at = now()", "updated_at = now()" })
        {
            Assert.Equal("42501", await FailsAsync(Role.App, W.Erin, $"UPDATE nina.sleep_session SET {set} WHERE baby_id = '{W.BabyA}' AND id = '{id}'"));
        }

        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, $"UPDATE nina.baby SET version = 1, created_by = NULL WHERE id = '{W.BabyA}'"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Erin, $"DELETE FROM nina.sleep_session WHERE baby_id = '{W.BabyA}'"));          // exclusao e soft delete
        // o servidor, nao o cliente, atribui a versao: um INSERT com version forjada e sobrescrito pelo gatilho
        await using var erin = await AsApp(W.Erin);
        var forged = Guid.NewGuid();
        var version = await erin.ScalarAsync<long>($"WITH x AS (INSERT INTO nina.sleep_session (id, baby_id, start_at, tz, sleep_type, source, version) VALUES ('{forged}', '{W.BabyA}', now(), 'UTC', 'NAP', 'MANUAL', 12345) RETURNING version) SELECT version FROM x");
        Assert.NotEqual(12345L, version);
    }

    [Fact]
    public async Task A_tombstoned_entity_cannot_be_resurrected_and_the_owner_deletes_a_baby_as_a_tombstone_shell()
    {
        var id = Guid.NewGuid();
        await OwnerAsync(Sleep(W.BabyA, id: id));
        await using (var erin = await AsApp(W.Erin))
        {
            await erin.ExecAsync($"UPDATE nina.sleep_session SET deleted_at = now() WHERE baby_id = '{W.BabyA}' AND id = '{id}'");
            await erin.CommitAsync();
        }

        Assert.Equal("NN002", await FailsAsync(Role.App, W.Erin, $"UPDATE nina.sleep_session SET deleted_at = NULL WHERE baby_id = '{W.BabyA}' AND id = '{id}'"));
        Assert.Equal("NN002", await FailsAsync(Role.App, W.Erin, $"UPDATE nina.sleep_session SET notes = 'volta' WHERE baby_id = '{W.BabyA}' AND id = '{id}'"));

        // NR-07: a exclusao do bebe nao e mais um UPDATE de deleted_at (coluna fora do GRANT do app; gatilho NN052 para qualquer outro papel):
        // so nina.delete_baby (reautenticacao BABY_DELETE, auditoria, aviso aos cuidadores). O tombstone do bebe entra no feed.
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, $"UPDATE nina.baby SET deleted_at = now(), display_name = NULL, birth_date = NULL, field_versions = '{{}}' WHERE id = '{W.BabyA}'"));
        Assert.Equal("NN052", await FailsAsync(Role.Owner, null, $"UPDATE nina.baby SET deleted_at = now(), display_name = NULL, birth_date = NULL, field_versions = '{{}}' WHERE id = '{W.BabyA}'"));
        var reauth = await ReauthAsync(W.Alice, "BABY_DELETE");
        await using (var alice = await AsApp(W.Alice))
        {
            await alice.ExecAsync("SELECT nina.delete_baby(@b, @h, true)", P("b", W.BabyA), Bytes("h", reauth));
            await alice.CommitAsync();
        }

        Assert.Equal(1, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.tombstone WHERE baby_id = '{W.BabyA}' AND entity_type = 'BABY'"));
        Assert.Equal("NN002", await FailsAsync(Role.Owner, null, $"UPDATE nina.baby SET deleted_at = NULL WHERE id = '{W.BabyA}'"));
    }

    [Fact]
    public async Task The_operational_purge_removes_expired_secrets_codes_sessions_and_ledger_rows_and_only_for_the_worker()
    {
        await OwnerAsync(
            $"""
            INSERT INTO nina.recovery_request (user_id, token_hash, expires_at) VALUES ('{W.Alice}', decode(repeat('01', 32), 'hex'), now() - interval '8 days'), ('{W.Alice}', decode(repeat('02', 32), 'hex'), now() + interval '1 day');
            INSERT INTO nina.email_verification_code (user_id, code_hash, created_at, expires_at) VALUES ('{W.Alice}', decode(repeat('03', 32), 'hex'), now() - interval '9 days', now() - interval '8 days');
            INSERT INTO nina.email_change_request (user_id, new_email, code_hash, created_at, expires_at) VALUES ('{W.Bob}', 'b2@example.org', decode(repeat('04', 32), 'hex'), now() - interval '9 days', now() - interval '8 days');
            INSERT INTO nina.reauth_jti (jti_hash, user_id, session_id, scopes, issued_at, consumed_at, consumed_scope, expires_at)
              VALUES (decode(repeat('05', 32), 'hex'), '{W.Alice}', gen_random_uuid(), ARRAY['ACCOUNT_DELETE'], now() - interval '3 days', now() - interval '3 days', 'ACCOUNT_DELETE', now() - interval '3 days' + interval '5 minutes');
            INSERT INTO nina.auth_session (user_id, device_id, platform, absolute_expires_at, revoked_at, revoked_reason) VALUES ('{W.Alice}', gen_random_uuid(), 'IOS', now() - interval '40 days', now() - interval '40 days', 'LOGOUT');
            INSERT INTO nina.data_export_request (user_id, status, requested_at, expires_at) VALUES ('{W.Alice}', 'EXPIRED', now() - interval '120 days', now() - interval '113 days');
            """);
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, "SELECT nina.purge_expired_operational_data(100)"));
        string result;
        await using (var worker = await OpenAsync(Role.Worker))
        {
            result = await worker.ScalarAsync<string>("SELECT nina.purge_expired_operational_data(100)::text") ?? string.Empty;
            await worker.CommitAsync();
        }

        Assert.Contains("\"recovery_request\": 1", result, StringComparison.Ordinal);
        Assert.Equal(1, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.recovery_request"));
        Assert.Equal(0, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.email_verification_code"));
        Assert.Equal(0, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.email_change_request"));
        Assert.Equal(0, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.reauth_jti"));
        Assert.Equal(0, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.auth_session"));
        Assert.Equal(0, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.data_export_request"));
    }

    // ------------------------------------------------------------------ versao / feed / purga com PK escopada

    [Fact]
    public async Task The_change_log_is_contiguous_and_equals_the_head_after_mixed_writes()
    {
        await using (var erin = await AsApp(W.Erin))
        {
            for (var i = 0; i < 12; i++)
            {
                await erin.ExecAsync(Sleep(W.BabyA));
            }

            await erin.CommitAsync();
        }

        await using var rollback = await AsApp(W.Erin);
        await rollback.ExecAsync(Sleep(W.BabyA));            // descartado (sem COMMIT): nao pode deixar lacuna
        await using var carol = await AsApp(W.Carol);
        var seqs = (await carol.RowsAsync($"SELECT sync_sequence FROM nina.change_log WHERE baby_id = '{W.BabyA}' ORDER BY 1")).Select(r => (long)r[0]!).ToArray();
        Assert.Equal(Enumerable.Range(1, seqs.Length).Select(i => (long)i), seqs);
        Assert.Equal(seqs.Length, await carol.ScalarAsync<long>($"SELECT last_sequence FROM nina.sync_head('{W.BabyA}')"));
    }

    [Fact]
    public async Task The_sync_purge_removes_only_the_entity_of_the_right_baby_when_ids_repeat_across_babies()
    {
        var shared = Guid.NewGuid();
        await OwnerAsync($"{Sleep(W.BabyA, id: shared)}; {Sleep(W.BabyB, id: shared)}");
        await OwnerAsync($"UPDATE nina.sleep_session SET deleted_at = now() - interval '100 days' WHERE baby_id = '{W.BabyA}' AND id = '{shared}'");
        await OwnerAsync(
            $"UPDATE nina.change_log SET changed_at = now() - interval '100 days' WHERE baby_id = '{W.BabyA}'; UPDATE nina.tombstone SET expires_at = now() - interval '1 day' WHERE baby_id = '{W.BabyA}'");

        await using (var worker = await OpenAsync(Role.Worker))
        {
            await worker.ExecAsync("SELECT * FROM nina.purge_expired_sync_data(1000)");
            await worker.CommitAsync();
        }

        Assert.Equal(0, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.sleep_session WHERE baby_id = '{W.BabyA}' AND id = '{shared}'"));
        Assert.Equal(1, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.sleep_session WHERE baby_id = '{W.BabyB}' AND id = '{shared}'"));    // a do outro tenant fica
        Assert.True(await OwnerScalarAsync<long>($"SELECT purged_through FROM nina.baby_sync_head WHERE baby_id = '{W.BabyA}'") >= 1);
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, "SELECT * FROM nina.purge_expired_sync_data(10)"));
    }
}
