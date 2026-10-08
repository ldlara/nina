using Nina.Database.Tests.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Nina.Database.Tests.Tests;

/// <summary>
/// NR-03 (reteste): o livro-razão de reautenticação não é auto-emitível. Só vale jti EMITIDO pela API (MAC da chave servidor-banco), vinculado a
/// usuário, sessão, escopos e expiração; as funções sensíveis (transfer_ownership, request_account_deletion, open_privacy_request,
/// email_change_create, delete_baby) exigem esse jti emitido e consumido, nunca um valor inventado por quem executa SQL.
/// </summary>
[Collection(PgClusterGroup.Name)]
public sealed class ReauthLedgerTests(PgCluster cluster) : DbTestBase(cluster)
{
    private static readonly DateTimeOffset Issued = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    private static NpgsqlParameter Scopes(string[] scopes) => new("scopes", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = scopes };

    private Task<string?> IssueFailsAsync(Guid asUser, byte[] hash, Guid session, string[] scopes, DateTimeOffset issued, DateTimeOffset expires, byte[]? mac)
        => FailsAsync(Role.App, asUser, "SELECT nina.reauth_issue(@h, @s, @scopes, @iat, @exp, @mac)",
            Bytes("h", hash), P("s", session), Scopes(scopes), P("iat", issued.UtcDateTime), P("exp", expires.UtcDateTime),
            new NpgsqlParameter("mac", NpgsqlDbType.Bytea) { Value = (object?)mac ?? DBNull.Value });

    private static byte[] ValidMac(Guid user, Guid session, byte[] hash, string[] scopes, DateTimeOffset issued, DateTimeOffset expires) =>
        ServerSigner.Sign("reauth.issue", ServerSigner.ReauthIssue(user, session, hash, scopes, issued, expires));

    // ------------------------------------------------------------------ a chave de assinatura (HMAC no banco)

    [Fact]
    public async Task The_database_hmac_matches_rfc4231_and_the_dotnet_implementation()
    {
        await using var s = await OpenAsync(Role.Owner);
        Assert.Equal("b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7",
            await s.ScalarAsync<string>("SELECT encode(nina.hmac_sha256(decode(repeat('0b', 20), 'hex'), convert_to('Hi There', 'UTF8')), 'hex')"));
        Assert.Equal("5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843",
            await s.ScalarAsync<string>("SELECT encode(nina.hmac_sha256(convert_to('Jefe', 'UTF8'), convert_to('what do ya want for nothing?', 'UTF8')), 'hex')"));
        Assert.Equal("60e431591ee0b67f0d8a26aacbf5b77f8e0bc6213728c5140546040f0ee37f54",                    // chave maior que o bloco (131 bytes)
            await s.ScalarAsync<string>("SELECT encode(nina.hmac_sha256(decode(repeat('aa', 131), 'hex'), convert_to('Test Using Larger Than Block-Size Key - Hash Key First', 'UTF8')), 'hex')"));

        var expected = ServerSigner.Hex(ServerSigner.Sign("p", "mensagem|com|separadores"));
        Assert.Equal(expected, await s.ScalarAsync<string>(
            "SELECT encode(nina.hmac_sha256((SELECT key FROM nina.server_key), convert_to('p|mensagem|com|separadores', 'UTF8')), 'hex')"));
    }

    [Fact]
    public async Task The_server_key_and_the_mac_primitives_are_invisible_and_unexecutable_for_every_application_role()
    {
        foreach (var role in new[] { Role.App, Role.Worker, Role.Config })
        {
            var user = role == Role.Worker ? (Guid?)null : W.Alice;
            Assert.Equal("42501", await FailsAsync(role, user, "SELECT key FROM nina.server_key"));
            Assert.Equal("42501", await FailsAsync(role, user, "SELECT nina.provision_server_key(decode(repeat('00', 32), 'hex'))"));
            Assert.Equal("42501", await FailsAsync(role, user, "SELECT nina.mac_ok('reauth.issue', 'x', decode(repeat('00', 32), 'hex'))"));
            Assert.Equal("42501", await FailsAsync(role, user, "SELECT nina.hmac_sha256(decode('00', 'hex'), decode('00', 'hex'))"));
            Assert.Equal("42501", await FailsAsync(role, user, "SELECT nina.apply_role_limits('postgres', 'nina_app')"));
        }
    }

    [Fact]
    public async Task Without_a_provisioned_key_every_signed_function_fails_closed()
    {
        var session = await SessionAsync(W.Alice);
        await OwnerAsync("DELETE FROM nina.server_key");
        var hash = Hash("sem-chave");
        var expires = Issued + TimeSpan.FromMinutes(5);
        Assert.Equal("NN070", await IssueFailsAsync(W.Alice, hash, session, ["ACCOUNT_DELETE"], Issued, expires, ValidMac(W.Alice, session, hash, ["ACCOUNT_DELETE"], Issued, expires)));
        Assert.Equal("NN070", await FailsAsync(Role.App, W.Alice, "SELECT nina.email_code_issue(@h, now(), now() + interval '5 minutes', NULL, @m)", Bytes("h", hash), Bytes("m", hash)));
    }

