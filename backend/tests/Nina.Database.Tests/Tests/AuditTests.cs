using Nina.Database.Tests.Infrastructure;
using Npgsql;

namespace Nina.Database.Tests.Tests;

/// <summary>
/// SR-005 (ALTO: auditoria apagável), SR-006 (ALTO: auditoria forjável, PII aninhada) e SR-008 (GUCs definíveis pelo chamador).
/// Anexo A: AU1..AU4, G2, G3, G4.
/// </summary>
[Collection(PgClusterGroup.Name)]
public sealed class AuditTests(PgCluster cluster) : DbTestBase(cluster)
{
    private const string RawInsert =
        "INSERT INTO nina.audit_event (actor_user_id, actor_type, action, entity_type, entity_id, is_critical) VALUES (gen_random_uuid(), 'ADMIN', 'privacy.erasure_completed', 'USER', gen_random_uuid(), true)";

    /// <summary>Zera a trilha (a migracao ja grava os eventos dos seeds) para contagens exatas; só o superusuário de teste consegue, desligando os gatilhos.</summary>
    private Task ClearAuditAsync() => OwnerAsync(
        "ALTER TABLE nina.audit_event DISABLE TRIGGER audit_event_no_truncate; TRUNCATE nina.audit_event; ALTER TABLE nina.audit_event ENABLE TRIGGER audit_event_no_truncate");

    private async Task SeedOldEventsAsync()
    {
        await ClearAuditAsync();
        // 14 meses (nao critico: elegivel a partir de 12), 14 meses critico (so apos 60), 70 meses critico (elegivel), recente
        await OwnerAsync(
            """
            INSERT INTO nina.audit_event (occurred_at, actor_type, action, is_critical) VALUES (now() - interval '70 months', 'SYSTEM', 'config.changed', true);
            INSERT INTO nina.audit_event (occurred_at, actor_type, action, is_critical) VALUES (now() - interval '14 months', 'SYSTEM', 'config.changed', true);
            INSERT INTO nina.audit_event (occurred_at, action) VALUES (now() - interval '14 months', 'auth.login');
            INSERT INTO nina.audit_event (occurred_at, action) VALUES (now() - interval '13 months', 'auth.logout');
            INSERT INTO nina.audit_event (occurred_at, action) VALUES (now() - interval '1 month', 'auth.login');
            INSERT INTO nina.audit_event (action, actor_type, is_critical) VALUES ('baby.erased', 'SYSTEM', true);
            """);
    }

    // ------------------------------------------------------------------ SR-006: forja

