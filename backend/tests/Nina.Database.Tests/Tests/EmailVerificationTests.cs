using Nina.Database.Tests.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Nina.Database.Tests.Tests;

/// <summary>
/// NR-09 (reteste): o app não grava <c>email_verified_at</c>. Só <c>email_code_verify</c> (código gerado e enviado pelo servidor), <c>email_change_confirm</c>
/// e <c>register_social_user</c> (atestado do provedor) o definem; e como "emitir o próprio código e verificá-lo" bastaria para se auto-verificar, a
/// emissão do código (e-mail atual e troca), e o cadastro social, exigem o MAC do servidor.
/// </summary>
[Collection(PgClusterGroup.Name)]
public sealed class EmailVerificationTests(PgCluster cluster) : DbTestBase(cluster)
{
    private async Task<Guid?> VerifyAsync(string email, byte[] code)
    {
        await using var anon = await AsApp(null);
        var id = await anon.ScalarAsync<Guid?>("SELECT nina.email_code_verify(@e, @h, now())", P("e", email), Bytes("h", code));
        await anon.CommitAsync();
        return id;
    }

    private static NpgsqlParameter MacParam(byte[]? mac) => new("mac", NpgsqlDbType.Bytea) { Value = (object?)mac ?? DBNull.Value };

    [Fact]
    public async Task NR09_the_app_cannot_write_email_verified_at_by_update_or_insert()
    {
        await OwnerAsync($"UPDATE nina.app_user SET email_verified_at = NULL WHERE id = '{W.Dave}'");
        // o ataque do reteste: UPDATE da propria linha e INSERT de conta pre-verificada com e-mail de terceiro
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"UPDATE nina.app_user SET email_verified_at = now() WHERE id = '{W.Dave}'"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, $"UPDATE nina.app_user SET email_verified_at = NULL WHERE id = '{W.Alice}'"));
        var id = Guid.NewGuid();
        Assert.Equal("42501", await FailsAsync(Role.App, id, $"INSERT INTO nina.app_user (id, email, email_verified_at) VALUES ('{id}', 'ceo@empresa.com', now())"));
        // o cadastro sem a coluna continua possivel e nasce NAO verificado
        await using (var s = await AsApp(id))
        {
            await s.ExecAsync($"INSERT INTO nina.app_user (id, email) VALUES ('{id}', 'ceo@empresa.com')");
            await s.CommitAsync();
        }

        Assert.Null(await OwnerScalarAsync<DateTime?>($"SELECT email_verified_at FROM nina.app_user WHERE id = '{id}'"));
        Assert.Equal("42501", await FailsAsync(Role.Worker, null, $"UPDATE nina.app_user SET email_verified_at = now() WHERE id = '{id}'"));
        Assert.Equal("42501", await FailsAsync(Role.Config, W.Dave, $"UPDATE nina.app_user SET email_verified_at = now() WHERE id = '{id}'"));
    }

    [Fact]
    public async Task NR09_issuing_ones_own_code_to_self_verify_fails_without_the_servers_mac()
    {
        var id = Guid.NewGuid();
        await using (var s = await AsApp(id))
        {
            await s.ExecAsync($"INSERT INTO nina.app_user (id, email) VALUES ('{id}', 'ceo@empresa.com')");
            await s.CommitAsync();
        }

        var known = Hash("codigo-que-eu-conheco");
        const string issue = "SELECT nina.email_code_issue(@h, now(), now() + interval '10 minutes', NULL, @mac)";
        Assert.Equal("NN071", await FailsAsync(Role.App, id, issue, Bytes("h", known), MacParam(null)));
        Assert.Equal("NN071", await FailsAsync(Role.App, id, issue, Bytes("h", known), MacParam(new byte[32])));
        Assert.Equal("NN071", await FailsAsync(Role.App, id, issue, Bytes("h", known), MacParam(ServerSigner.Sign("email.code", ServerSigner.EmailCode(id, known, DateTimeOffset.UtcNow), Hash("outra-chave")))));
        Assert.Equal("42501", await FailsAsync(Role.App, id, "INSERT INTO nina.email_verification_code (user_id, code_hash, expires_at) VALUES (@u, @h, now() + interval '1 hour')", P("u", id), Bytes("h", known)));
        Assert.Null(await VerifyAsync("ceo@empresa.com", known));
        Assert.Null(await OwnerScalarAsync<DateTime?>($"SELECT email_verified_at FROM nina.app_user WHERE id = '{id}'"));
        Assert.Equal(0, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.email_verification_code"));
    }

    [Fact]
    public async Task NR09_the_servers_code_verifies_once_and_is_the_only_way_to_become_verified()
    {
        await OwnerAsync($"UPDATE nina.app_user SET email_verified_at = NULL WHERE id = '{W.Dave}'");
        var code = Hash("123456");
        Assert.NotNull(await IssueEmailCodeAsync(W.Dave, code));
        Assert.Null(await VerifyAsync("dave@example.org", Hash("errado")));                                // errado: persistente, sem excecao
        Assert.Null(await OwnerScalarAsync<DateTime?>($"SELECT email_verified_at FROM nina.app_user WHERE id = '{W.Dave}'"));
        Assert.Equal(1, await OwnerScalarAsync<short>($"SELECT attempts FROM nina.email_verification_code WHERE user_id = '{W.Dave}'"));
        Assert.Equal(W.Dave, await VerifyAsync(" DAVE@example.org ", code));
        Assert.NotNull(await OwnerScalarAsync<DateTime?>($"SELECT email_verified_at FROM nina.app_user WHERE id = '{W.Dave}'"));
        Assert.Null(await VerifyAsync("dave@example.org", code));                                          // uso unico
        Assert.Null(await VerifyAsync("naoexiste@example.org", code));                                     // conta inexistente: mesma resposta
    }

    [Fact]
    public async Task NR09_resending_respects_the_interval_invalidates_the_previous_code_and_skips_verified_accounts()
    {
        await OwnerAsync($"UPDATE nina.app_user SET email_verified_at = NULL WHERE id = '{W.Dave}'");
        var first = Hash("primeiro");
        var second = Hash("segundo");
        Assert.NotNull(await IssueEmailCodeAsync(W.Dave, first, minInterval: TimeSpan.FromSeconds(60)));
        Assert.Null(await IssueEmailCodeAsync(W.Dave, second, minInterval: TimeSpan.FromSeconds(60)));          // dentro do intervalo de reenvio: nada
        Assert.Equal(1, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.email_verification_code WHERE user_id = '{W.Dave}'"));
        Assert.NotNull(await IssueEmailCodeAsync(W.Dave, second, minInterval: TimeSpan.Zero));                  // fora do intervalo: novo codigo
        Assert.Null(await VerifyAsync("dave@example.org", first));                                              // o anterior foi invalidado
        Assert.Equal(W.Dave, await VerifyAsync("dave@example.org", second));
        Assert.Null(await IssueEmailCodeAsync(W.Alice, Hash("para-conta-verificada")));                         // Alice ja verificada: nao ha codigo a emitir
        Assert.Equal(0, await OwnerScalarAsync<long>($"SELECT count(*) FROM nina.email_verification_code WHERE user_id = '{W.Alice}'"));
    }

    [Fact]
    public async Task NR09_social_registration_creates_a_verified_account_only_with_the_servers_attestation()
    {
        var id = Guid.NewGuid();
        const string call = "SELECT nina.register_social_user(@id, @email, 'Ana', 'pt-BR', 'America/Sao_Paulo', @p, @s, now(), @mac)";
        byte[] Mac(Guid user, string provider, string subject, string email) => ServerSigner.Sign("social.register", ServerSigner.SocialRegister(user, provider, subject, email));
        NpgsqlParameter[] Args(Guid user, string email, string provider, string subject, byte[]? mac) =>
            [P("id", user), P("email", email), P("p", provider), P("s", subject), MacParam(mac)];

        // sem atestado / atestado de outra conta, provedor, subject ou e-mail / contexto de outro usuario
        Assert.Equal("NN071", await FailsAsync(Role.App, id, call, Args(id, "Ana@Example.org", "GOOGLE", "g-1", null)));
        Assert.Equal("NN071", await FailsAsync(Role.App, id, call, Args(id, "ana@example.org", "GOOGLE", "g-1", new byte[32])));
        Assert.Equal("NN071", await FailsAsync(Role.App, id, call, Args(id, "ana@example.org", "GOOGLE", "g-1", Mac(id, "APPLE", "g-1", "ana@example.org"))));
        Assert.Equal("NN071", await FailsAsync(Role.App, id, call, Args(id, "ana@example.org", "GOOGLE", "g-1", Mac(id, "GOOGLE", "g-2", "ana@example.org"))));
        Assert.Equal("NN071", await FailsAsync(Role.App, id, call, Args(id, "ceo@empresa.com", "GOOGLE", "g-1", Mac(id, "GOOGLE", "g-1", "ana@example.org"))));
        Assert.Equal("NN015", await FailsAsync(Role.App, W.Dave, call, Args(id, "ana@example.org", "GOOGLE", "g-1", Mac(id, "GOOGLE", "g-1", "ana@example.org"))));
        Assert.Equal("NN015", await FailsAsync(Role.App, null, call, Args(id, "ana@example.org", "GOOGLE", "g-1", Mac(id, "GOOGLE", "g-1", "ana@example.org"))));
        // e-mail ja em uso (mesmo com atestado valido): conflito, nada criado
        Assert.Equal("23505", await FailsAsync(Role.App, id, call, Args(id, "alice@example.org", "GOOGLE", "g-1", Mac(id, "GOOGLE", "g-1", "alice@example.org"))));

        await using (var s = await AsApp(id))
        {
            await s.ExecAsync(call, Args(id, "Ana@Example.org", "GOOGLE", "g-1", Mac(id, "GOOGLE", "g-1", "Ana@Example.org")));
            await s.CommitAsync();
        }

        Assert.NotNull(await OwnerScalarAsync<DateTime?>($"SELECT email_verified_at FROM nina.app_user WHERE id = '{id}'"));
        Assert.Equal("GOOGLE", await OwnerScalarAsync<string>($"SELECT provider FROM nina.user_identity WHERE user_id = '{id}' AND provider_subject = 'g-1'"));
        await using var me = await AsApp(id);
        Assert.Equal(1, await me.ScalarAsync<long>("SELECT count(*) FROM nina.app_user"));
    }

    [Fact]
    public async Task NR09_the_email_change_code_is_also_signed_and_confirming_it_verifies_the_new_address()
    {
        var reauth = await ReauthAsync(W.Alice, "ACCOUNT_EMAIL_CHANGE");
        var code = Hash("troca");
        await using (var alice = await AsApp(W.Alice))
        {
            // codigo emitido por quem nao tem a chave (MAC de outra mensagem): recusado
            var ex = await Assert.ThrowsAsync<PostgresException>(() => alice.ExecAsync(
                "SELECT nina.email_change_create('ceo@empresa.com', @c, now(), now() + interval '10 minutes', @r, @mac)",
                Bytes("c", code), Bytes("r", reauth), Bytes("mac", ServerSigner.Sign("email.change", "qualquer"))));
            Assert.Equal("NN071", ex.SqlState);
        }

        await using (var alice = await AsApp(W.Alice))
        {
            await EmailChangeCreateAsync(alice, W.Alice, "novo@example.org", code, reauth);
            Assert.Equal("CHANGED", await alice.ScalarAsync<string>("SELECT nina.email_change_confirm(@c, now())", Bytes("c", code)));
            await alice.CommitAsync();
        }

        Assert.Equal("novo@example.org", await OwnerScalarAsync<string>($"SELECT email FROM nina.app_user WHERE id = '{W.Alice}'"));
        Assert.NotNull(await OwnerScalarAsync<DateTime?>($"SELECT email_verified_at FROM nina.app_user WHERE id = '{W.Alice}'"));
    }
}
