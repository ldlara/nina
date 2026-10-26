using Nina.Database.Tests.Infrastructure;
using Npgsql;

namespace Nina.Database.Tests.Tests;

/// <summary>
/// NR-07 (reteste): a exclusão de bebê pelo Owner não é mais um UPDATE de <c>deleted_at</c>. Só <c>nina.delete_baby</c> (contrato 1.0.1): Owner ativo,
/// reautenticação <c>BABY_DELETE</c> comprovada e vinculada ao bebê, reconhecimento quando há outros cuidadores, auditoria com o jti, aviso a todos pelo
/// outbox e apagamento (<c>erase_baby</c>); depois disso eventos, vínculos e a casca ficam ilegíveis para o app. A exclusão continua IMEDIATA
/// (a janela de arrependimento é decisão de produto, V2-05).
/// </summary>
[Collection(PgClusterGroup.Name)]
public sealed class BabyDeletionTests(PgCluster cluster) : DbTestBase(cluster)
{
    private async Task SeedAsync()
    {
        await OwnerAsync(
            $"""
            INSERT INTO nina.sleep_session (id, baby_id, start_at, end_at, tz, sleep_type, source, notes, created_by, last_modified_by)
              VALUES (gen_random_uuid(), '{W.BabyA}', now() - interval '3 hours', now() - interval '2 hours', 'UTC', 'NAP', 'MANUAL', 'nota privada da Alice', '{W.Alice}', '{W.Alice}');
            INSERT INTO nina.data_export_request (user_id, status, baby_ids, file_ref, expires_at) VALUES ('{W.Alice}', 'READY', ARRAY['{W.BabyA}']::uuid[], 'exports/a.zip', now() + interval '3 days');
            """);
    }

    private async Task DeleteAsync(Guid user, Guid baby, bool acknowledge, byte[] proof)
    {
        await using var s = await AsApp(user);
        await s.ExecAsync("SELECT nina.delete_baby(@b, @h, @ack)", P("b", baby), Bytes("h", proof), P("ack", acknowledge));
        await s.CommitAsync();
    }