    [Fact]
    public async Task AU1_G4_no_application_role_can_insert_audit_events_directly()
    {
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, RawInsert));
        Assert.Equal("42501", await FailsAsync(Role.Config, W.Dave, RawInsert));
        Assert.Equal("42501", await FailsAsync(Role.Worker, null, RawInsert));
        Assert.Equal(0, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE action = 'privacy.erasure_completed'"));
    }

    [Fact]
    public async Task The_audit_function_forces_the_actor_and_takes_severity_from_the_catalog()
    {
        await using (var dave = await AsApp(W.Dave))
        {
            await dave.ExecAsync(
                "SELECT nina.audit('auth.password_changed', 'user', @u, NULL, NULL, 'req-1', 'SUCCESS', decode(repeat('ab', 32), 'hex'), '{}')", P("u", W.Alice));
            await dave.CommitAsync();
        }

        var row = (await (await OpenAsync(Role.Owner)).RowsAsync(
            "SELECT actor_user_id, actor_type, is_critical, chain_seq IS NOT NULL FROM nina.audit_event WHERE action = 'auth.password_changed'")).Single();
        Assert.Equal(W.Dave, (Guid)row[0]!);                // nao ha como atribuir o evento a outra pessoa
        Assert.Equal("USER", row[1]);
        Assert.True((bool)row[2]!);                         // critica por catalogo, nao pela chamada
        Assert.True((bool)row[3]!);                         // e entra na cadeia
    }

    [Fact]
    public async Task The_audit_function_refuses_unknown_privileged_or_mismatching_events()
    {
        foreach (var action in new[] { "privacy.erasure_completed", "config.changed", "account.erased", "baby.erased", "audit.purged", "inventada.acao", "support.access_granted", "auth.login_failed" })
        {
            Assert.Equal("NN061", await FailsAsync(Role.App, W.Dave, "SELECT nina.audit(@a)", P("a", action)));
        }

        Assert.Equal("NN061", await FailsAsync(Role.App, W.Dave, "SELECT nina.audit('auth.login', NULL, NULL, NULL, NULL, NULL, 'DENIED')"));
        Assert.Equal("NN061", await FailsAsync(Role.App, W.Dave, "SELECT nina.audit('auth.refresh_reuse_detected', NULL, NULL, NULL, NULL, NULL, 'SUCCESS')"));
        Assert.Equal("NN060", await FailsAsync(Role.App, null, "SELECT nina.audit('auth.login')"));
        Assert.Equal("NN060", await FailsAsync(Role.App, Guid.NewGuid(), "SELECT nina.audit('auth.login')"));                 // contexto de usuario inexistente
        Assert.Equal("NN060", await FailsAsync(Role.App, W.Dave, $"SELECT nina.audit('auth.login', NULL, NULL, '{W.BabyA}')"));   // bebe sem vinculo
        Assert.Null(await FailsAsync(Role.App, W.Carol, $"SELECT nina.audit('auth.login', NULL, NULL, '{W.BabyA}')"));
    }

    [Fact]
    public async Task AU2_metadata_is_checked_against_an_allowlist_and_a_deep_pii_denylist()
    {
        // pela funcao: chaves fora do catalogo ou com PII
        Assert.Equal("NN062", await FailsAsync(Role.App, W.Dave, "SELECT nina.audit('auth.login', NULL, NULL, NULL, NULL, NULL, 'SUCCESS', NULL, @m::jsonb)", P("m", "{\"detalhe\": {\"email\": \"alice@ex.org\", \"nome\": \"Alice Silva\"}}")));
        Assert.Equal("NN062", await FailsAsync(Role.App, W.Dave, "SELECT nina.audit('auth.login', NULL, NULL, NULL, NULL, NULL, 'SUCCESS', NULL, @m::jsonb)", P("m", "{\"e_mail\": \"bob@ex.org\"}")));
        Assert.Equal("NN062", await FailsAsync(Role.App, W.Dave, "SELECT nina.audit('auth.login', NULL, NULL, NULL, NULL, NULL, 'SUCCESS', NULL, @m::jsonb)", P("m", "{\"method\": \"alice@ex.org\"}")));     // chave ok, valor e e-mail
        Assert.Equal("NN062", await FailsAsync(Role.App, W.Dave, "SELECT nina.audit('auth.login', NULL, NULL, NULL, NULL, NULL, 'SUCCESS', NULL, @m::jsonb)", P("m", "[1]")));
        Assert.Null(await FailsAsync(Role.App, W.Dave, "SELECT nina.audit('auth.login', NULL, NULL, NULL, NULL, NULL, 'SUCCESS', NULL, @m::jsonb)", P("m", "{\"method\": \"PASSWORD\", \"platform\": \"IOS\"}")));

        // pela tabela (defesa em profundidade, inclusive para o dono): PII aninhada ou com outro nome nao entra
        foreach (var meta in new[] { "{\"detalhe\":{\"email\":\"alice@ex.org\",\"nome\":\"Alice Silva\"},\"e_mail\":\"bob@ex.org\"}", "{\"a\":{\"b\":{\"c\":[{\"Display-Name\":\"x\"}]}}}", "{\"obs\":\"fale com alice@example.org\"}", "{\"Birth_Date\":\"2026-01-01\"}", "{\"phone\":\"1\"}", "{\"idToken\":\"abc\"}" })
        {
            Assert.Equal("23514", await FailsAsync(Role.Owner, null, "INSERT INTO nina.audit_event (action, actor_type, metadata_safe) VALUES ('x.y', 'SYSTEM', @m::jsonb)", P("m", meta)));
        }

        Assert.Null(await FailsAsync(Role.Owner, null, "INSERT INTO nina.audit_event (action, actor_type, metadata_safe) VALUES ('x.y', 'SYSTEM', @m::jsonb)", P("m", "{\"count\": 3, \"fields\": [\"notes_len\"], \"purpose_key\": \"terms_of_use\"}")));
    }

    [Fact]
    public async Task Pre_auth_attempts_record_only_failures_of_the_catalog_never_success_or_critical_events()
    {
        await using (var anon = await AsApp(null))
        {
            await anon.ExecAsync("SELECT nina.audit_auth_attempt('auth.login_failed', @u, NULL, 'r1', decode(repeat('cd', 32), 'hex'))", P("u", W.Alice));
            await anon.ExecAsync("SELECT nina.audit_auth_attempt('auth.email_verify_failed', @u)", P("u", Guid.NewGuid()));       // conta inexistente
            await anon.CommitAsync();
        }

        Assert.Equal(1, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.audit_event WHERE action = 'auth.login_failed' AND actor_user_id = '{W.Alice}' AND result = 'FAILURE' AND NOT is_critical"));
        Assert.Equal("SYSTEM", await OwnerScalarAsync<string>("SELECT actor_type FROM nina.audit_event WHERE action = 'auth.email_verify_failed'"));
        foreach (var action in new[] { "auth.login", "auth.password_changed", "privacy.erasure_completed", "consent.granted" })
        {
            Assert.Equal("NN061", await FailsAsync(Role.App, null, "SELECT nina.audit_auth_attempt(@a, @u)", P("a", action), P("u", W.Alice)));
        }

        Assert.Equal("NN062", await FailsAsync(Role.App, null, "SELECT nina.audit_auth_attempt('auth.login_failed', NULL, NULL, NULL, NULL, '{\"email\": \"a@b.org\"}')"));
    }

    [Fact]
    public async Task Privileged_events_come_only_from_the_worker_through_the_catalog()
    {
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, "SELECT nina.audit_privileged('ADMIN', 'support.access_granted')"));
        Assert.Equal("42501", await FailsAsync(Role.Config, W.Dave, "SELECT nina.audit_privileged('ADMIN', 'support.access_granted')"));
        Assert.Equal("NN061", await FailsAsync(Role.Worker, null, "SELECT nina.audit_privileged('USER', 'support.access_granted')"));
        Assert.Equal("NN061", await FailsAsync(Role.Worker, null, "SELECT nina.audit_privileged('ADMIN', 'privacy.erasure_completed')"));
        Assert.Equal("NN062", await FailsAsync(Role.Worker, null, "SELECT nina.audit_privileged('SUPPORT', 'support.access_granted', NULL, NULL, NULL, NULL, 'SUCCESS', '{\"nome\": \"x\"}')"));
        await using (var worker = await OpenAsync(Role.Worker))
        {
            await worker.ExecAsync("SELECT nina.audit_privileged('SUPPORT', 'support.access_granted', gen_random_uuid(), NULL, NULL, NULL, 'DENIED', '{\"reason_code\": \"NO_TICKET\"}')");
            await worker.CommitAsync();
        }

        // ator nao-USER e resultado DENIED entram na cadeia, mesmo sem is_critical (SR-005.3)
        Assert.Equal(1, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE action = 'support.access_granted' AND chain_seq IS NOT NULL AND is_critical"));
    }

    // ------------------------------------------------------------------ SR-005: apagar

    [Fact]
    public async Task AU3_update_delete_and_truncate_of_audit_consent_and_config_history_fail_for_every_role()
    {
        await OwnerAsync(
            $"INSERT INTO nina.audit_event (action, actor_type) VALUES ('auth.login', 'SYSTEM'); INSERT INTO nina.consent_record (user_id, purpose_key, policy_version, text_hash, locale, status, source) VALUES ('{W.Dave}', 'terms_of_use', '1.0.0', '{new string('a', 64)}', 'pt-BR', 'GRANTED', 'ONBOARDING')");
        foreach (var role in new[] { Role.App, Role.Config, Role.Worker })
        {
            foreach (var sql in new[] { "UPDATE nina.audit_event SET result = 'DENIED'", "DELETE FROM nina.audit_event", "TRUNCATE nina.audit_event", "DELETE FROM nina.consent_record", "UPDATE nina.consent_record SET status = 'REVOKED'", "DELETE FROM nina.config_change", "TRUNCATE nina.consent_record" })
            {
                Assert.Equal("42501", await FailsAsync(role, W.Alice, sql));
            }
        }

        // defesa em profundidade: o dono tambem e barrado pelos gatilhos de imutabilidade (NN030)
        foreach (var sql in new[] { "UPDATE nina.audit_event SET result = 'DENIED'", "DELETE FROM nina.audit_event", "TRUNCATE nina.audit_event", "DELETE FROM nina.consent_record", "UPDATE nina.config_change SET reason = 'x'", "DELETE FROM nina.config_change", "TRUNCATE nina.config_change" })
        {
            Assert.Equal("NN030", await FailsAsync(Role.Owner, null, sql));
        }
    }

    [Fact]
    public async Task AU4_the_retention_purge_cannot_be_unlocked_by_a_guc_by_the_worker_role_or_by_the_owner()
    {
        await SeedOldEventsAsync();
        const string Forge = "SELECT set_config('nina.retention_purge', 'on', true); SELECT set_config('nina.guard.purge_audit', 'on', true); ";
        Assert.Equal("42501", await FailsAsync(Role.Worker, null, Forge + "DELETE FROM nina.audit_event WHERE is_critical"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, Forge + "DELETE FROM nina.audit_event WHERE is_critical"));
        Assert.Equal("NN030", await FailsAsync(Role.Owner, null, Forge + "DELETE FROM nina.audit_event WHERE is_critical"));
        Assert.Equal(6, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.audit_event"));
    }

    [Fact]
    public async Task The_audit_purge_enforces_an_age_floor_a_longer_floor_for_chained_events_and_audits_itself()
    {
        await SeedOldEventsAsync();
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, "SELECT nina.purge_audit(interval '100 years')"));
        Assert.Equal("NN031", await FailsAsync(Role.Worker, null, "SELECT nina.purge_audit(interval '1 day')"));
        Assert.Equal("NN031", await FailsAsync(Role.Worker, null, "SELECT nina.purge_audit(interval '11 months')"));
        Assert.Equal("NN031", await FailsAsync(Role.Worker, null, "SELECT nina.purge_audit(interval '100 years', 0)"));
        Assert.Equal(6, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.audit_event"));

        long deleted;
        await using (var worker = await OpenAsync(Role.Worker))
        {
            deleted = await worker.ScalarAsync<long>("SELECT nina.purge_audit(interval '12 months')");
            await worker.CommitAsync();
        }

        // sai: os 2 comuns com > 12 meses e o encadeado de 70 meses; fica o critico de 14 meses (piso de 60), o recente e o recem-criado
        Assert.Equal(3, deleted);
        Assert.Equal(1, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE occurred_at < now() - interval '13 months' AND is_critical"));
        Assert.Equal(0, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE occurred_at < now() - interval '60 months'"));
        Assert.Equal(1, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE action = 'baby.erased'"));
        // o proprio ato fica registrado (contagem e faixa de id), critico e encadeado
        Assert.Equal(3, await OwnerScalarAsync<int>("SELECT (metadata_safe->>'deleted')::int FROM nina.audit_event WHERE action = 'audit.purged'"));
        Assert.True(await OwnerScalarAsync<bool>("SELECT is_critical AND chain_seq IS NOT NULL FROM nina.audit_event WHERE action = 'audit.purged'"));
        Assert.True(await OwnerScalarAsync<bool>("SELECT ok FROM nina.verify_audit_chain()"));          // a cadeia segue verificavel (checkpoint)
        Assert.Equal(1, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.audit_chain_checkpoint"));
    }

    [Fact]
    public async Task The_chain_detects_tampering_and_does_not_depend_on_the_session_time_zone()
    {
        await ClearAuditAsync();
        await OwnerAsync(
            """
            SET LOCAL TIME ZONE 'Asia/Tokyo';
            INSERT INTO nina.audit_event (actor_type, action, is_critical) VALUES ('SYSTEM', 'config.changed', true);
            INSERT INTO nina.audit_event (actor_type, action, is_critical) VALUES ('SYSTEM', 'config.changed', true);
            """);
        await OwnerAsync("SET LOCAL TIME ZONE 'America/Sao_Paulo'; INSERT INTO nina.audit_event (actor_type, action, is_critical) VALUES ('SYSTEM', 'config.changed', true)");

        await using (var worker = await OpenAsync(Role.Worker))
        {
            await worker.ExecAsync("SET LOCAL TIME ZONE 'Pacific/Auckland'");
            Assert.True(await worker.ScalarAsync<bool>("SELECT ok FROM nina.verify_audit_chain()"));
            Assert.Equal(3, await worker.ScalarAsync<long>("SELECT chain_seq FROM nina.audit_chain_head()"));
        }

        // adulteracao (so possivel desligando o gatilho como superusuario): a verificacao aponta a linha
        await OwnerAsync(
            "ALTER TABLE nina.audit_event DISABLE TRIGGER audit_event_immutable; UPDATE nina.audit_event SET metadata_safe = '{\"x\": 1}' WHERE chain_seq = 2; ALTER TABLE nina.audit_event ENABLE TRIGGER audit_event_immutable");
        await using var check = await OpenAsync(Role.Worker);
        var result = (await check.RowsAsync("SELECT ok, broken_chain_seq FROM nina.verify_audit_chain()")).Single();
        Assert.False((bool)result[0]!);
        Assert.Equal(2L, result[1]);
    }

    [Fact]
    public async Task The_consent_purge_has_a_floor_and_only_removes_superseded_records()
    {
        await OwnerAsync(
            $"""
            INSERT INTO nina.consent_record (user_id, purpose_key, policy_version, text_hash, locale, status, recorded_at, source)
              VALUES ('{W.Dave}', 'analytics_product', '1.0.0', '{new string('a', 64)}', 'pt-BR', 'GRANTED', now() - interval '80 months', 'SETTINGS');
            INSERT INTO nina.consent_record (user_id, purpose_key, policy_version, text_hash, locale, status, recorded_at, source)
              VALUES ('{W.Dave}', 'analytics_product', '1.0.0', '{new string('a', 64)}', 'pt-BR', 'REVOKED', now() - interval '70 months', 'SETTINGS');
            INSERT INTO nina.consent_record (user_id, purpose_key, policy_version, text_hash, locale, status, recorded_at, source)
              VALUES ('{W.Dave}', 'analytics_product', '1.0.0', '{new string('a', 64)}', 'pt-BR', 'GRANTED', now() - interval '65 months', 'SETTINGS');
            INSERT INTO nina.consent_record (user_id, purpose_key, policy_version, text_hash, locale, status, recorded_at, source)
              VALUES ('{W.Dave}', 'privacy_policy', '1.0.0', '{new string('a', 64)}', 'pt-BR', 'GRANTED', now() - interval '90 months', 'ONBOARDING');
            """);
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, "SELECT nina.purge_consent(interval '100 years')"));
        Assert.Equal("NN031", await FailsAsync(Role.Worker, null, "SELECT nina.purge_consent(interval '1 year')"));

        await using (var worker = await OpenAsync(Role.Worker))
        {
            Assert.Equal(2, await worker.ScalarAsync<long>("SELECT nina.purge_consent(interval '60 months')"));
            await worker.CommitAsync();
        }

        // o estado vigente de cada finalidade permanece; so os substituidos saem
        Assert.Equal(2, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.consent_record WHERE user_id = '{W.Dave}'"));
        Assert.Equal(1, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE action = 'consent.purged' AND is_critical"));
    }

    // ------------------------------------------------------------------ SR-008: GUCs

    [Fact]
    public async Task G2_the_authorship_scrub_cannot_be_triggered_by_setting_gucs_from_the_app()
    {
        var sleep = Guid.NewGuid();
        await OwnerAsync($"INSERT INTO nina.sleep_session (id, baby_id, start_at, tz, sleep_type, source, notes, created_by, last_modified_by) VALUES ('{sleep}', '{W.BabyA}', now(), 'UTC', 'NAP', 'MANUAL', 'n', '{W.Alice}', '{W.Alice}')");
        var version = await OwnerScalarAsync<long>($"SELECT version FROM nina.sleep_session WHERE id = '{sleep}'");
        await using (var erin = await AsApp(W.Erin))
        {
            await erin.ExecAsync($"SELECT set_config('nina.authorship_scrub', 'on', true), set_config('nina.scrub_user', '{W.Alice}', true), set_config('nina.guard.authorship_scrub', 'on', true)");
            await erin.ExecAsync($"UPDATE nina.sleep_session SET notes = notes WHERE baby_id = '{W.BabyA}' AND id = '{sleep}'");
            await erin.CommitAsync();
        }

        // foi um UPDATE comum: autoria intacta, nova versao e rastro no change_log (nada de apagar evidencia em silencio)
        Assert.Equal(W.Alice, await OwnerScalarAsync<Guid>($"SELECT created_by FROM nina.sleep_session WHERE id = '{sleep}'"));
        Assert.True(await OwnerScalarAsync<long>($"SELECT version FROM nina.sleep_session WHERE id = '{sleep}'") > version);
        Assert.Equal("42501", await FailsAsync(Role.App, W.Erin, $"UPDATE nina.sleep_session SET created_by = NULL WHERE baby_id = '{W.BabyA}' AND id = '{sleep}'"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Erin, "SELECT nina.scrub_user_personal_data(gen_random_uuid())"));
    }

    [Fact]
    public async Task G3_a_config_admin_cannot_skip_attribution_with_a_migration_guc()
    {
        const string Change = "UPDATE nina.app_parameter SET value = '30'::jsonb WHERE param_key = 'sync.changelog_retention_days'";
        Assert.Equal("NN040", await FailsAsync(Role.Config, null, Change));
        Assert.Equal("NN040", await FailsAsync(Role.Config, null, "SELECT set_config('nina.migration', 'on', true); SELECT set_config('nina.guard.migration', 'on', true); " + Change));
        await using var admin = await OpenAsync(Role.Config, W.Alice);
        Assert.Equal(1, await admin.ExecAsync(Change));
        await admin.CommitAsync();
        Assert.Equal(W.Alice, await OwnerScalarAsync<Guid>("SELECT actor_user_id FROM nina.config_change WHERE record_key = 'sync.changelog_retention_days' ORDER BY id DESC LIMIT 1"));
        Assert.Equal("ADMIN", await OwnerScalarAsync<string>("SELECT actor_type FROM nina.audit_event WHERE action = 'config.changed' ORDER BY id DESC LIMIT 1"));
    }

    [Fact]
    public async Task The_audit_floors_cannot_be_lowered_by_configuration()
    {
        foreach (var (key, value) in new[] { ("audit.retention_months", "1"), ("audit.critical_retention_months", "12"), ("consent.retention_months", "6"), ("erasure_ledger.retention_days", "7") })
        {
            Assert.Equal("23514", await FailsAsync(Role.Config, W.Alice, "UPDATE nina.app_parameter SET value = @v::jsonb WHERE param_key = @k", P("v", value), P("k", key)));
        }

        Assert.Null(await FailsAsync(Role.Config, W.Alice, "UPDATE nina.app_parameter SET value = '24'::jsonb WHERE param_key = 'audit.retention_months'"));
    }

    [Fact]
    public async Task Users_read_only_their_own_audit_trail_through_a_function_without_ip_or_request_id()
    {
        await using (var alice = await AsApp(W.Alice))
        {
            await alice.ExecAsync("SELECT nina.audit('auth.login', 'session', NULL, NULL, NULL, 'rq', 'SUCCESS', decode(repeat('ab', 32), 'hex'), '{\"method\": \"PASSWORD\"}')");
            await alice.CommitAsync();
        }

        await using (var bob = await AsApp(W.Bob))
        {
            await bob.ExecAsync("SELECT nina.audit('auth.login')");
            await bob.CommitAsync();
        }

        await using var again = await AsApp(W.Alice);
        var rows = await again.RowsAsync("SELECT action, metadata_safe::text FROM nina.my_audit_events(10, NULL)");
        var row = Assert.Single(rows);
        Assert.Equal("auth.login", row[0]);
        Assert.Contains("PASSWORD", (string)row[1]!, StringComparison.Ordinal);
        var shape = await OwnerScalarAsync<string>("SELECT pg_get_function_result('nina.my_audit_events(integer,bigint)'::regprocedure)");
        Assert.DoesNotContain("ip_hash", shape, StringComparison.Ordinal);
        Assert.DoesNotContain("request_id", shape, StringComparison.Ordinal);
        Assert.DoesNotContain("actor_user_id", shape, StringComparison.Ordinal);
        await using var none = await AsApp(null);
        Assert.Empty(await none.RowsAsync("SELECT * FROM nina.my_audit_events(10, NULL)"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, "SELECT count(*) FROM nina.audit_event"));
    }
}
