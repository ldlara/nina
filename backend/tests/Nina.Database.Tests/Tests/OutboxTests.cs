using Nina.Database.Tests.Infrastructure;

namespace Nina.Database.Tests.Tests;

/// <summary>
/// NR-11 (reteste): o outbox não é forjável pelo app. INSERT só pelas colunas <c>aggregate_type, aggregate_id, event_type, payload</c>, só de tipos do catálogo
/// destinados ao PRÓPRIO usuário (aviso de segurança), com chaves de payload do catálogo; <c>event_key</c>, <c>processed_at</c>, <c>attempts</c> e
/// <c>available_at</c> são do sistema. As mensagens de exclusão, convites e exportação só nascem das funções definer.
/// </summary>
[Collection(PgClusterGroup.Name)]
public sealed class OutboxTests(PgCluster cluster) : DbTestBase(cluster)
{
    private static string Insert(string aggregateType, Guid aggregate, string eventType, string payload = "{}") =>
        $"INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type, payload) VALUES ('{aggregateType}', '{aggregate}', '{eventType}', '{payload}')";

    [Fact]
    public async Task NR11_the_attack_of_the_retest_forged_notices_and_system_columns_are_all_refused()
    {
        // Eve (Dave) tenta: aviso falso de exclusao a cuidadores, invalidar arquivo de exportacao alheio, evento ja processado, event_key do sistema.
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, Insert("USER", W.Alice, "SharedBabyDeletedNotice", $"{{\"baby_id\": \"{W.BabyA}\"}}")));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, Insert("EXPORT", W.Alice, "ExportFileInvalidated")));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, Insert("BABY", W.BabyA, "BabyDeleted")));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, Insert("USER", W.Alice, "SecurityNoticeRequested", "{\"notice\": \"PASSWORD_CHANGED\"}")));      // aviso a OUTRA pessoa
        foreach (var column in new[] { "processed_at", "attempts", "available_at", "event_key", "created_at", "last_error_code", "dead_lettered_at" })
        {
            var value = column switch
            {
                "attempts" => "0",
                "event_key" => "'bloqueia-chave-do-sistema'",
                "last_error_code" => "'X'",
                _ => "now()",
            };
            Assert.Equal("42501", await FailsAsync(Role.App, W.Dave,
                $"INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type, payload, {column}) VALUES ('USER', '{W.Dave}', 'SecurityNoticeRequested', '{{\"notice\": \"PASSWORD_CHANGED\"}}', {value})"), column);
        }

        Assert.Equal(0, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.outbox_message"));
    }

    [Fact]
    public async Task NR11_the_app_enqueues_only_catalogued_security_notices_for_itself_with_a_validated_payload()
    {
        await using (var dave = await AsApp(W.Dave))
        {
            foreach (var notice in new[] { "NEW_DEVICE_LOGIN", "PASSWORD_CHANGED", "PASSWORD_RESET", "IDENTITY_LINKED", "EMAIL_CHANGED" })
            {
                await dave.ExecAsync(Insert("USER", W.Dave, "SecurityNoticeRequested", $"{{\"notice\": \"{notice}\"}}"));
            }

            await dave.CommitAsync();
        }

        Assert.Equal(5, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.outbox_message WHERE event_type = 'SecurityNoticeRequested'"));
        // payload invalido: aviso desconhecido, chave fora do catalogo (PII inclusive), vazio, agregado incompativel, tipo desconhecido
        Assert.Equal("NN080", await FailsAsync(Role.App, W.Dave, Insert("USER", W.Dave, "SecurityNoticeRequested", "{\"notice\": \"QUALQUER\"}")));
        Assert.Equal("NN080", await FailsAsync(Role.App, W.Dave, Insert("USER", W.Dave, "SecurityNoticeRequested")));
        Assert.Equal("NN080", await FailsAsync(Role.App, W.Dave, Insert("USER", W.Dave, "SecurityNoticeRequested", "{\"notice\": \"PASSWORD_RESET\", \"email\": \"x@y.org\"}")));
        Assert.Equal("NN080", await FailsAsync(Role.App, W.Dave, Insert("BABY", W.Dave, "SecurityNoticeRequested", "{\"notice\": \"PASSWORD_RESET\"}")));
        Assert.Equal("NN080", await FailsAsync(Role.App, W.Dave, Insert("USER", W.Dave, "Evt")));
        // continua insert-only: nao le, nao atualiza, nao apaga
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, "SELECT count(*) FROM nina.outbox_message"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, "UPDATE nina.outbox_message SET processed_at = now()"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, "DELETE FROM nina.outbox_message"));
    }

    [Fact]
    public async Task NR11_every_message_produced_by_the_definer_functions_belongs_to_the_catalog()
    {
        // fluxo completo de exclusao compartilhada + transferencia + saida de cuidador: todos os tipos emitidos pelas funcoes passam no catalogo
        var proof = await ReauthAsync(W.Alice, "OWNERSHIP_TRANSFER");
        await using (var alice = await AsApp(W.Alice))
        {
            await alice.ExecAsync("SELECT nina.transfer_ownership(@b, @m, @h)", P("b", W.BabyA), P("m", W.MembershipErin), Bytes("h", proof));
            await alice.CommitAsync();
        }

        await using (var carol = await AsApp(W.Carol))
        {
            await carol.ExecAsync("SELECT nina.leave_baby(@b)", P("b", W.BabyA));
            await carol.CommitAsync();
        }

        var delete = await ReauthAsync(W.Erin, "BABY_DELETE");
        await using (var erin = await AsApp(W.Erin))
        {
            await erin.ExecAsync("SELECT nina.delete_baby(@b, @h, true)", P("b", W.BabyA), Bytes("h", delete));
            await erin.CommitAsync();
        }

        var types = await OwnerScalarAsync<string>("SELECT string_agg(DISTINCT event_type, ',' ORDER BY event_type) FROM nina.outbox_message");
        Assert.Equal("BabyDeleted,BabyOwnershipTransferred,CaregiverLeft,SharedBabyDeletedNotice", types);
        Assert.Equal(0, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.outbox_message m WHERE NOT EXISTS (SELECT 1 FROM nina.outbox_event_type t WHERE t.event_type = m.event_type AND t.aggregate_type = m.aggregate_type)"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, "SELECT * FROM nina.outbox_event_type"));
    }

    [Fact]
    public async Task NR11_the_worker_keeps_its_operational_columns_but_also_cannot_enqueue_unknown_types()
    {
        await OwnerAsync(Insert("BABY", W.BabyA, "BabyDeleted"));
        await using (var worker = await OpenAsync(Role.Worker))
        {
            Assert.Equal(1, await worker.ExecAsync("UPDATE nina.outbox_message SET processed_at = now(), attempts = attempts + 1"));
            await worker.ExecAsync("INSERT INTO nina.outbox_message (aggregate_type, aggregate_id, event_type, event_key) VALUES ('BABY', gen_random_uuid(), 'CaregiverJoined', 'sys-1')");
            await worker.CommitAsync();
        }

        Assert.Equal("NN080", await FailsAsync(Role.Worker, null, "INSERT INTO nina.outbox_message (aggregate_type, event_type) VALUES ('BABY', 'TipoNovoSemMigracao')"));
        Assert.Equal(1, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.outbox_message WHERE event_key = 'sys-1'"));
    }
}
