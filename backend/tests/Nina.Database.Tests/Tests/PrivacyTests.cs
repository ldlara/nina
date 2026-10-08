using Nina.Database.Tests.Infrastructure;
using Npgsql;

namespace Nina.Database.Tests.Tests;

/// <summary>SR-007 (requisição de privacidade/anonimização com verificação e janela), SR-011 (exclusão sem resíduos), SR-016 e as funções de exclusão sob SECURITY DEFINER.</summary>
[Collection(PgClusterGroup.Name)]
public sealed class PrivacyTests(PgCluster cluster) : DbTestBase(cluster)
{
    private async Task<Guid> OpenRequestAsync(Guid user, string type, string verification = "REAUTHENTICATION", string scope = "PRIVACY_REQUEST")
    {
        var jti = await ReauthAsync(user, scope);
        await using var s = await AsApp(user);
        var id = await s.ScalarAsync<Guid>("SELECT nina.open_privacy_request(@t, @v, @h)", P("t", type), P("v", verification), Bytes("h", jti));
        await s.CommitAsync();
        return id;
    }

    private Task WorkerAsync(string sql) => RunAsync(Role.Worker, sql);

    private async Task RunAsync(Role role, string sql)
    {
        await using var s = await OpenAsync(role);
        await s.ExecAsync(sql);
        await s.CommitAsync();
    }

    // ------------------------------------------------------------------ SR-007

