using System.Diagnostics;
using Nina.Database.Tests.Infrastructure;
using Npgsql;

namespace Nina.Database.Tests.Tests;

/// <summary>
/// NR-05 e NR-13 (reteste): nenhum gatilho SECURITY DEFINER pode produzir, antes do WITH CHECK da RLS, um resultado observável que dependa de dados
/// de OUTRO tenant. Um INSERT com <c>baby_id</c> alheio (ou inexistente) devolve SEMPRE o mesmo erro da política (42501), sem tocar nos contadores
/// nem esperar locks do bebê. Também cobre os oráculos residuais: <c>baby_ever_had_owner</c>, convite por <c>user_id</c> e <c>p_now</c> retroativo.
/// </summary>
[Collection(PgClusterGroup.Name)]
public sealed class CrossTenantObservabilityTests(PgCluster cluster) : DbTestBase(cluster)
{
    private static string Sleep(Guid baby, string start, string? end) =>
        $"INSERT INTO nina.sleep_session (id, baby_id, start_at, end_at, tz, sleep_type, source) VALUES (gen_random_uuid(), '{baby}', {start}, {(end ?? "NULL")}, 'UTC', 'NAP', 'MANUAL')";

    private static string Wake(Guid baby, Guid session) =>
        $"INSERT INTO nina.wake_event (id, baby_id, sleep_session_id, started_at, ended_at, tz, source) VALUES (gen_random_uuid(), '{baby}', '{session}', now() - interval '150 minutes', now() - interval '140 minutes', 'UTC', 'MANUAL')";

    private async Task RejectOverlapsAsync()
    {
        await using var admin = await OpenAsync(Role.Config, W.Alice);
        await admin.ExecAsync("UPDATE nina.app_parameter SET value = '\"REJECT\"'::jsonb WHERE param_key = 'sleep.overlap_policy'");
        await admin.CommitAsync();
    }

