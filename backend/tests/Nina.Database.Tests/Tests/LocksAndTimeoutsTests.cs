using System.Diagnostics;
using Nina.Database.Tests.Infrastructure;
using Npgsql;

namespace Nina.Database.Tests.Tests;

/// <summary>
/// NR-04 (reteste): sem locks consultivos de chave previsível, com timeouts por papel e com purga que nunca falha em silêncio.
/// A cadeia de auditoria e as purgas serializam pelo lock de LINHA da tabela privada <c>nina.control_lock</c>, que nenhum papel de aplicação alcança.
/// </summary>
[Collection(PgClusterGroup.Name)]
public sealed class LocksAndTimeoutsTests(PgCluster cluster) : DbTestBase(cluster)
{
    private static readonly string[] AdvisoryNames =
        ["nina.audit_chain", "nina.purge_expired_operational_data", "nina.purge_expired_sync_data", "nina.purge_audit", "nina.purge_consent"];

    private async Task<PostgresException> RunFailureAsync(Role role, Guid? user, string sql)
    {
        await using var s = await OpenAsync(role, user);
        return await Assert.ThrowsAsync<PostgresException>(() => s.ExecAsync(sql));
    }

    [Fact]
    public async Task NR04_holding_the_old_predictable_advisory_locks_no_longer_blocks_the_audit_chain_or_the_purges()
    {
        // O ataque do reteste: um usuario qualquer segura pg_advisory_lock(hashtextextended('nina.audit_chain', 0)) e as chaves das purgas.
        await using var eve = await AsApp(W.Dave);
        foreach (var name in AdvisoryNames)
        {
            await eve.ExecAsync($"SELECT pg_advisory_lock(hashtextextended('{name}', 0))");
        }

        var clock = Stopwatch.StartNew();
        await using (var alice = await AsApp(W.Alice))
        {
            Assert.True(await alice.ScalarAsync<long>("SELECT nina.audit('auth.password_changed')") > 0);       // evento critico => passa pela cadeia
            await alice.CommitAsync();
        }

        await using (var worker = await OpenAsync(Role.Worker))
        {
            Assert.NotNull(await worker.ScalarAsync<string>("SELECT nina.purge_expired_operational_data(10)::text"));
            Assert.Single(await worker.RowsAsync("SELECT * FROM nina.purge_expired_sync_data(10)"));
            Assert.Equal(0L, await worker.ScalarAsync<long>("SELECT nina.purge_audit(interval '2 years', 10)"));
            Assert.Equal(0L, await worker.ScalarAsync<long>("SELECT nina.purge_consent(interval '6 years', 10)"));
            await worker.CommitAsync();
        }

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"as funcoes esperaram por advisory locks de terceiros ({clock.Elapsed})");
        Assert.True(await OwnerScalarAsync<bool>("SELECT ok FROM nina.verify_audit_chain()"));
    }

    [Fact]
    public async Task NR04_the_control_lock_table_is_unreachable_for_every_application_role()
    {
        foreach (var (role, user) in new[] { (Role.App, (Guid?)W.Alice), (Role.Worker, null), (Role.Config, W.Alice) })
        {
            Assert.Equal("42501", await FailsAsync(role, user, "SELECT * FROM nina.control_lock"));
            Assert.Equal("42501", await FailsAsync(role, user, "SELECT name FROM nina.control_lock WHERE name = 'audit_chain' FOR UPDATE"));
            Assert.Equal("42501", await FailsAsync(role, user, "UPDATE nina.control_lock SET name = name"));
            Assert.Equal("42501", await FailsAsync(role, user, "LOCK TABLE nina.control_lock IN ACCESS EXCLUSIVE MODE"));
            Assert.Equal("42501", await FailsAsync(role, user, "DELETE FROM nina.control_lock"));
        }
    }

    [Fact]
    public async Task NR04_a_purge_that_cannot_take_its_lock_fails_loudly_and_deletes_nothing()
    {
        // linhas expiradas que a purga operacional removeria
        await OwnerAsync($"INSERT INTO nina.recovery_request (user_id, token_hash, expires_at) VALUES ('{W.Alice}', decode(repeat('01', 32), 'hex'), now() - interval '8 days')");

        var cases = new (string Lock, string Sql)[]
        {
            ("purge_operational", "SELECT nina.purge_expired_operational_data(10)"),
            ("purge_sync", "SELECT * FROM nina.purge_expired_sync_data(10)"),
            ("purge_audit", "SELECT nina.purge_audit(interval '2 years', 10)"),
            ("purge_consent", "SELECT nina.purge_consent(interval '6 years', 10)"),
        };
        foreach (var (lockName, sql) in cases)
        {
            await using var holder = await OpenAsync(Role.Owner);            // outra execucao (ou travada) segurando o lock da purga
            await holder.ExecAsync($"SELECT 1 FROM nina.control_lock WHERE name = '{lockName}' FOR UPDATE");
            var ex = await RunFailureAsync(Role.Worker, null, sql);
            Assert.Equal("NN032", ex.SqlState);
            Assert.Contains("PURGE_LOCK_BUSY", ex.MessageText, StringComparison.Ordinal);
        }

        Assert.Equal(1, await OwnerScalarAsync<long>("SELECT count(*) FROM nina.recovery_request"));      // nada foi apagado

        // liberado o lock, a mesma purga roda e remove
        await using var worker = await OpenAsync(Role.Worker);
        Assert.Contains("\"recovery_request\": 1", await worker.ScalarAsync<string>("SELECT nina.purge_expired_operational_data(10)::text"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NR04_the_audit_chain_waits_only_up_to_lock_timeout_and_fails_with_55P03()
    {
        await using var holder = await OpenAsync(Role.Owner);
        await holder.ExecAsync("SELECT 1 FROM nina.control_lock WHERE name = 'audit_chain' FOR UPDATE");

        await using var alice = await AsApp(W.Alice);
        await alice.ExecAsync("SET LOCAL lock_timeout = '300ms'");
        // evento nao encadeado (login) nao depende do lock
        Assert.True(await alice.ScalarAsync<long>("SELECT nina.audit('auth.login')") > 0);
        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => alice.ScalarAsync<long>("SELECT nina.audit('auth.password_changed')"));
        Assert.Equal("55P03", ex.SqlState);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task NR04_each_application_role_has_statement_lock_and_idle_in_transaction_timeouts()
    {
        var expected = new Dictionary<Role, (int Statement, int Lock, int Idle)>
        {
            [Role.App] = (15_000, 5_000, 30_000),
            [Role.Worker] = (600_000, 30_000, 60_000),
            [Role.Config] = (30_000, 5_000, 30_000),
        };
        foreach (var (role, want) in expected)
        {
            await using var s = await OpenAsync(role);
            Assert.Equal(want.Statement, await s.ScalarAsync<int>("SELECT setting::int FROM pg_settings WHERE name = 'statement_timeout'"));
            Assert.Equal(want.Lock, await s.ScalarAsync<int>("SELECT setting::int FROM pg_settings WHERE name = 'lock_timeout'"));
            Assert.Equal(want.Idle, await s.ScalarAsync<int>("SELECT setting::int FROM pg_settings WHERE name = 'idle_in_transaction_session_timeout'"));
        }

        // a migracao grava os limites nos papeis nina_* (valem para o login que ENTRA como esse papel); o login membro os recebe por apply_role_limits
        await using var owner = await OpenAsync(Role.Owner);
        foreach (var group in new[] { "nina_app", "nina_worker", "nina_config_admin" })
        {
            var settings = (await owner.RowsAsync(
                "SELECT unnest(s.setconfig) FROM pg_db_role_setting s JOIN pg_roles r ON r.oid = s.setrole WHERE r.rolname = @g AND s.setdatabase = 0", P("g", group)))
                .Select(r => ((string)r[0]!).Split('=')[0]).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(["idle_in_transaction_session_timeout", "lock_timeout", "statement_timeout"], settings);
        }

        var ex = await Assert.ThrowsAsync<PostgresException>(() => owner.ExecAsync("SELECT nina.apply_role_limits('t_app_login', 'postgres')"));
        Assert.Equal("NN070", ex.SqlState);
    }
}
