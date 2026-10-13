using Nina.Database.Tests.Infrastructure;
using Npgsql;

namespace Nina.Database.Tests.Tests;

/// <summary>
/// SR-003 (ALTO): as 10 tabelas de identidade/credencial/token/assinatura/outbox têm RLS e privilégios mínimos; os fluxos
/// pré-autenticação do módulo Identity funcionam por funções estreitas, sem expor linhas alheias. Anexo A: N1, N4, N5, N6, O3, O4.
/// </summary>
[Collection(PgClusterGroup.Name)]
public sealed class IdentityTablesTests(PgCluster cluster) : DbTestBase(cluster)
{
    private async Task SeedCredentialsAsync()
    {
        await OwnerAsync(
            $"""
            INSERT INTO nina.user_credential (user_id, password_hash, hash_algorithm) VALUES
              ('{W.Alice}', '$argon2id$alice', 'ARGON2ID'), ('{W.Bob}', '$argon2id$bob', 'ARGON2ID');
            INSERT INTO nina.user_identity (user_id, provider, provider_subject) VALUES ('{W.Alice}', 'GOOGLE', 'g-alice'), ('{W.Bob}', 'APPLE', 'a-bob');
            INSERT INTO nina.auth_session (id, user_id, device_id, platform, absolute_expires_at)
              VALUES ('{W.Alice}', '{W.Alice}', '{W.Alice}', 'IOS', now() + interval '30 days'), ('{W.Bob}', '{W.Bob}', '{W.Bob}', 'ANDROID', now() + interval '30 days');
            INSERT INTO nina.refresh_token (session_id, token_hash, expires_at) VALUES
              ('{W.Alice}', decode(repeat('a1', 32), 'hex'), now() + interval '30 days'), ('{W.Bob}', decode(repeat('b1', 32), 'hex'), now() + interval '30 days');
            INSERT INTO nina.recovery_request (user_id, token_hash, expires_at) VALUES
              ('{W.Alice}', decode(repeat('a2', 32), 'hex'), now() + interval '1 hour'), ('{W.Bob}', decode(repeat('b2', 32), 'hex'), now() + interval '1 hour');
            INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type) VALUES ('BABY', '{W.BabyB}', 'BabyDeleted');
            INSERT INTO nina.subscription (family_id, plan_id, store, product_ref, original_transaction_ref, store_transaction_ref, status)
              VALUES ('{W.FamilyB}', 2, 'APPLE', 'p', 'orig-b', 'tx-b', 'ACTIVE');
            """);
    }

    [Fact]
    public async Task N1_a_user_with_no_baby_sees_no_one_elses_identity_credentials_tokens_or_billing_rows()
    {
        await SeedCredentialsAsync();
        await using var dave = await AsApp(W.Dave);
        foreach (var table in new[] { "user_credential", "user_identity", "auth_session", "refresh_token", "recovery_request", "family", "subscription", "family_entitlement", "family_entitlement_member", "device_push_token", "notification_job", "privacy_request", "data_export_request", "account_deletion_request", "consent_record" })
        {
            Assert.Equal(0, await dave.ScalarAsync<long>($"SELECT count(*) FROM nina.{table}"));
        }

        Assert.Equal(1, await dave.ScalarAsync<long>("SELECT count(*) FROM nina.app_user"));
        Assert.Equal(W.Dave, await dave.ScalarAsync<Guid>("SELECT id FROM nina.app_user"));
        // sem contexto, nada (nem a propria linha)
        await using var none = await AsApp(null);
        Assert.Equal(0, await none.ScalarAsync<long>("SELECT count(*) FROM nina.app_user"));
        Assert.Equal(0, await none.ScalarAsync<long>("SELECT count(*) FROM nina.user_credential"));
    }