    [Fact]
    public async Task P1_a_privacy_request_cannot_be_opened_without_a_proven_verification()
    {
        Assert.Equal("42883", await FailsAsync(Role.App, W.Carol, "SELECT nina.open_privacy_request('ANONYMIZATION')"));          // o DEFAULT fail-open nao existe mais
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Carol, "SELECT nina.open_privacy_request('ANONYMIZATION', NULL, NULL)"));
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Carol, "SELECT nina.open_privacy_request('ANONYMIZATION', 'REAUTHENTICATION', NULL)"));
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Carol, "SELECT nina.open_privacy_request('ANONYMIZATION', 'REAUTHENTICATION', @h)", Bytes("h", Hash("inventado"))));
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Carol, "SELECT nina.open_privacy_request('ANONYMIZATION', 'EMAIL', @h)", Bytes("h", await ReauthAsync(W.Carol, "PRIVACY_REQUEST"))));
        var wrongScope = await ReauthAsync(W.Carol, "ACCOUNT_DELETE");
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Carol, "SELECT nina.open_privacy_request('ANONYMIZATION', 'REAUTHENTICATION', @h)", Bytes("h", wrongScope)));
        var someoneElses = await ReauthAsync(W.Dave, "PRIVACY_REQUEST");
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Carol, "SELECT nina.open_privacy_request('ANONYMIZATION', 'REAUTHENTICATION', @h)", Bytes("h", someoneElses)));
        Assert.Equal("NN013", await FailsAsync(Role.App, W.Alice, "SELECT nina.open_privacy_request('ANONYMIZATION', 'REAUTHENTICATION', @h)", Bytes("h", await ReauthAsync(W.Alice, "PRIVACY_REQUEST"))));     // Owner usa a exclusao de conta
        Assert.Equal(0, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.privacy_request"));
    }

    [Fact]
    public async Task P1_an_anonymization_request_is_scheduled_after_the_grace_window_and_the_proof_is_single_use()
    {
        var jti = await ReauthAsync(W.Carol, "PRIVACY_REQUEST");
        Guid request;
        await using (var carol = await AsApp(W.Carol))
        {
            request = await carol.ScalarAsync<Guid>("SELECT nina.open_privacy_request('ANONYMIZATION', 'REAUTHENTICATION', @h)", Bytes("h", jti));
            await carol.CommitAsync();
        }

        var row = (await (await OpenAsync(Role.Owner)).RowsAsync(
            $"SELECT status, scheduled_for >= now() + interval '6 days 23 hours', identity_verified_at IS NOT NULL, due_at >= now() + interval '14 days' FROM nina.privacy_request WHERE id = '{request}'")).Single();
        Assert.Equal("SCHEDULED", row[0]);
        Assert.True((bool)row[1]!);
        Assert.True((bool)row[2]!);
        Assert.True((bool)row[3]!);
        Assert.Equal(1, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.outbox_message WHERE event_type = 'PrivacyRequestOpened'"));      // o worker avisa o e-mail da conta
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Carol, "SELECT nina.open_privacy_request('ACCESS', 'REAUTHENTICATION', @h)", Bytes("h", jti)));   // o mesmo jti nao abre outro pedido
        Assert.Equal("23505", await FailsAsync(Role.App, W.Carol, "SELECT nina.open_privacy_request('ANONYMIZATION', 'REAUTHENTICATION', @h)", Bytes("h", await ReauthAsync(W.Carol, "PRIVACY_REQUEST"))));   // um pedido aberto por tipo
        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol, $"UPDATE nina.privacy_request SET scheduled_for = now() WHERE id = '{request}'"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol, "INSERT INTO nina.privacy_request (user_id, request_type, due_at, scheduled_for, identity_verified_at, verification_method, reauth_jti_hash) VALUES (gen_random_uuid(), 'ANONYMIZATION', now(), now(), now(), 'REAUTHENTICATION', decode(repeat('ab', 32), 'hex'))"));
    }

    [Fact]
    public async Task P2_the_erasure_cannot_run_before_the_scheduled_date_and_then_anonymizes_only_the_requester()
    {
        var sleep = Guid.NewGuid();
        await OwnerAsync($"INSERT INTO nina.sleep_session (id, baby_id, start_at, end_at, tz, sleep_type, source, created_by, last_modified_by) VALUES ('{sleep}', '{W.BabyA}', now() - interval '2 hours', now() - interval '1 hour', 'UTC', 'NAP', 'MANUAL', '{W.Carol}', '{W.Carol}')");
        await OwnerAsync($"INSERT INTO nina.user_credential (user_id, password_hash, hash_algorithm) VALUES ('{W.Carol}', 'x', 'ARGON2ID')");
        var request = await OpenRequestAsync(W.Carol, "ANONYMIZATION");
        var access = await OpenRequestAsync(W.Carol, "ACCESS");

        Assert.Equal("NN008", await FailsAsync(Role.Worker, null, $"SELECT nina.fulfill_privacy_erasure('{request}')"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol, $"SELECT nina.fulfill_privacy_erasure('{request}')"));
        await OwnerAsync($"UPDATE nina.privacy_request SET scheduled_for = now() - interval '1 minute' WHERE id = '{request}'");     // o tempo passa
        await WorkerAsync($"SELECT nina.fulfill_privacy_erasure('{request}')");
        await WorkerAsync($"SELECT nina.fulfill_privacy_erasure('{request}')");                  // idempotente

        Assert.Equal("DELETED", await OwnerScalarAsync<string>($"SELECT status FROM nina.app_user WHERE id = '{W.Carol}'"));
        Assert.Null(await OwnerScalarAsync<string>($"SELECT display_name FROM nina.app_user WHERE id = '{W.Carol}'"));
        Assert.Null(await OwnerScalarAsync<string>($"SELECT email FROM nina.app_user WHERE id = '{W.Carol}'"));
        Assert.Equal(0, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.user_credential WHERE user_id = '{W.Carol}'"));
        Assert.Equal(0, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.caregiver_membership WHERE user_id = '{W.Carol}'"));
        Assert.Null(await OwnerScalarAsync<Guid?>($"SELECT created_by FROM nina.sleep_session WHERE id = '{sleep}'"));      // autoria zerada (ficha de scrub emitida pela funcao)
        Assert.Equal(1, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.sleep_session WHERE id = '{sleep}'"));     // o dado do bebe fica
        Assert.Equal("CANCELLED", await OwnerScalarAsync<string>($"SELECT status FROM nina.privacy_request WHERE id = '{access}'"));
        Assert.Equal(1, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE action = 'privacy.erasure_completed' AND is_critical AND chain_seq IS NOT NULL"));
        Assert.Equal(1, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.erasure_ledger WHERE entity_type = 'USER' AND entity_id = '{W.Carol}'"));
        Assert.Equal("ACTIVE", await OwnerScalarAsync<string>($"SELECT status FROM nina.app_user WHERE id = '{W.Alice}'"));
        Assert.Equal(3, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.caregiver_membership WHERE baby_id = '{W.BabyA}' AND status = 'ACTIVE' OR baby_id = '{W.BabyB}' AND status = 'ACTIVE'"));
    }

    [Fact]
    public async Task A_scheduled_request_can_be_cancelled_by_its_owner_only_while_open()
    {
        var request = await OpenRequestAsync(W.Carol, "ANONYMIZATION");
        Assert.Equal("NN015", await FailsAsync(Role.App, W.Erin, $"SELECT nina.cancel_privacy_request('{request}')"));
        await using (var carol = await AsApp(W.Carol))
        {
            await carol.ExecAsync($"SELECT nina.cancel_privacy_request('{request}')");
            await carol.CommitAsync();
        }

        Assert.Equal("CANCELLED", await OwnerScalarAsync<string>($"SELECT status FROM nina.privacy_request WHERE id = '{request}'"));
        await OwnerAsync($"UPDATE nina.privacy_request SET scheduled_for = now() - interval '1 day' WHERE id = '{request}'");
        Assert.Equal("NN015", await FailsAsync(Role.Worker, null, $"SELECT nina.fulfill_privacy_erasure('{request}')"));
        Assert.Equal("NN015", await FailsAsync(Role.App, W.Carol, $"SELECT nina.cancel_privacy_request('{request}')"));
    }

    [Fact]
    public async Task The_worker_closes_access_requests_but_never_completes_an_anonymization_by_closing_it()
    {
        var access = await OpenRequestAsync(W.Carol, "ACCESS");
        var anon = await OpenRequestAsync(W.Erin, "ANONYMIZATION");
        Assert.Equal("NN015", await FailsAsync(Role.Worker, null, $"SELECT nina.close_privacy_request('{anon}', 'COMPLETED')"));
        await WorkerAsync($"SELECT nina.close_privacy_request('{access}', 'COMPLETED', 'DONE')");
        await WorkerAsync($"SELECT nina.close_privacy_request('{anon}', 'REJECTED', 'DUPLICATE')");
        Assert.Equal("COMPLETED", await OwnerScalarAsync<string>($"SELECT status FROM nina.privacy_request WHERE id = '{access}'"));
        Assert.Equal("REJECTED", await OwnerScalarAsync<string>($"SELECT status FROM nina.privacy_request WHERE id = '{anon}'"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol, $"SELECT nina.close_privacy_request('{access}', 'COMPLETED')"));
    }

    // ------------------------------------------------------------------ SR-011

    [Fact]
    public async Task R1_erasing_a_baby_invalidates_every_export_that_contains_it_and_leaves_no_residue()
    {
        await OwnerAsync(
            $"""
            INSERT INTO nina.data_export_request (id, user_id, status, file_ref, baby_ids, expires_at) VALUES
              ('{W.Bob}', '{W.Bob}', 'READY', 's3://exports/bob.zip', ARRAY['{W.BabyB}']::uuid[], now() + interval '5 days'),
              ('{W.Carol}', '{W.Carol}', 'READY', 's3://exports/carol.zip', ARRAY['{W.BabyA}']::uuid[], now() + interval '5 days'),
              ('{W.Erin}', '{W.Erin}', 'READY', 's3://exports/erin.zip', ARRAY['{W.BabyA}', '{W.BabyB}']::uuid[], now() + interval '5 days'),
              ('{W.Dave}', '{W.Dave}', 'PROCESSING', NULL, ARRAY['{W.BabyB}']::uuid[], NULL);
            """);
        await OwnerAsync($"UPDATE nina.baby SET photo_ref = 's3://fotos/b.jpg' WHERE id = '{W.BabyB}'");
        await WorkerAsync($"SELECT nina.erase_baby('{W.BabyB}')");

        Assert.Equal("EXPIRED", await OwnerScalarAsync<string>($"SELECT status FROM nina.data_export_request WHERE id = '{W.Bob}'"));
        Assert.Null(await OwnerScalarAsync<string>($"SELECT file_ref FROM nina.data_export_request WHERE id = '{W.Bob}'"));
        Assert.Null(await OwnerScalarAsync<string>($"SELECT file_ref FROM nina.data_export_request WHERE id = '{W.Erin}'"));       // contem o bebe apagado, mesmo com outro
        Assert.Equal("EXPIRED", await OwnerScalarAsync<string>($"SELECT status FROM nina.data_export_request WHERE id = '{W.Dave}'"));   // em geracao: cancelada
        Assert.Equal("s3://exports/carol.zip", await OwnerScalarAsync<string>($"SELECT file_ref FROM nina.data_export_request WHERE id = '{W.Carol}'"));      // so de A: intacta
        Assert.Equal(2, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.outbox_message WHERE event_type = 'ExportFileInvalidated'"));                  // storage apaga os arquivos
        Assert.Null(await OwnerScalarAsync<string>($"SELECT display_name FROM nina.baby WHERE id = '{W.BabyB}'"));
        Assert.Null(await OwnerScalarAsync<string>($"SELECT photo_ref FROM nina.baby WHERE id = '{W.BabyB}'"));
        Assert.Equal(0, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.sleep_session WHERE baby_id = '{W.BabyB}'"));
        Assert.Equal("{}", await OwnerScalarAsync<string>($"SELECT field_versions::text FROM nina.baby WHERE id = '{W.BabyB}'"));
        Assert.Equal(1, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.erasure_ledger WHERE entity_type = 'BABY' AND entity_id = '{W.BabyB}'"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Bob, $"SELECT nina.erase_baby('{W.BabyB}')"));
    }

    [Fact]
    public async Task Exports_can_only_be_requested_for_babies_the_user_can_read_and_start_in_the_requested_state()
    {
        Assert.Null(await FailsAsync(Role.App, W.Carol, $"INSERT INTO nina.data_export_request (user_id, baby_ids) VALUES ('{W.Carol}', ARRAY['{W.BabyA}']::uuid[])"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol, $"INSERT INTO nina.data_export_request (user_id, baby_ids) VALUES ('{W.Carol}', ARRAY['{W.BabyB}']::uuid[])"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol, $"INSERT INTO nina.data_export_request (user_id, status, file_ref) VALUES ('{W.Carol}', 'READY', 's3://x')"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol, $"INSERT INTO nina.data_export_request (user_id) VALUES ('{W.Dave}')"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol, "UPDATE nina.data_export_request SET status = 'READY'"));
    }

    [Fact]
    public async Task The_erasure_ledger_reapplies_deletions_after_a_backup_restore()
    {
        // simula o restore: o bebe do Bob e a conta do Dave voltam "vivos" mesmo constando no ledger
        await OwnerAsync($"INSERT INTO nina.erasure_ledger (entity_type, entity_id) VALUES ('BABY', '{W.BabyB}'), ('USER', '{W.Dave}')");
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, "SELECT nina.reapply_erasure_ledger()"));
        await WorkerAsync("SELECT nina.reapply_erasure_ledger()");

        Assert.Null(await OwnerScalarAsync<string>($"SELECT display_name FROM nina.baby WHERE id = '{W.BabyB}'"));
        Assert.NotNull(await OwnerScalarAsync<DateTime?>($"SELECT deleted_at FROM nina.baby WHERE id = '{W.BabyB}'"));
        Assert.Equal("DELETED", await OwnerScalarAsync<string>($"SELECT status FROM nina.app_user WHERE id = '{W.Dave}'"));
        Assert.Equal(1, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE action = 'backup.erasure_reapplied' AND is_critical"));
    }

    [Fact]
    public async Task Scrubbing_a_user_also_clears_the_actor_in_the_change_log()
    {
        await using (var erin = await AsApp(W.Erin))
        {
            await erin.ExecAsync($"INSERT INTO nina.sleep_session (id, baby_id, start_at, tz, sleep_type, source, created_by, last_modified_by) VALUES (gen_random_uuid(), '{W.BabyA}', now(), 'UTC', 'NAP', 'MANUAL', '{W.Erin}', '{W.Erin}')");
            await erin.CommitAsync();
        }

        Assert.True(await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.change_log WHERE actor_user_id = '{W.Erin}'") > 0);
        var request = await OpenRequestAsync(W.Erin, "ANONYMIZATION");
        await OwnerAsync($"UPDATE nina.privacy_request SET scheduled_for = now() - interval '1 minute' WHERE id = '{request}'");
        await WorkerAsync($"SELECT nina.fulfill_privacy_erasure('{request}')");
        Assert.Equal(0, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.change_log WHERE actor_user_id = '{W.Erin}'"));
    }

    // ------------------------------------------------------------------ exclusao de conta do Owner (funcoes definer + fichas)

    // NR-03/NR-14: pelo app o pedido SEMPRE nasce confirmado (reautenticacao ACCOUNT_DELETE emitida e comprovada). O pedido NAO confirmado so existe
    // se criado fora da funcao (dono/legado): serve para provar que erase_user continua exigindo a confirmacao para bebe compartilhado.
    private async Task<Guid> ScheduleDeletionAsync(Guid user, bool confirmed, bool elapsed = true)
    {
        Guid id;
        if (confirmed)
        {
            await using var s = await AsApp(user);
            id = await s.ScalarAsync<Guid>("SELECT nina.request_account_deletion(@h)", Bytes("h", await ReauthAsync(user, "ACCOUNT_DELETE")));
            await s.CommitAsync();
        }
        else
        {
            id = await OwnerScalarAsync<Guid>($"INSERT INTO nina.account_deletion_request (user_id, grace_days, scheduled_for) VALUES ('{user}', 1, now()) RETURNING id");
        }

        if (elapsed)
        {
            await OwnerAsync($"ALTER TABLE nina.account_deletion_request DISABLE TRIGGER account_deletion_request_guard; UPDATE nina.account_deletion_request SET requested_at = now() - interval '8 days', scheduled_for = now() - interval '1 minute' WHERE id = '{id}'; ALTER TABLE nina.account_deletion_request ENABLE TRIGGER account_deletion_request_guard");
        }

        return id;
    }

    private async Task SetPolicyAsync(string policy)
    {
        await using var admin = await OpenAsync(Role.Config, W.Alice);
        await admin.ExecAsync("UPDATE nina.app_parameter SET value = to_jsonb(@p::text) WHERE param_key = 'privacy.owner_deletion_policy'", P("p", policy));
        await admin.CommitAsync();
    }

    [Fact]
    public async Task Account_deletion_waits_for_the_window_needs_a_request_and_a_confirmation_for_shared_babies()
    {
        Assert.Equal("NN009", await FailsAsync(Role.Worker, null, $"SELECT nina.erase_user('{W.Alice}')"));
        var request = await ScheduleDeletionAsync(W.Alice, confirmed: false, elapsed: false);
        Assert.Equal("NN008", await FailsAsync(Role.Worker, null, $"SELECT nina.erase_user('{W.Alice}')"));
        await OwnerAsync($"ALTER TABLE nina.account_deletion_request DISABLE TRIGGER account_deletion_request_guard; UPDATE nina.account_deletion_request SET requested_at = now() - interval '8 days', scheduled_for = now() - interval '1 minute' WHERE id = '{request}'; ALTER TABLE nina.account_deletion_request ENABLE TRIGGER account_deletion_request_guard");
        Assert.Equal("NN007", await FailsAsync(Role.Worker, null, $"SELECT nina.erase_user('{W.Alice}')"));            // bebe compartilhado sem confirmacao comprovada
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, $"SELECT nina.erase_user('{W.Alice}')"));
        Assert.Equal("ACTIVE", await OwnerScalarAsync<string>($"SELECT status FROM nina.app_user WHERE id = '{W.Alice}'"));
    }

    [Fact]
    public async Task Account_deletion_with_cascade_erases_the_shared_baby_and_warns_every_active_caregiver()
    {
        await ScheduleDeletionAsync(W.Alice, confirmed: true);
        await WorkerAsync($"SELECT nina.erase_user('{W.Alice}')");

        Assert.Equal("DELETED", await OwnerScalarAsync<string>($"SELECT status FROM nina.app_user WHERE id = '{W.Alice}'"));
        Assert.NotNull(await OwnerScalarAsync<DateTime?>($"SELECT deleted_at FROM nina.baby WHERE id = '{W.BabyA}'"));
        Assert.Equal(2, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.outbox_message WHERE event_type = 'SharedBabyDeletedNotice'"));      // Carol e Erin (SR-016)
        Assert.Equal(1, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE action = 'account.cascade_shared_baby_erased' AND is_critical"));
        Assert.Equal("ACTIVE", await OwnerScalarAsync<string>($"SELECT status FROM nina.app_user WHERE id = '{W.Erin}'"));
        Assert.NotNull(await OwnerScalarAsync<string>($"SELECT display_name FROM nina.baby WHERE id = '{W.BabyB}'"));
    }

    [Fact]
    public async Task Account_deletion_with_transfer_policy_promotes_a_caregiver_and_moves_the_baby_to_their_family()
    {
        await SetPolicyAsync("TRANSFER_OWNERSHIP");
        await ScheduleDeletionAsync(W.Alice, confirmed: false);
        await WorkerAsync($"SELECT nina.erase_user('{W.Alice}')");

        Assert.Equal("OWNER", await OwnerScalarAsync<string>($"SELECT role FROM nina.caregiver_membership WHERE id = '{W.MembershipErin}'"));      // CAREGIVER antes de READ_ONLY
        Assert.Equal("BebeAlice", await OwnerScalarAsync<string>($"SELECT display_name FROM nina.baby WHERE id = '{W.BabyA}'"));                // dados preservados
        Assert.Equal(W.Erin, await OwnerScalarAsync<Guid>($"SELECT f.owner_user_id FROM nina.baby b JOIN nina.family f ON f.id = b.family_id WHERE b.id = '{W.BabyA}'"));
        Assert.Equal("DELETED", await OwnerScalarAsync<string>($"SELECT status FROM nina.app_user WHERE id = '{W.Alice}'"));
        await using var erin = await AsApp(W.Erin);
        Assert.Equal(1, await erin.ExecAsync($"UPDATE nina.baby SET display_name = 'Novo' WHERE id = '{W.BabyA}'"));
    }

    [Fact]
    public async Task Account_deletion_with_block_policy_refuses_and_changes_nothing()
    {
        await SetPolicyAsync("BLOCK");
        await ScheduleDeletionAsync(W.Alice, confirmed: true);
        Assert.Equal("NN004", await FailsAsync(Role.Worker, null, $"SELECT nina.erase_user('{W.Alice}')"));
        Assert.Equal("ACTIVE", await OwnerScalarAsync<string>($"SELECT status FROM nina.app_user WHERE id = '{W.Alice}'"));
        Assert.Equal(3, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.caregiver_membership WHERE baby_id = '{W.BabyA}' AND status = 'ACTIVE'"));
    }

    [Fact]
    public async Task The_deletion_window_still_cannot_be_bypassed_even_by_the_schema_owner()
    {
        var request = await ScheduleDeletionAsync(W.Bob, confirmed: true, elapsed: false);
        Assert.Equal("NN008", await FailsAsync(Role.Owner, null, $"SELECT nina.erase_user('{W.Bob}')"));
        Assert.Equal("NN008", await FailsAsync(Role.Owner, null, $"UPDATE nina.account_deletion_request SET status = 'COMPLETED' WHERE id = '{request}'"));
        await using var s = await OpenAsync(Role.Owner);
        Assert.Equal("ACTIVE", await s.ScalarAsync<string>($"SELECT status FROM nina.app_user WHERE id = '{W.Bob}'"));
    }
}