    [Fact]
    public async Task NR07_the_direct_soft_delete_is_closed_for_the_app_the_owner_of_the_schema_and_inserts()
    {
        const string update = "SET deleted_at = now(), display_name = NULL, birth_date = NULL, field_versions = '{}'";
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, $"UPDATE nina.baby {update} WHERE id = '{W.BabyA}'"));              // coluna fora do GRANT
        Assert.Equal("NN052", await FailsAsync(Role.Owner, null, $"UPDATE nina.baby {update} WHERE id = '{W.BabyA}'"));               // gatilho: sem a ficha de nina.delete_baby
        Assert.Equal("42501", await FailsAsync(Role.Worker, null, $"UPDATE nina.baby {update} WHERE id = '{W.BabyA}'"));             // o worker so apaga por erase_baby
        var family = Guid.NewGuid();
        await OwnerAsync($"INSERT INTO nina.family (id, owner_user_id) VALUES ('{family}', '{W.Dave}')");
        Assert.Equal("NN052", await FailsAsync(Role.App, W.Dave,
            $"INSERT INTO nina.baby (id, family_id, timezone, deleted_at) VALUES (gen_random_uuid(), '{family}', 'UTC', now())"));    // nem nasce ja excluido
        Assert.Null(await OwnerScalarAsync<DateTime?>($"SELECT deleted_at FROM nina.baby WHERE id = '{W.BabyA}'"));
    }

    [Fact]
    public async Task NR07_only_an_active_owner_with_a_proven_baby_delete_reauth_and_an_acknowledgement_deletes()
    {
        await SeedAsync();
        var proof = await ReauthAsync(W.Alice, "BABY_DELETE");
        // nao-Owner, bebe alheio e bebe inexistente recebem o MESMO erro uniforme
        foreach (var (user, baby) in new[] { (W.Erin, W.BabyA), (W.Carol, W.BabyA), (W.Dave, W.BabyA), (W.Bob, W.BabyA), (W.Alice, W.BabyB), (W.Alice, Guid.NewGuid()) })
        {
            Assert.Equal("NN059", await FailsAsync(Role.App, user, "SELECT nina.delete_baby(@b, @h, true)", P("b", baby), Bytes("h", proof)));
        }

        // sem prova, prova inventada, escopo errado ou jti nao consumido: nada acontece
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Alice, "SELECT nina.delete_baby(@b, NULL, true)", P("b", W.BabyA)));
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Alice, "SELECT nina.delete_baby(@b, @h, true)", P("b", W.BabyA), Bytes("h", Hash("inventado"))));
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Alice, "SELECT nina.delete_baby(@b, @h, true)", P("b", W.BabyA), Bytes("h", await ReauthAsync(W.Alice, "ACCOUNT_DELETE"))));
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Alice, "SELECT nina.delete_baby(@b, @h, true)", P("b", W.BabyA),
            Bytes("h", await IssueReauthAsync(W.Alice, await SessionAsync(W.Alice), ["BABY_DELETE"]))));
        // outros cuidadores ativos: sem o reconhecimento explicito (acknowledge_other_caregivers) a exclusao e recusada, mesmo com prova valida
        Assert.Equal("NN007", await FailsAsync(Role.App, W.Alice, "SELECT nina.delete_baby(@b, @h)", P("b", W.BabyA), Bytes("h", proof)));
        Assert.Equal("NN007", await FailsAsync(Role.App, W.Alice, "SELECT nina.delete_baby(@b, @h, false)", P("b", W.BabyA), Bytes("h", proof)));
        Assert.Null(await OwnerScalarAsync<DateTime?>($"SELECT deleted_at FROM nina.baby WHERE id = '{W.BabyA}'"));
        Assert.Equal(0, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.outbox_message"));
        Assert.Equal(0, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE action LIKE 'baby.%'"));

        // as recusas foram desfeitas junto com a transacao: a mesma prova ainda serve quando a exclusao e valida
        await DeleteAsync(W.Alice, W.BabyA, true, proof);
        Assert.NotNull(await OwnerScalarAsync<DateTime?>($"SELECT deleted_at FROM nina.baby WHERE id = '{W.BabyA}'"));
        Assert.Equal("NN059", await FailsAsync(Role.App, W.Alice, "SELECT nina.delete_baby(@b, @h, true)", P("b", W.BabyA), Bytes("h", proof)));   // ja excluido
    }

    [Fact]
    public async Task NR07_the_deletion_is_audited_with_the_proof_notifies_every_active_caregiver_and_erases_the_content()
    {
        await SeedAsync();
        var proof = await ReauthAsync(W.Alice, "BABY_DELETE");
        await DeleteAsync(W.Alice, W.BabyA, true, proof);

        // auditoria: ator, bebe, jti (hash) e quantos outros cuidadores; mais o apagamento propriamente dito
        Assert.Equal(1, await OwnerScalarAsync<long>(
            $"SELECT count(*) FROM nina.audit_event WHERE action = 'baby.deleted_by_owner' AND actor_user_id = '{W.Alice}' AND baby_id = '{W.BabyA}' AND is_critical " +
            $"AND metadata_safe->>'jti' = '{ServerSigner.Hex(proof)}' AND (metadata_safe->>'other_active_members')::int = 2"));
        Assert.Equal(1, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.audit_event WHERE action = 'baby.erased' AND actor_user_id = '{W.Alice}' AND is_critical"));
        Assert.True(await OwnerScalarAsync<bool>("SELECT ok FROM nina.verify_audit_chain()"));
        // aviso a todos: o Owner e os dois cuidadores, sem PII
        var notified = (await OwnerRowsAsync("SELECT aggregate_id::text, payload->>'baby_id' FROM nina.outbox_message WHERE event_type = 'SharedBabyDeletedNotice' ORDER BY 1"))
            .Select(r => (Guid.Parse((string)r[0]!), (string)r[1]!)).ToList();
        Assert.Equal(new[] { W.Alice, W.Carol, W.Erin }.Order().ToList(), notified.Select(n => n.Item1).Order().ToList());
        Assert.All(notified, n => Assert.Equal(W.BabyA.ToString(), n.Item2));
        Assert.Equal(1, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.outbox_message WHERE event_type = 'BabyDeleted'"));
        Assert.Equal(1, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.outbox_message WHERE event_type = 'ExportFileInvalidated'"));
        // conteudo apagado, exportacao invalidada, ledger de exclusoes e jti vinculado ao bebe
        Assert.Equal(0, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.sleep_session WHERE baby_id = '{W.BabyA}'"));
        Assert.Null(await OwnerScalarAsync<string>($"SELECT display_name FROM nina.baby WHERE id = '{W.BabyA}'"));
        Assert.Equal("EXPIRED", await OwnerScalarAsync<string>("SELECT status FROM nina.data_export_request"));
        Assert.Equal(1, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.erasure_ledger WHERE entity_type = 'BABY' AND entity_id = '{W.BabyA}'"));
        Assert.Equal(W.BabyA, await OwnerScalarAsync<Guid>($"SELECT bound_entity_id FROM nina.reauth_jti WHERE jti_hash = decode('{ServerSigner.Hex(proof)}', 'hex')"));
    }

    [Fact]
    public async Task NR07_after_the_deletion_events_links_and_the_shell_are_unreadable_and_unwritable_for_everyone_including_the_former_owner()
    {
        await SeedAsync();
        await DeleteAsync(W.Alice, W.BabyA, true, await ReauthAsync(W.Alice, "BABY_DELETE"));

        foreach (var user in new[] { W.Alice, W.Carol, W.Erin })
        {
            await using var s = await AsApp(user);
            Assert.Equal(0, await s.ScalarAsync<long>("SELECT count(*) FROM nina.baby"));
            Assert.Equal(0, await s.ScalarAsync<long>("SELECT count(*) FROM nina.sleep_session"));
            Assert.Equal(0, await s.ScalarAsync<long>("SELECT count(*) FROM nina.caregiver_membership"));               // vinculos ilegiveis (inclusive o proprio, ja encerrado)
            Assert.Equal(0, await s.ScalarAsync<long>("SELECT count(*) FROM nina.change_log"));
            Assert.Equal(0, await s.ScalarAsync<long>("SELECT count(*) FROM nina.tombstone"));
            Assert.False(await s.ScalarAsync<bool>("SELECT nina.can_read_baby(@b)", P("b", W.BabyA)));
            Assert.False(await s.ScalarAsync<bool>("SELECT nina.can_write_baby(@b)", P("b", W.BabyA)));
            Assert.Null(await s.ScalarAsync<string>("SELECT nina.baby_role(@b)", P("b", W.BabyA)));
            Assert.Empty(await s.RowsAsync("SELECT * FROM nina.sync_head(@b)", P("b", W.BabyA)));
            Assert.Empty(await s.RowsAsync("SELECT * FROM nina.baby_member_refs(@b)", P("b", W.BabyA)));
        }

        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice,
            $"INSERT INTO nina.sleep_session (id, baby_id, start_at, tz, sleep_type, source) VALUES (gen_random_uuid(), '{W.BabyA}', now(), 'UTC', 'NAP', 'MANUAL')"));
        await using (var alice = await AsApp(W.Alice))
        {
            Assert.Equal(0, await alice.ExecAsync($"UPDATE nina.baby SET display_name = 'volta' WHERE id = '{W.BabyA}'"));          // nem a casca se edita
        }

        // os vinculos existem fisicamente (ate a purga), todos encerrados, inclusive o do Owner
        Assert.Equal(3, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.caregiver_membership WHERE baby_id = '{W.BabyA}' AND status = 'REVOKED' AND revoked_reason = 'BABY_DELETED'"));
        // outros bebes seguem intactos
        await using var bob = await AsApp(W.Bob);
        Assert.Equal(1, await bob.ScalarAsync<long>("SELECT count(*) FROM nina.baby"));
        Assert.Equal(1, await bob.ScalarAsync<long>("SELECT count(*) FROM nina.caregiver_membership"));
    }

    [Fact]
    public async Task NR07_a_lone_owner_deletes_without_acknowledgement_and_the_proof_is_bound_to_that_baby()
    {
        var second = Guid.NewGuid();
        await OwnerAsync(
            $"""
            INSERT INTO nina.baby (id, family_id, display_name, birth_date, timezone) VALUES ('{second}', '{W.FamilyB}', 'Segundo', current_date - 10, 'UTC');
            INSERT INTO nina.caregiver_membership (baby_id, user_id, role, status, accepted_at) VALUES ('{second}', '{W.Bob}', 'OWNER', 'ACTIVE', now());
            """);
        var proof = await ReauthAsync(W.Bob, "BABY_DELETE");
        await DeleteAsync(W.Bob, W.BabyB, false, proof);

        Assert.NotNull(await OwnerScalarAsync<DateTime?>($"SELECT deleted_at FROM nina.baby WHERE id = '{W.BabyB}'"));
        Assert.Equal(0, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.outbox_message WHERE event_type = 'SharedBabyDeletedNotice'"));      // ninguem a avisar
        Assert.Equal(1, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE action = 'baby.deleted_by_owner'"));
        // o mesmo jti nao apaga o outro bebe
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Bob, "SELECT nina.delete_baby(@b, @h, false)", P("b", second), Bytes("h", proof)));
        Assert.Null(await OwnerScalarAsync<DateTime?>($"SELECT deleted_at FROM nina.baby WHERE id = '{second}'"));
    }

    [Fact]
    public async Task NR07_the_worker_erasure_path_still_works_and_also_hides_the_baby_from_every_member()
    {
        await SeedAsync();
        await using (var worker = await OpenAsync(Role.Worker))
        {
            await worker.ExecAsync("SELECT nina.erase_baby(@b)", P("b", W.BabyA));
            await worker.CommitAsync();
        }

        await using var carol = await AsApp(W.Carol);
        Assert.Equal(0, await carol.ScalarAsync<long>("SELECT count(*) FROM nina.baby"));
        Assert.Equal(0, await carol.ScalarAsync<long>("SELECT count(*) FROM nina.caregiver_membership"));
    }

    private async Task<List<object?[]>> OwnerRowsAsync(string sql)
    {
        await using var s = await OpenAsync(Role.Owner);
        return await s.RowsAsync(sql);
    }
}