    [Fact]
    public async Task N4_a_user_cannot_overwrite_another_users_credential_nor_change_email_or_status()
    {
        await SeedCredentialsAsync();
        await using (var dave = await AsApp(W.Dave))
        {
            Assert.Equal(0, await dave.ExecAsync($"UPDATE nina.user_credential SET password_hash = '$argon2id$ATACANTE' WHERE user_id = '{W.Bob}'"));
            Assert.Equal(0, await dave.ExecAsync($"UPDATE nina.app_user SET display_name = 'x' WHERE id = '{W.Bob}'"));
            Assert.Equal(0, await dave.ExecAsync($"UPDATE nina.refresh_token SET used_at = now() WHERE session_id = '{W.Bob}'"));
            Assert.Equal(0, await dave.ExecAsync($"UPDATE nina.recovery_request SET used_at = now() WHERE user_id = '{W.Bob}'"));
            Assert.Equal(0, await dave.ExecAsync($"DELETE FROM nina.user_identity WHERE user_id = '{W.Bob}'"));
        }

        Assert.Equal("$argon2id$bob", await OwnerScalarAsync<string>($"SELECT password_hash FROM nina.user_credential WHERE user_id = '{W.Bob}'"));
        // colunas fora do GRANT
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"UPDATE nina.app_user SET email = 'novo@example.org' WHERE id = '{W.Dave}'"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"UPDATE nina.app_user SET status = 'DELETED' WHERE id = '{W.Dave}'"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"UPDATE nina.user_credential SET user_id = '{W.Dave}' WHERE user_id = '{W.Bob}'"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"DELETE FROM nina.app_user WHERE id = '{W.Dave}'"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"DELETE FROM nina.refresh_token"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"DELETE FROM nina.recovery_request"));
    }

    [Fact]
    public async Task A_user_can_update_only_their_own_profile_fields_with_a_persistent_display_name()
    {
        await using (var dave = await AsApp(W.Dave))
        {
            Assert.Equal(1, await dave.ExecAsync($"UPDATE nina.app_user SET display_name = 'David', locale = 'en-US', timezone = 'UTC' WHERE id = '{W.Dave}'"));
            await dave.CommitAsync();
        }

        Assert.Equal("David", await OwnerScalarAsync<string>($"SELECT display_name FROM nina.app_user WHERE id = '{W.Dave}'"));
        Assert.Equal("23514", await FailsAsync(Role.App, W.Dave, $"UPDATE nina.app_user SET display_name = '{new string('x', 81)}' WHERE id = '{W.Dave}'"));
        Assert.Equal("23514", await FailsAsync(Role.App, W.Dave, $"UPDATE nina.app_user SET display_name = '   ' WHERE id = '{W.Dave}'"));
    }

    [Fact]
    public async Task N5_a_user_cannot_grant_themselves_a_plan_only_the_config_admin_function_can()
    {
        await OwnerAsync($"INSERT INTO nina.family (id, owner_user_id) VALUES (gen_random_uuid(), '{W.Dave}')");
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, "INSERT INTO nina.family_entitlement (family_id, plan_id, source, status) SELECT id, 2, 'MANUAL_GRANT', 'ACTIVE' FROM nina.family"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, "INSERT INTO nina.family_entitlement_member (entitlement_id, user_id, member_role) VALUES (gen_random_uuid(), (SELECT current_setting('nina.user_id')::uuid), 'HOLDER')"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, "INSERT INTO nina.subscription (family_id, plan_id, store, product_ref, original_transaction_ref, store_transaction_ref, status) SELECT id, 2, 'APPLE', 'p', 'o', 't', 'ACTIVE' FROM nina.family"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, "UPDATE nina.plan SET max_premium_members = 99"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, "SELECT nina.grant_manual_entitlement(gen_random_uuid(), 'premium', NULL, 'x')"));
        Assert.Equal("42501", await FailsAsync(Role.Worker, null, "SELECT nina.grant_manual_entitlement(gen_random_uuid(), 'premium', NULL, 'x')"));

        var family = await OwnerScalarAsync<Guid>($"SELECT id FROM nina.family WHERE owner_user_id = '{W.Dave}'");
        Assert.Equal("NN040", await FailsAsync(Role.Config, null, $"SELECT nina.grant_manual_entitlement('{family}', 'premium', NULL, 'cortesia')"));    // sem ator
        Assert.Equal("NN040", await FailsAsync(Role.Config, W.Alice, $"SELECT nina.grant_manual_entitlement('{family}', 'premium', NULL, '')"));        // sem motivo
        await using (var admin = await OpenAsync(Role.Config, W.Alice))
        {
            await admin.ExecAsync($"SELECT nina.grant_manual_entitlement('{family}', 'premium', NULL, 'cortesia')");
            await admin.CommitAsync();
        }

        await using var s = await AsApp(W.Dave);
        Assert.Equal("premium", await s.ScalarAsync<string>("SELECT nina.my_plan_code()"));
        Assert.Equal(1, await s.ScalarAsync<long>("SELECT count(*) FROM nina.family_entitlement"));             // le o proprio entitlement
        await using var alice = await AsApp(W.Alice);
        Assert.Equal("free", await alice.ScalarAsync<string>("SELECT nina.my_plan_code()"));
        Assert.Equal(1, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE action = 'entitlement.manual_granted' AND actor_type = 'ADMIN' AND is_critical"));
    }

    [Fact]
    public async Task O3_the_app_has_no_oracle_for_the_plan_or_ownership_of_other_users()
    {
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"SELECT nina.user_plan_code('{W.Bob}')"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"SELECT nina.is_active_baby_owner('{W.Bob}')"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"SELECT nina.user_has_feature('{W.Bob}', 'x')"));
        await using var s = await AsApp(W.Dave);
        Assert.False(await s.ScalarAsync<bool>("SELECT nina.my_has_feature('qualquer')"));
    }

    [Fact]
    public async Task N6_the_outbox_is_insert_only_for_the_app_and_never_exposes_other_tenants_ids()
    {
        await SeedCredentialsAsync();
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, "SELECT aggregate_id FROM nina.outbox_message"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, "UPDATE nina.outbox_message SET processed_at = now()"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, "DELETE FROM nina.outbox_message"));
        // NR-11: o app so enfileira avisos de seguranca PARA SI (tipo do catalogo, agregado = o proprio usuario), por 4 colunas
        Assert.Null(await FailsAsync(Role.App, W.Dave, $"INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type, payload) VALUES ('USER', '{W.Dave}', 'SecurityNoticeRequested', '{{\"notice\": \"PASSWORD_CHANGED\"}}')"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type, payload) VALUES ('USER', '{W.Bob}', 'SecurityNoticeRequested', '{{\"notice\": \"PASSWORD_CHANGED\"}}')"));   // aviso a outro usuario
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type, event_key) VALUES ('USER', '{W.Dave}', 'SecurityNoticeRequested', 'k1')"));   // event_key e do sistema
    }

    [Fact]
    public async Task The_outbox_payload_rejects_pii_even_when_nested_or_renamed()
    {
        // chaves fora do catalogo do tipo (inclusive PII renomeada/aninhada) sao recusadas pelo gatilho do catalogo (NR-11)...
        foreach (var payload in new[] { "{\"email\": \"a@b.org\"}", "{\"e_mail\": \"x\"}", "{\"user\": {\"E-Mail\": \"x\"}}", "{\"info\": \"escreva a bob@example.org\"}", "{\"list\": [{\"password\": 1}]}" })
        {
            Assert.Equal("NN080", await FailsAsync(Role.App, W.Dave, $"INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type, payload) VALUES ('USER', '{W.Dave}', 'SecurityNoticeRequested', @p::jsonb)", P("p", payload)));
        }

        // ...e a CHECK de PII continua como defesa em profundidade para as chaves permitidas (valor com formato de e-mail, tamanho, agregado)
        Assert.Equal("23514", await FailsAsync(Role.Owner, null, "INSERT INTO nina.outbox_message (aggregate_type, event_type, payload) VALUES ('USER', 'SharedBabyDeletedNotice', '{\"baby_id\": \"bob@example.org\"}')"));
        Assert.Equal("23514", await FailsAsync(Role.Owner, null, "INSERT INTO nina.outbox_message (aggregate_type, event_type, payload) VALUES ('USER', 'SharedBabyDeletedNotice', @p::jsonb)", P("p", "{\"baby_id\": \"" + new string('x', 5000) + "\"}")));
        Assert.Equal("NN080", await FailsAsync(Role.Owner, null, "INSERT INTO nina.outbox_message (aggregate_type, event_type) VALUES ('lowercase', 'SharedBabyDeletedNotice')"));
    }

    // ----------------------------------------------------- fluxos pre-autenticacao (Identity)

    [Fact]
    public async Task Pre_auth_lookups_return_only_the_row_for_the_given_key()
    {
        await SeedCredentialsAsync();
        await using var anon = await AsApp(null);
        var row = Assert.Single(await anon.RowsAsync("SELECT id, email, password_hash FROM nina.auth_lookup_user_by_email('  ALICE@example.ORG ')"));
        Assert.Equal(W.Alice, (Guid)row[0]!);
        Assert.Equal("$argon2id$alice", row[2]);
        Assert.Empty(await anon.RowsAsync("SELECT * FROM nina.auth_lookup_user_by_email('naoexiste@example.org')"));
        Assert.Empty(await anon.RowsAsync("SELECT * FROM nina.auth_lookup_user_by_email('%')"));              // sem curinga: nao enumera
        Assert.Empty(await anon.RowsAsync("SELECT * FROM nina.auth_lookup_user_by_email('')"));
        Assert.Equal(W.Alice, (await anon.RowsAsync("SELECT user_id FROM nina.auth_lookup_identity('GOOGLE', 'g-alice')")).Single()[0]);
        Assert.Empty(await anon.RowsAsync("SELECT * FROM nina.auth_lookup_identity('GOOGLE', 'g-%')"));
    }

    [Fact]
    public async Task Recovery_tokens_are_consumed_once_by_hash_and_only_while_valid()
    {
        await SeedCredentialsAsync();
        await using var anon = await AsApp(null);
        var aliceHash = Convert.FromHexString(string.Concat(Enumerable.Repeat("a2", 32)));
        Assert.Null(await anon.ScalarAsync<Guid?>("SELECT nina.auth_consume_recovery(@h, now())", Bytes("h", Hash("desconhecido"))));
        Assert.Equal(W.Alice, await anon.ScalarAsync<Guid?>("SELECT nina.auth_consume_recovery(@h, now())", Bytes("h", aliceHash)));
        Assert.Null(await anon.ScalarAsync<Guid?>("SELECT nina.auth_consume_recovery(@h, now())", Bytes("h", aliceHash)));     // uso unico
        Assert.Null(await anon.ScalarAsync<Guid?>("SELECT nina.auth_consume_recovery(@h, now() + interval '2 hours')", Bytes("h", Convert.FromHexString(string.Concat(Enumerable.Repeat("b2", 32))))));   // expirado
    }

    [Fact]
    public async Task A_user_can_only_insert_their_own_identity_rows()
    {
        var id = Guid.NewGuid();
        Assert.Null(await FailsAsync(Role.App, id, $"INSERT INTO nina.app_user (id, email) VALUES ('{id}', 'novo@example.org')"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"INSERT INTO nina.app_user (id, email) VALUES ('{id}', 'novo@example.org')"));       // id != contexto
        Assert.Equal("42501", await FailsAsync(Role.App, null, $"INSERT INTO nina.app_user (id, email) VALUES ('{id}', 'novo@example.org')"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"INSERT INTO nina.user_credential (user_id, password_hash, hash_algorithm) VALUES ('{W.Bob}', 'x', 'ARGON2ID')"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"INSERT INTO nina.user_identity (user_id, provider, provider_subject) VALUES ('{W.Bob}', 'GOOGLE', 'x')"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"INSERT INTO nina.auth_session (user_id, device_id, platform, absolute_expires_at) VALUES ('{W.Bob}', gen_random_uuid(), 'IOS', now() + interval '1 day')"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"INSERT INTO nina.recovery_request (user_id, token_hash, expires_at) VALUES ('{W.Bob}', decode(repeat('ee', 32), 'hex'), now() + interval '1 day')"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, "INSERT INTO nina.refresh_token (session_id, token_hash, expires_at) VALUES (gen_random_uuid(), decode(repeat('ef', 32), 'hex'), now() + interval '1 day')"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"INSERT INTO nina.app_user (id, email, status) VALUES ('{Guid.NewGuid()}', 'x@example.org', 'DELETED')"));
    }

    [Fact]
    public async Task A_used_secret_stays_used_and_refresh_tokens_follow_only_the_owners_session()
    {
        await SeedCredentialsAsync();
        await using (var alice = await AsApp(W.Alice))
        {
            Assert.Equal(1, await alice.ScalarAsync<long>("SELECT count(*) FROM nina.refresh_token"));
            Assert.Equal(1, await alice.ExecAsync("UPDATE nina.refresh_token SET used_at = now()"));
            await alice.CommitAsync();
        }

        Assert.Equal("NN053", await FailsAsync(Role.App, W.Alice, "UPDATE nina.refresh_token SET used_at = NULL"));
        await OwnerAsync($"UPDATE nina.recovery_request SET used_at = now() WHERE user_id = '{W.Alice}'");
        Assert.Equal("NN053", await FailsAsync(Role.Owner, null, $"UPDATE nina.recovery_request SET used_at = NULL WHERE user_id = '{W.Alice}'"));
    }

    // ----------------------------------------------------------------- identidade externa

    [Fact]
    public async Task A_user_unlinks_their_own_identity_but_never_the_last_way_to_sign_in()
    {
        await SeedCredentialsAsync();
        await using (var alice = await AsApp(W.Alice))
        {
            Assert.Equal(1, await alice.ExecAsync("DELETE FROM nina.user_identity WHERE provider = 'GOOGLE'"));      // tem senha: ok
            await alice.CommitAsync();
        }

        await OwnerAsync($"INSERT INTO nina.user_identity (user_id, provider, provider_subject) VALUES ('{W.Dave}', 'GOOGLE', 'g-dave')");   // Dave: so social
        Assert.Equal("NN055", await FailsAsync(Role.App, W.Dave, "DELETE FROM nina.user_identity"));
        await OwnerAsync($"INSERT INTO nina.user_identity (user_id, provider, provider_subject) VALUES ('{W.Dave}', 'APPLE', 'a-dave')");
        await using var dave = await AsApp(W.Dave);
        Assert.Equal(1, await dave.ExecAsync("DELETE FROM nina.user_identity WHERE provider = 'GOOGLE'"));          // sobra a Apple
    }

    // ----------------------------------------------------------------- token de push

    [Fact]
    public async Task O4_the_push_token_follows_the_device_to_a_new_user_only_when_the_previous_owner_no_longer_has_a_session_there()
    {
        await SeedCredentialsAsync();
        var device = Guid.NewGuid();
        await OwnerAsync($"INSERT INTO nina.auth_session (id, user_id, device_id, platform, absolute_expires_at) VALUES ('{device}', '{W.Alice}', '{device}', 'IOS', now() + interval '30 days')");
        const string register = "SELECT * FROM nina.register_push_token(@d, 'APNS', @t, 'SANDBOX', 'pt-BR', '1.0', true, now())";

        await using (var alice = await AsApp(W.Alice))
        {
            Assert.Single(await alice.RowsAsync(register, P("d", device), P("t", "tok-1")));
            await alice.CommitAsync();
        }

        // Bob entra no MESMO aparelho enquanto a sessao da Alice segue ativa: conflito, sem revelar quem
        await OwnerAsync($"INSERT INTO nina.auth_session (id, user_id, device_id, platform, absolute_expires_at) VALUES (gen_random_uuid(), '{W.Bob}', '{device}', 'IOS', now() + interval '30 days')");
        Assert.Equal("23505", await FailsAsync(Role.App, W.Bob, "SELECT * FROM nina.register_push_token(@d, 'APNS', @t, 'SANDBOX', 'pt-BR', '1.0', true, now())", P("d", device), P("t", "tok-1")));
        // a sessao da Alice acaba (logout, expiracao...): o token passa a Bob e o vinculo antigo some (notificacoes nao vazam)
        await OwnerAsync($"UPDATE nina.auth_session SET revoked_at = now(), revoked_reason = 'LOGOUT' WHERE user_id = '{W.Alice}' AND device_id = '{device}'");
        await using (var bob = await AsApp(W.Bob))
        {
            Assert.Single(await bob.RowsAsync(register, P("d", device), P("t", "tok-1")));
            await bob.CommitAsync();
        }

        Assert.Equal(W.Bob, await OwnerScalarAsync<Guid>("SELECT user_id FROM nina.device_push_token WHERE token = 'tok-1'"));
        Assert.Equal(1, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.device_push_token"));
        // metadados novos persistem
        Assert.Equal("SANDBOX", await OwnerScalarAsync<string>("SELECT environment FROM nina.device_push_token"));
        Assert.True(await OwnerScalarAsync<bool>("SELECT os_notifications_authorized FROM nina.device_push_token"));
        Assert.Equal("pt-BR", await OwnerScalarAsync<string>("SELECT locale FROM nina.device_push_token"));
    }

    [Fact]
    public async Task Push_tokens_need_an_active_session_on_the_device_and_cannot_be_written_directly()
    {
        Assert.Equal("NN015", await FailsAsync(Role.App, W.Dave, "SELECT * FROM nina.register_push_token(gen_random_uuid(), 'APNS', 't', NULL, NULL, NULL, NULL, now())"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"INSERT INTO nina.device_push_token (user_id, device_id, platform, token) VALUES ('{W.Dave}', gen_random_uuid(), 'APNS', 't')"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, "UPDATE nina.device_push_token SET token = 'x'"));
    }

    // ----------------------------------------------------------------- codigos de e-mail / troca de e-mail

    [Fact]
    public async Task Email_verification_codes_have_an_attempt_counter_and_are_invalidated_when_exhausted()
    {
        var code = Hash("123456");
        await OwnerAsync($"UPDATE nina.app_user SET email_verified_at = NULL WHERE id = '{W.Alice}'");      // codigo so existe para conta nao verificada
        Assert.NotNull(await IssueEmailCodeAsync(W.Alice, code));

        async Task<Guid?> Verify(byte[] h)
        {
            await using var anon = await AsApp(null);
            var r = await anon.ScalarAsync<Guid?>("SELECT nina.email_code_verify('alice@example.org', @h, now())", Bytes("h", h));
            await anon.CommitAsync();
            return r;
        }

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(await Verify(Hash("000000")));
        }

        Assert.Null(await Verify(code));                                       // esgotou: nem o certo vale mais
        Assert.Equal(5, await OwnerScalarAsync<short>($"SELECT attempts FROM nina.email_verification_code WHERE user_id = '{W.Alice}'"));
        Assert.NotNull(await IssueEmailCodeAsync(W.Alice, code));

        Assert.Equal(W.Alice, await Verify(code));
        Assert.Null(await Verify(code));                                       // uso unico
        Assert.Equal(1, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.email_verification_code WHERE used_at IS NOT NULL"));
    }

    [Fact]
    public async Task Email_change_is_pending_until_the_code_sent_to_the_new_address_is_confirmed()
    {
        var code = Hash("654321");
        var reauth = await ReauthAsync(W.Alice, "ACCOUNT_EMAIL_CHANGE");
        await using (var noProof = await AsApp(W.Alice))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => EmailChangeCreateAsync(noProof, W.Alice, "novo@example.org", code, Hash("sem-reauth")));
            Assert.Equal("NN014", ex.SqlState);
        }

        await using (var alice = await AsApp(W.Alice))
        {
            await EmailChangeCreateAsync(alice, W.Alice, "Novo@Example.org", code, reauth);
            await alice.CommitAsync();
        }

        Assert.Equal("alice@example.org", await OwnerScalarAsync<string>($"SELECT email FROM nina.app_user WHERE id = '{W.Alice}'"));       // ainda nao muda
        await using (var alice = await AsApp(W.Alice))
        {
            Assert.Equal("INVALID", await alice.ScalarAsync<string>("SELECT nina.email_change_confirm(@c, now())", Bytes("c", Hash("errado"))));
            await alice.CommitAsync();
        }

        Assert.Equal(1, await OwnerScalarAsync<short>($"SELECT attempts FROM nina.email_change_request WHERE user_id = '{W.Alice}'"));
        await using (var alice = await AsApp(W.Alice))
        {
            Assert.Equal("CHANGED", await alice.ScalarAsync<string>("SELECT nina.email_change_confirm(@c, now())", Bytes("c", code)));
            await alice.CommitAsync();
        }

        Assert.Equal("Novo@Example.org", await OwnerScalarAsync<string>($"SELECT email FROM nina.app_user WHERE id = '{W.Alice}'"));
        Assert.Equal(1, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE action = 'auth.email_changed' AND is_critical"));
    }

    [Fact]
    public async Task Email_change_to_an_address_already_in_use_is_refused_without_changing_anything()
    {
        var code = Hash("111111");
        var reauth = await ReauthAsync(W.Alice, "ACCOUNT_EMAIL_CHANGE");
        await using (var alice = await AsApp(W.Alice))
        {
            await EmailChangeCreateAsync(alice, W.Alice, "bob@example.org", code, reauth);
            Assert.Equal("EMAIL_IN_USE", await alice.ScalarAsync<string>("SELECT nina.email_change_confirm(@c, now())", Bytes("c", code)));
        }

        Assert.Equal("alice@example.org", await OwnerScalarAsync<string>($"SELECT email FROM nina.app_user WHERE id = '{W.Alice}'"));
    }

    // ----------------------------------------------------------------- ledger de reautenticacao

    [Fact]
    public async Task The_reauth_jti_is_single_use_across_instances_and_counts_replays()
    {
        var session = await SessionAsync(W.Alice);
        var hash = await IssueReauthAsync(W.Alice, session, ["ACCOUNT_DELETE"]);
        await using (var alice = await AsApp(W.Alice))
        {
            Assert.True(await alice.ScalarAsync<bool>("SELECT nina.consume_reauth_jti(@h, 'ACCOUNT_DELETE', @s)", Bytes("h", hash), P("s", session)));
            await alice.CommitAsync();
        }

        await using (var alice = await AsApp(W.Alice))
        {
            Assert.False(await alice.ScalarAsync<bool>("SELECT nina.consume_reauth_jti(@h, 'ACCOUNT_DELETE', @s)", Bytes("h", hash), P("s", session)));
            await alice.CommitAsync();
        }

        await using (var bob = await AsApp(W.Bob))
        {
            Assert.False(await bob.ScalarAsync<bool>("SELECT nina.consume_reauth_jti(@h, 'ACCOUNT_DELETE', @s)", Bytes("h", hash), P("s", session)));   // nao "queima" nem le o do outro
            await bob.CommitAsync();
        }

        Assert.Equal(2, await OwnerScalarAsync<short>("SELECT attempts FROM nina.reauth_jti"));
        Assert.Equal(W.Alice, await OwnerScalarAsync<Guid>("SELECT user_id FROM nina.reauth_jti"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, "SELECT * FROM nina.reauth_jti"));
        Assert.Equal("23514", await FailsAsync(Role.Owner, null, $"INSERT INTO nina.reauth_jti (jti_hash, user_id, session_id, scopes, issued_at, expires_at) VALUES (decode(repeat('01', 32), 'hex'), '{W.Alice}', '{session}', ARRAY['QUALQUER'], now(), now() + interval '1 minute')"));
    }

    [Fact]
    public async Task D1_account_deletion_confirmation_is_proven_by_the_reauth_ledger_and_cannot_be_forged()
    {
        // o app nao insere o pedido nem escreve confirmed_at
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, $"INSERT INTO nina.account_deletion_request (user_id, grace_days, scheduled_for, confirmed_at, confirmation_method) VALUES ('{W.Alice}', 1, now(), now(), 'REAUTHENTICATION')"));
        // nao-Owner nao pede exclusao
        Assert.Equal("NN012", await FailsAsync(Role.App, W.Carol, "SELECT nina.request_account_deletion(NULL)"));
        // confirmacao sem prova
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Alice, "SELECT nina.request_account_deletion(@h)", Bytes("h", Hash("inventado"))));

        var jti = await ReauthAsync(W.Alice, "ACCOUNT_DELETE");
        Guid request;
        await using (var alice = await AsApp(W.Alice))
        {
            request = await alice.ScalarAsync<Guid>("SELECT nina.request_account_deletion(@h)", Bytes("h", jti));
            await alice.CommitAsync();
        }

        Assert.Equal("SCHEDULED", await OwnerScalarAsync<string>($"SELECT status FROM nina.account_deletion_request WHERE id = '{request}'"));
        Assert.Equal(7, await OwnerScalarAsync<int>($"SELECT grace_days FROM nina.account_deletion_request WHERE id = '{request}'"));                       // trigger recalcula
        Assert.Equal("REAUTHENTICATION", await OwnerScalarAsync<string>($"SELECT confirmation_method FROM nina.account_deletion_request WHERE id = '{request}'"));
        // o pedido ja esta confirmado (e o jti ja vinculado a ele): nao se confirma de novo
        Assert.Equal("NN011", await FailsAsync(Role.App, W.Alice, "SELECT nina.confirm_account_deletion(@r, @h)", P("r", request), Bytes("h", jti)));
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Alice, "SELECT nina.request_account_deletion(@h)", Bytes("h", jti)));          // jti vinculado nao serve a outro pedido
        // o app nao altera a confirmacao nem a janela depois
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, $"UPDATE nina.account_deletion_request SET confirmed_at = NULL, confirmation_method = NULL WHERE id = '{request}'"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, $"UPDATE nina.account_deletion_request SET scheduled_for = now() WHERE id = '{request}'"));
        Assert.Equal("NN001", await FailsAsync(Role.Owner, null, $"UPDATE nina.account_deletion_request SET confirmed_at = NULL, confirmation_method = NULL WHERE id = '{request}'"));
        // cancelar na janela continua possivel
        await using var cancel = await AsApp(W.Alice);
        Assert.Equal(1, await cancel.ExecAsync($"UPDATE nina.account_deletion_request SET status = 'CANCELLED' WHERE id = '{request}'"));
    }

    [Fact]
    public async Task A_legacy_unconfirmed_deletion_request_can_be_confirmed_later_with_a_proven_reauth()
    {
        // pelo app o pedido nasce sempre confirmado (NR-03/NR-14): sem reautenticacao nao ha pedido
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Alice, "SELECT nina.request_account_deletion(NULL)"));
        var request = Guid.NewGuid();
        await OwnerAsync($"INSERT INTO nina.account_deletion_request (id, user_id, grace_days, scheduled_for) VALUES ('{request}', '{W.Alice}', 1, now())");

        Assert.Null(await OwnerScalarAsync<DateTime?>($"SELECT confirmed_at FROM nina.account_deletion_request WHERE id = '{request}'"));
        var jti = await ReauthAsync(W.Alice, "ACCOUNT_DELETE");
        await using (var alice = await AsApp(W.Alice))
        {
            await alice.ExecAsync("SELECT nina.confirm_account_deletion(@r, @h)", P("r", request), Bytes("h", jti));
            await alice.CommitAsync();
        }

        Assert.NotNull(await OwnerScalarAsync<DateTime?>($"SELECT confirmed_at FROM nina.account_deletion_request WHERE id = '{request}'"));
        Assert.Equal("NN011", await FailsAsync(Role.App, W.Alice, "SELECT nina.confirm_account_deletion(@r, @h)", P("r", request), Bytes("h", await ReauthAsync(W.Alice, "ACCOUNT_DELETE"))));   // ja confirmado
    }
}