    [Fact]
    public async Task NR05_with_the_reject_policy_a_foreign_baby_never_reveals_overlap_a_deleted_baby_or_a_wake_session()
    {
        await RejectOverlapsAsync();
        // Bob dorme de now()-3h a now()-2h (SleepB). Dave, sem vinculo algum, sonda o bebe do Bob.
        var overlapping = Sleep(W.BabyB, "now() - interval '170 minutes'", "now() - interval '150 minutes'");
        var free = Sleep(W.BabyB, "now() - interval '20 hours'", "now() - interval '19 hours'");
        var nonexistent = Sleep(Guid.NewGuid(), "now() - interval '170 minutes'", "now() - interval '150 minutes'");
        foreach (var sql in new[] { overlapping, free, nonexistent, Sleep(W.BabyB, "now()", null) })
        {
            Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, sql));
        }

        // despertar: sessao existente do Bob, inexistente e de bebe inexistente => todos 42501 (nao NN002)
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, Wake(W.BabyB, W.SleepB)));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, Wake(W.BabyB, Guid.NewGuid())));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, Wake(Guid.NewGuid(), Guid.NewGuid())));
        // leitor (Carol) do bebe A tambem nao escreve: mesmo erro, nunca SLEEP_OVERLAP
        await OwnerAsync($"INSERT INTO nina.sleep_session (id, baby_id, start_at, end_at, tz, sleep_type, source) VALUES (gen_random_uuid(), '{W.BabyA}', now() - interval '4 hours', now() - interval '3 hours', 'UTC', 'NAP', 'MANUAL')");
        Assert.Equal("42501", await FailsAsync(Role.App, W.Carol, Sleep(W.BabyA, "now() - interval '230 minutes'", "now() - interval '200 minutes'")));
        // os dados do proprio tenant seguem com os erros de negocio (o dono do bebe ve o SLEEP_OVERLAP)
        Assert.Equal("NN006", await FailsAsync(Role.App, W.Bob, overlapping));
    }

    [Fact]
    public async Task NR05_a_deleted_baby_is_not_revealed_by_an_insert_from_outside()
    {
        var proof = await ReauthAsync(W.Bob, "BABY_DELETE");
        await using (var bob = await AsApp(W.Bob))
        {
            await bob.ExecAsync("SELECT nina.delete_baby(@b, @h)", P("b", W.BabyB), Bytes("h", proof));
            await bob.CommitAsync();
        }

        // antes era NN003 ("bebe excluido") para quem nao devia saber; agora e a negativa da RLS, igual a bebe inexistente
        var sql = Sleep(W.BabyB, "now() - interval '5 hours'", "now() - interval '4 hours'");
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, sql));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Bob, sql));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, Sleep(Guid.NewGuid(), "now() - interval '5 hours'", "now() - interval '4 hours'")));
    }

    [Fact]
    public async Task NR05_a_foreign_insert_neither_waits_for_nor_consumes_the_victims_sync_counter()
    {
        // Bob esta no meio de uma escrita (segura o lock de linha do contador do bebe dele)
        await using var bob = await AsApp(W.Bob);
        await bob.ExecAsync(Sleep(W.BabyB, "now() - interval '9 hours'", "now() - interval '8 hours'"));
        var before = await OwnerScalarAsync<long>($"SELECT last_sequence FROM nina.baby_sync_head WHERE baby_id = '{W.BabyB}'");

        var clock = Stopwatch.StartNew();
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, Sleep(W.BabyB, "now() - interval '7 hours'", "now() - interval '6 hours'")));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"o INSERT alheio esperou o lock do bebe do Bob ({clock.Elapsed}): oraculo de tempo/lock");
        await bob.CommitAsync();
        Assert.Equal(before, await OwnerScalarAsync<long>($"SELECT last_sequence FROM nina.baby_sync_head WHERE baby_id = '{W.BabyB}'"));
    }

    [Fact]
    public async Task NR05_the_daily_invite_counter_of_another_owner_is_not_observable()
    {
        await using (var admin = await OpenAsync(Role.Config, W.Alice))
        {
            await admin.ExecAsync("UPDATE nina.app_parameter SET value = '1'::jsonb WHERE param_key = 'invites.max_per_day'");
            await admin.CommitAsync();
        }

        string Invite(Guid baby, Guid invitedBy, string email) =>
            $"INSERT INTO nina.caregiver_membership (baby_id, invited_email, role, status, invited_by, invite_token_hash, invite_expires_at) VALUES ('{baby}', '{email}', 'CAREGIVER', 'PENDING', '{invitedBy}', decode(md5('{email}') || md5('{email}x'), 'hex'), now() + interval '2 days')";
        await using (var alice = await AsApp(W.Alice))
        {
            await alice.ExecAsync(Invite(W.BabyA, W.Alice, "um@example.org"));
            await alice.CommitAsync();
        }

        Assert.Equal("NN057", await FailsAsync(Role.App, W.Alice, Invite(W.BabyA, W.Alice, "dois@example.org")));            // o proprio Owner ve o limite
        // Dave tenta convidar "em nome da Alice": a RLS-equivalente nega antes, sem revelar que ela atingiu o limite
        Assert.Equal("42501", await FailsAsync(Role.App, W.Dave, Invite(W.BabyA, W.Alice, "tres@example.org")));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Bob, Invite(W.BabyB, W.Alice, "quatro@example.org")));
    }

    // ------------------------------------------------------------------ NR-13

    [Fact]
    public async Task NR13_baby_ever_had_owner_is_no_oracle_for_the_existence_of_a_foreign_baby()
    {
        await using var dave = await AsApp(W.Dave);
        Assert.False(await dave.ScalarAsync<bool>("SELECT nina.baby_ever_had_owner(@b)", P("b", W.BabyB)));                 // existe, tem Owner, mas nao e dele
        Assert.False(await dave.ScalarAsync<bool>("SELECT nina.baby_ever_had_owner(@b)", P("b", Guid.NewGuid())));          // nao existe
        await using var bob = await AsApp(W.Bob);
        Assert.True(await bob.ScalarAsync<bool>("SELECT nina.baby_ever_had_owner(@b)", P("b", W.BabyB)));                   // o proprio Owner sabe
        await using var carol = await AsApp(W.Carol);
        Assert.True(await carol.ScalarAsync<bool>("SELECT nina.baby_ever_had_owner(@b)", P("b", W.BabyA)));                 // membro do bebe A
        Assert.False(await carol.ScalarAsync<bool>("SELECT nina.baby_ever_had_owner(@b)", P("b", W.BabyB)));
    }

    [Fact]
    public async Task NR13_an_owner_invites_only_by_email_never_by_the_user_id_of_an_existing_account()
    {
        string Invite(string columns, string values) =>
            $"INSERT INTO nina.caregiver_membership (baby_id, {columns}, role, status, invited_by, invite_token_hash, invite_expires_at) VALUES ('{W.BabyA}', {values}, 'CAREGIVER', 'PENDING', '{W.Alice}', decode(repeat('ab', 32), 'hex'), now() + interval '2 days')";
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, Invite("user_id", $"'{W.Dave}'")));                           // convite nao solicitado a conta existente
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, Invite("user_id", $"'{Guid.NewGuid()}'")));                    // mesmo erro para UUID inexistente (sem oraculo de FK)
        Assert.Null(await FailsAsync(Role.App, W.Alice, Invite("invited_email", "'novo@example.org'")));
    }

    [Fact]
    public async Task NR13_a_backdated_p_now_cannot_stretch_the_validity_of_an_expired_secret()
    {
        // token de recuperacao ja expirado ha 1 hora: com p_now de 2 horas atras (retroativo) a versao antiga o aceitaria
        await OwnerAsync($"INSERT INTO nina.recovery_request (user_id, token_hash, created_at, expires_at) VALUES ('{W.Alice}', decode(repeat('c1', 32), 'hex'), now() - interval '2 hours', now() - interval '1 hour')");
        var expiredHash = Convert.FromHexString(string.Concat(Enumerable.Repeat("c1", 32)));
        await using (var anon = await AsApp(null))
        {
            Assert.Null(await anon.ScalarAsync<Guid?>("SELECT nina.auth_consume_recovery(@h, now() - interval '90 minutes')", Bytes("h", expiredHash)));
            Assert.Null(await anon.ScalarAsync<Guid?>("SELECT nina.auth_consume_recovery(@h, now())", Bytes("h", expiredHash)));
        }

        // codigo de e-mail expirado, mesma prova
        var code = Hash("velho");
        await OwnerAsync(
            $"""
            UPDATE nina.app_user SET email_verified_at = NULL WHERE id = '{W.Alice}';
            INSERT INTO nina.email_verification_code (user_id, code_hash, created_at, expires_at) VALUES ('{W.Alice}', decode('{ServerSigner.Hex(code)}', 'hex'), now() - interval '2 hours', now() - interval '1 hour');
            """);
        await using (var anon = await AsApp(null))
        {
            Assert.Null(await anon.ScalarAsync<Guid?>("SELECT nina.email_code_verify('alice@example.org', @h, now() - interval '90 minutes')", Bytes("h", code)));
        }

        Assert.Null(await OwnerScalarAsync<DateTime?>($"SELECT email_verified_at FROM nina.app_user WHERE id = '{W.Alice}'"));
    }
}