    // ------------------------------------------------------------------ NR-03: jti inventado nao vale

    [Fact]
    public async Task NR03_a_forged_jti_is_refused_by_the_ledger_and_by_every_sensitive_function()
    {
        // O ataque do reteste: Alice, sem senha nem token, "consome" um jti inventado e usa o hash em funcoes sensiveis.
        var session = await SessionAsync(W.Alice);
        var forged = Hash("forged");
        await using (var alice = await AsApp(W.Alice))
        {
            Assert.False(await alice.ScalarAsync<bool>("SELECT nina.consume_reauth_jti(@h, 'OWNERSHIP_TRANSFER', @s)", Bytes("h", forged), P("s", session)));
            Assert.False(await alice.ScalarAsync<bool>("SELECT nina.consume_reauth_jti(@h, 'OWNERSHIP_TRANSFER', @s)", Bytes("h", forged), P("s", Guid.NewGuid())));
        }

        Assert.Equal("NN014", await FailsAsync(Role.App, W.Alice, "SELECT nina.transfer_ownership(@b, @m, @h)", P("b", W.BabyA), P("m", W.MembershipErin), Bytes("h", forged)));
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Alice, "SELECT nina.request_account_deletion(@h)", Bytes("h", forged)));
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Carol, "SELECT nina.open_privacy_request('ANONYMIZATION', 'REAUTHENTICATION', @h)", Bytes("h", forged)));
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Alice, "SELECT nina.delete_baby(@b, @h, true)", P("b", W.BabyA), Bytes("h", forged)));
        await using (var alice = await AsApp(W.Alice))
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => EmailChangeCreateAsync(alice, W.Alice, "novo@example.org", Hash("code"), forged));
            Assert.Equal("NN014", ex.SqlState);
        }

        Assert.Equal(W.Alice, await OwnerScalarAsync<Guid>($"SELECT user_id FROM nina.caregiver_membership WHERE baby_id = '{W.BabyA}' AND role = 'OWNER' AND status = 'ACTIVE'"));
        Assert.Equal(0, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.account_deletion_request"));
        Assert.Equal(0, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.privacy_request"));
        Assert.Equal(0, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.reauth_jti"));
        Assert.Null(await OwnerScalarAsync<DateTime?>($"SELECT deleted_at FROM nina.baby WHERE id = '{W.BabyA}'"));
    }

    [Fact]
    public async Task NR03_the_app_cannot_write_the_ledger_directly_nor_read_it()
    {
        var session = await SessionAsync(W.Alice);
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice,
            $"INSERT INTO nina.reauth_jti (jti_hash, user_id, session_id, issued_at, expires_at) VALUES (decode(repeat('0a', 32), 'hex'), '{W.Alice}', '{session}', now(), now() + interval '1 minute')"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, "UPDATE nina.reauth_jti SET consumed_at = now()"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, "DELETE FROM nina.reauth_jti"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, "SELECT count(*) FROM nina.reauth_jti"));
        Assert.Equal("42501", await FailsAsync(Role.Worker, null, "SELECT count(*) FROM nina.reauth_jti"));
    }

    [Fact]
    public async Task NR03_issuing_requires_the_servers_mac_over_user_session_jti_scopes_and_validity()
    {
        var session = await SessionAsync(W.Alice);
        var otherSession = await SessionAsync(W.Bob);
        var hash = Hash("emitido");
        var expires = Issued + TimeSpan.FromMinutes(5);
        string[] scopes = ["ACCOUNT_DELETE"];
        var mac = ValidMac(W.Alice, session, hash, scopes, Issued, expires);

        Assert.Equal("NN071", await IssueFailsAsync(W.Alice, hash, session, scopes, Issued, expires, null));                          // sem MAC
        Assert.Equal("NN071", await IssueFailsAsync(W.Alice, hash, session, scopes, Issued, expires, new byte[32]));                 // MAC qualquer
        Assert.Equal("NN071", await IssueFailsAsync(W.Alice, hash, session, scopes, Issued, expires,
            ServerSigner.Sign("reauth.issue", ServerSigner.ReauthIssue(W.Alice, session, hash, scopes, Issued, expires), Hash("outra-chave"))));   // chave errada
        Assert.Equal("NN071", await IssueFailsAsync(W.Alice, hash, session, scopes, Issued, expires,
            ServerSigner.Sign("email.code", ServerSigner.ReauthIssue(W.Alice, session, hash, scopes, Issued, expires))));              // MAC de outro proposito
        // cada campo e coberto pelo MAC: trocar qualquer um invalida
        Assert.Equal("NN071", await IssueFailsAsync(W.Alice, Hash("outro-jti"), session, scopes, Issued, expires, mac));
        Assert.Equal("NN071", await IssueFailsAsync(W.Alice, hash, session, ["OWNERSHIP_TRANSFER"], Issued, expires, mac));
        Assert.Equal("NN071", await IssueFailsAsync(W.Alice, hash, session, scopes, Issued, expires.AddSeconds(30), mac));
        Assert.Equal("NN071", await IssueFailsAsync(W.Alice, hash, session, scopes, Issued.AddSeconds(1), expires, mac));
        Assert.Equal("NN071", await IssueFailsAsync(W.Alice, hash, otherSession, scopes, Issued, expires, mac));
        Assert.Equal("NN071", await IssueFailsAsync(W.Dave, hash, session, scopes, Issued, expires, mac));                             // MAC do usuario Alice nao serve a outro contexto

        // MAC valido, mas a sessao e de OUTRO usuario / revogada => recusado
        var bobMac = ValidMac(W.Alice, otherSession, hash, scopes, Issued, expires);
        Assert.Equal("NN071", await IssueFailsAsync(W.Alice, hash, otherSession, scopes, Issued, expires, bobMac));
        await OwnerAsync($"UPDATE nina.auth_session SET revoked_at = now(), revoked_reason = 'LOGOUT' WHERE id = '{session}'");
        Assert.Equal("NN071", await IssueFailsAsync(W.Alice, hash, session, scopes, Issued, expires, mac));
        Assert.Equal(0, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.reauth_jti"));
    }

    [Fact]
    public async Task NR03_a_signed_issuance_has_a_lifetime_of_at_most_five_minutes_and_a_jti_is_issued_once()
    {
        var session = await SessionAsync(W.Alice);
        var hash = Hash("longo");
        string[] scopes = ["IDENTITY_LINK"];
        var tooLong = Issued + TimeSpan.FromMinutes(6);
        Assert.Equal("23514", await IssueFailsAsync(W.Alice, hash, session, scopes, Issued, tooLong, ValidMac(W.Alice, session, hash, scopes, Issued, tooLong)));      // assinado, mas > 5 min
        var inverted = Issued - TimeSpan.FromSeconds(1);
        Assert.Equal("23514", await IssueFailsAsync(W.Alice, hash, session, scopes, Issued, inverted, ValidMac(W.Alice, session, hash, scopes, Issued, inverted)));
        Assert.Equal("23514", await IssueFailsAsync(W.Alice, hash, session, ["QUALQUER"], Issued, Issued + TimeSpan.FromMinutes(5), ValidMac(W.Alice, session, hash, ["QUALQUER"], Issued, Issued + TimeSpan.FromMinutes(5))));

        var issued = await IssueReauthAsync(W.Alice, session, ["IDENTITY_LINK"], "uma-vez");
        var expires = Issued + TimeSpan.FromMinutes(5);
        // reemitir o MESMO jti (mesmo com MAC valido) e recusado: 23505
        await OwnerAsync($"UPDATE nina.reauth_jti SET issued_at = '{Issued.UtcDateTime:O}', expires_at = '{expires.UtcDateTime:O}' WHERE jti_hash = decode('{ServerSigner.Hex(issued)}', 'hex')");
        Assert.Equal("23505", await IssueFailsAsync(W.Alice, issued, session, ["IDENTITY_LINK"], Issued, expires, ValidMac(W.Alice, session, issued, ["IDENTITY_LINK"], Issued, expires)));
    }

    // ------------------------------------------------------------------ consumo e vinculo

    [Fact]
    public async Task The_issued_jti_is_consumed_only_by_its_user_session_and_scope_and_only_once()
    {
        var session = await SessionAsync(W.Alice);
        var other = await SessionAsync(W.Alice);
        var hash = await IssueReauthAsync(W.Alice, session, ["ACCOUNT_DELETE", "PRIVACY_REQUEST"]);

        await using (var bob = await AsApp(W.Bob))
        {
            Assert.False(await bob.ScalarAsync<bool>("SELECT nina.consume_reauth_jti(@h, 'ACCOUNT_DELETE', @s)", Bytes("h", hash), P("s", session)));       // outro usuario
        }

        await using (var alice = await AsApp(W.Alice))
        {
            Assert.False(await alice.ScalarAsync<bool>("SELECT nina.consume_reauth_jti(@h, 'ACCOUNT_DELETE', @s)", Bytes("h", hash), P("s", other)));          // outra sessao
            Assert.False(await alice.ScalarAsync<bool>("SELECT nina.consume_reauth_jti(@h, 'OWNERSHIP_TRANSFER', @s)", Bytes("h", hash), P("s", session)));   // escopo fora da emissao
            Assert.False(await alice.ScalarAsync<bool>("SELECT nina.consume_reauth_jti(@h, 'NAO_EXISTE', @s)", Bytes("h", hash), P("s", session)));
            Assert.True(await alice.ScalarAsync<bool>("SELECT nina.consume_reauth_jti(@h, 'PRIVACY_REQUEST', @s)", Bytes("h", hash), P("s", session)));
            Assert.False(await alice.ScalarAsync<bool>("SELECT nina.consume_reauth_jti(@h, 'ACCOUNT_DELETE', @s)", Bytes("h", hash), P("s", session)));      // uso unico
            await alice.CommitAsync();
        }

        Assert.Equal("PRIVACY_REQUEST", await OwnerScalarAsync<string>("SELECT consumed_scope FROM nina.reauth_jti"));
    }

    [Fact]
    public async Task A_token_without_scopes_serves_exactly_one_sensitive_operation_but_only_if_it_was_issued()
    {
        var session = await SessionAsync(W.Alice);
        var hash = await IssueReauthAsync(W.Alice, session, []);
        await using var alice = await AsApp(W.Alice);
        Assert.True(await alice.ScalarAsync<bool>("SELECT nina.consume_reauth_jti(@h, 'DATA_EXPORT_REQUEST', @s)", Bytes("h", hash), P("s", session)));
        Assert.False(await alice.ScalarAsync<bool>("SELECT nina.consume_reauth_jti(@h, 'ACCOUNT_DELETE', @s)", Bytes("h", hash), P("s", session)));
    }

    [Fact]
    public async Task An_expired_issuance_cannot_be_consumed()
    {
        var session = await SessionAsync(W.Alice);
        var hash = Hash("velho");
        var issued = Issued - TimeSpan.FromMinutes(30);
        var expires = issued + TimeSpan.FromMinutes(5);
        string[] scopes = ["ACCOUNT_DELETE"];
        await using (var alice = await AsApp(W.Alice))
        {
            await alice.ExecAsync("SELECT nina.reauth_issue(@h, @s, @scopes, @iat, @exp, @mac)", Bytes("h", hash), P("s", session), Scopes(scopes),
                P("iat", issued.UtcDateTime), P("exp", expires.UtcDateTime), Bytes("mac", ValidMac(W.Alice, session, hash, scopes, issued, expires)));
            Assert.False(await alice.ScalarAsync<bool>("SELECT nina.consume_reauth_jti(@h, 'ACCOUNT_DELETE', @s)", Bytes("h", hash), P("s", session)));
        }
    }

    [Fact]
    public async Task Binding_requires_a_consumed_jti_in_the_same_scope_and_binds_it_to_one_resource_only()
    {
        var session = await SessionAsync(W.Alice);
        // emitido mas NAO consumido: nao serve de prova
        var notConsumed = await IssueReauthAsync(W.Alice, session, ["OWNERSHIP_TRANSFER"]);
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Alice, "SELECT nina.transfer_ownership(@b, @m, @h)", P("b", W.BabyA), P("m", W.MembershipErin), Bytes("h", notConsumed)));
        // consumido em OUTRO escopo: nao serve
        var wrongScope = await ReauthAsync(W.Alice, "ACCOUNT_DELETE");
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Alice, "SELECT nina.transfer_ownership(@b, @m, @h)", P("b", W.BabyA), P("m", W.MembershipErin), Bytes("h", wrongScope)));
        // consumido no escopo certo: serve, uma unica vez
        var proof = await ReauthAsync(W.Alice, "OWNERSHIP_TRANSFER");
        await using (var alice = await AsApp(W.Alice))
        {
            await alice.ExecAsync("SELECT nina.transfer_ownership(@b, @m, @h)", P("b", W.BabyA), P("m", W.MembershipErin), Bytes("h", proof));
            await alice.CommitAsync();
        }

        Assert.Equal("BABY", await OwnerScalarAsync<string>($"SELECT bound_entity_type FROM nina.reauth_jti WHERE jti_hash = decode('{ServerSigner.Hex(proof)}', 'hex')"));
        Assert.Equal("NN014", await FailsAsync(Role.App, W.Erin, "SELECT nina.transfer_ownership(@b, @m, @h)", P("b", W.BabyA), P("m", W.MembershipAlice), Bytes("h", proof)));   // Erin e a Owner agora, mas o jti e da Alice e ja esta vinculado
        var again = await ReauthAsync(W.Erin, "OWNERSHIP_TRANSFER");
        await using var erin = await AsApp(W.Erin);
        await erin.ExecAsync("SELECT nina.transfer_ownership(@b, @m, @h)", P("b", W.BabyA), P("m", W.MembershipAlice), Bytes("h", again));
    }
}
