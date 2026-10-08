using Nina.Database.Tests.Infrastructure;

namespace Nina.Database.Tests.Tests;

/// <summary>
/// Anexo B 1, 2 e 6 do SECURITY-REVIEW-001 (SR-001/003/012): RLS em todas as tabelas, matriz papel x tabela x operação (e papel x função)
/// contra um snapshot aprovado, atributos dos papéis e privilégios de PUBLIC. Qualquer mudança de privilégio exige atualizar o snapshot
/// de propósito (revisão de segurança), não por acidente.
/// </summary>
[Collection(PgClusterGroup.Name)]
public sealed class CatalogTests(PgCluster cluster) : DbTestBase(cluster)
{
    /// <summary>Catálogos de referência sem dado de tenant: o app só lê, a escrita é do nina_config_admin (auditada). Única exceção à RLS.</summary>
    private static readonly string[] RlsExceptions = ["app_parameter", "consent_purpose", "feature_flag", "plan", "plan_feature"];

    private const string TableMatrixSql =
        """
        WITH r(role) AS (VALUES ('nina_app'), ('nina_worker'), ('nina_config_admin')),
        t AS (SELECT c.oid, c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
               WHERE n.nspname = 'nina' AND c.relkind IN ('r', 'v')),
        tp AS (
          SELECT r.role, t.relname, string_agg(p, ',' ORDER BY p) AS privs
            FROM r CROSS JOIN t CROSS JOIN LATERAL unnest(ARRAY['SELECT','INSERT','UPDATE','DELETE','TRUNCATE','REFERENCES','TRIGGER']) p
           WHERE has_table_privilege(r.role, t.oid, p)
           GROUP BY r.role, t.relname),
        cp AS (
          SELECT r.role, t.relname, a.attname, p
            FROM r CROSS JOIN t JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum > 0 AND NOT a.attisdropped
                 CROSS JOIN LATERAL unnest(ARRAY['SELECT','INSERT','UPDATE']) p
           WHERE has_column_privilege(r.role, t.oid, a.attnum, p) AND NOT has_table_privilege(r.role, t.oid, p)),
        cpa AS (SELECT role, relname, p, string_agg(attname, ',' ORDER BY attname) cols FROM cp GROUP BY role, relname, p)
        SELECT role || '|' || relname || '|' || privs FROM tp
        UNION ALL SELECT role || '|' || relname || '|' || p || '(' || cols || ')' FROM cpa
        """;

    private const string FunctionMatrixSql =
        """
        SELECT r.role || '|' || p.oid::regprocedure::text
          FROM (VALUES ('nina_app'), ('nina_worker'), ('nina_config_admin'), ('public')) r(role)
          JOIN pg_proc p ON p.pronamespace = 'nina'::regnamespace
         WHERE has_function_privilege(r.role, p.oid, 'EXECUTE')
        """;

    [Fact]
    public async Task Every_table_has_row_level_security_except_the_approved_reference_catalogs()
    {
        await using var s = await OpenAsync(Role.Owner);
        var rows = await s.RowsAsync(
            """
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'nina' AND c.relkind IN ('r', 'p') AND NOT c.relrowsecurity ORDER BY 1
            """);

        Assert.Equal(RlsExceptions, rows.Select(r => (string)r[0]!).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Reference_catalogs_exempt_from_rls_carry_no_tenant_data_and_the_app_can_only_read_them()
    {
        await using var s = await OpenAsync(Role.Owner);
        foreach (var table in RlsExceptions)
        {
            Assert.False(await s.ScalarAsync<bool>($"SELECT has_table_privilege('nina_app', 'nina.{table}', 'INSERT,UPDATE,DELETE')"), table);
            Assert.True(await s.ScalarAsync<bool>($"SELECT has_table_privilege('nina_app', 'nina.{table}', 'SELECT')"), table);
        }
    }

    [Fact]
    public async Task Table_and_column_privilege_matrix_matches_the_approved_snapshot()
    {
        await AssertSnapshotAsync("privilege_matrix.txt", TableMatrixSql);
    }

    [Fact]
    public async Task Function_execute_matrix_matches_the_approved_snapshot_and_public_executes_nothing()
    {
        await AssertSnapshotAsync("function_execute_matrix.txt", FunctionMatrixSql);
        await using var s = await OpenAsync(Role.Owner);
        Assert.Equal(0, await s.ScalarAsync<long>(
            "SELECT count(*) FROM pg_proc p WHERE p.pronamespace = 'nina'::regnamespace AND has_function_privilege('public', p.oid, 'EXECUTE')"));
        Assert.Equal(0, await s.ScalarAsync<long>(
            "SELECT count(*) FROM pg_class c WHERE c.relnamespace = 'nina'::regnamespace AND c.relkind IN ('r','v') AND has_table_privilege('public', c.oid, 'SELECT,INSERT,UPDATE,DELETE')"));
    }

    [Fact]
    public async Task Application_roles_are_never_privileged_and_no_login_holds_two_of_them()
    {
        await using var s = await OpenAsync(Role.Owner);
        var attrs = await s.RowsAsync(
            "SELECT rolname, rolsuper, rolbypassrls, rolcreatedb, rolcreaterole, rolreplication FROM pg_roles WHERE rolname IN ('nina_app', 'nina_worker', 'nina_config_admin') ORDER BY 1");
        Assert.Equal(3, attrs.Count);
        Assert.All(attrs, a => Assert.True(a.Skip(1).All(v => v is false), (string)a[0]!));

        Assert.Equal(0, await s.ScalarAsync<long>(LoginsWithTwoRolesSql));

        foreach (var login in new[] { PgCluster.AppLogin, PgCluster.WorkerLogin, PgCluster.ConfigLogin })
        {
            Assert.False(await s.ScalarAsync<bool>($"SELECT rolsuper OR rolbypassrls FROM pg_roles WHERE rolname = '{login}'"), login);
        }
    }

    private const string LoginsWithTwoRolesSql =
        """
        SELECT count(*) FROM (
          SELECT m.member FROM pg_auth_members m JOIN pg_roles g ON g.oid = m.roleid
           WHERE g.rolname IN ('nina_app', 'nina_worker', 'nina_config_admin')
           GROUP BY m.member HAVING count(DISTINCT g.rolname) > 1) x
        """;

    [Fact]
    public async Task X2_the_detector_flags_a_login_that_would_hold_two_roles_and_see_every_tenant()
    {
        // Prova de que o teste acima nao e vacuo: um login em nina_app + nina_worker soma as politicas e le TODOS os bebes sem contexto (X2).
        await OwnerAsync("CREATE ROLE t_dual_login LOGIN IN ROLE nina_app, nina_worker");
        try
        {
            await using var s = await OpenAsync(Role.Owner);
            Assert.Equal(1, await s.ScalarAsync<long>(LoginsWithTwoRolesSql));
            await using var dual = await Session.OpenAsync(Cluster.ConnectionString(Role.Owner, DatabaseName).Replace("Username=postgres", "Username=t_dual_login", StringComparison.Ordinal), null);
            Assert.Equal(2, await dual.ScalarAsync<long>("SELECT count(*) FROM nina.baby"));
        }
        finally
        {
            await OwnerAsync("DROP ROLE IF EXISTS t_dual_login");
        }
    }

    [Fact]
    public async Task The_application_roles_own_nothing_and_cannot_create_objects_or_temp_tables()
    {
        await using var s = await OpenAsync(Role.Owner);
        Assert.Equal(0, await s.ScalarAsync<long>(
            "SELECT count(*) FROM pg_class c JOIN pg_roles o ON o.oid = c.relowner WHERE c.relnamespace = 'nina'::regnamespace AND o.rolname LIKE 'nina\\_%'"));
        Assert.False(await s.ScalarAsync<bool>($"SELECT has_schema_privilege('{PgCluster.AppLogin}', 'nina', 'CREATE')"));
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, "CREATE TABLE nina.x (a int)"));

        // O REVOKE TEMP de PUBLIC e executado pela migracao no banco em que ela roda (aqui, o modelo; um CREATE DATABASE nao herda o ACL).
        Assert.False(await s.ScalarAsync<bool>($"SELECT has_database_privilege('{PgCluster.AppLogin}', 'nina_template', 'TEMP')"));
        await using var app = await Session.OpenAsync(Cluster.ConnectionString(Role.App, "nina_template"), W.Alice);
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => app.ExecAsync("CREATE TEMP TABLE x (a int)"));
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task X1_a_function_created_after_the_migration_is_not_executable_by_the_application()
    {
        await OwnerAsync("CREATE FUNCTION nina.zz_future() RETURNS int LANGUAGE sql AS 'SELECT 1'");

        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, "SELECT nina.zz_future()"));
        Assert.Equal("42501", await FailsAsync(Role.Worker, null, "SELECT nina.zz_future()"));
        await OwnerAsync("CREATE TABLE nina.zz_future_table (a int)");
        Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, "SELECT * FROM nina.zz_future_table"));
    }

    [Fact]
    public async Task Every_security_definer_function_pins_its_search_path()
    {
        await using var s = await OpenAsync(Role.Owner);
        var unpinned = await s.RowsAsync(
            """
            SELECT p.oid::regprocedure::text FROM pg_proc p
             WHERE p.pronamespace = 'nina'::regnamespace AND p.prosecdef
               AND NOT EXISTS (SELECT 1 FROM unnest(coalesce(p.proconfig, ARRAY[]::text[])) c WHERE c LIKE 'search_path=%')
            """);
        Assert.Empty(unpinned);
        // e as definer nao podem ser alteradas por quem so executa: o dono das funcoes nao e nenhum papel nina_*
        Assert.Equal(0, await s.ScalarAsync<long>(
            "SELECT count(*) FROM pg_proc p JOIN pg_roles r ON r.oid = p.proowner WHERE p.pronamespace = 'nina'::regnamespace AND r.rolname LIKE 'nina\\_%'"));
    }

    [Fact]
    public async Task Triggers_and_guards_do_not_need_execute_grants_for_the_application_roles()
    {
        // As funcoes de guarda/ficha so podem ser chamadas pelo dono: um papel de aplicacao nao emite fichas (SR-008).
        foreach (var call in new[] { "nina.guard_arm('membership')", "nina.guard_token('membership')", "nina.guard_ok('membership')", "nina.guard_disarm('membership')" })
        {
            Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, $"SELECT {call}"));
            Assert.Equal("42501", await FailsAsync(Role.Worker, null, $"SELECT {call}"));
            Assert.Equal("42501", await FailsAsync(Role.Config, W.Alice, $"SELECT {call}"));
        }
    }

    [Fact]
    public async Task The_guard_secret_and_security_tables_are_invisible_to_every_application_role()
    {
        foreach (var table in new[] { "guard_secret", "audit_action", "audit_chain_checkpoint", "erasure_ledger", "baby_sync_head", "reauth_jti", "email_verification_code", "email_change_request" })
        {
            Assert.Equal("42501", await FailsAsync(Role.App, W.Alice, $"SELECT * FROM nina.{table}"));
            Assert.Equal("42501", await FailsAsync(Role.Config, W.Alice, $"SELECT * FROM nina.{table}"));
        }

        Assert.Equal("42501", await FailsAsync(Role.Worker, null, "SELECT * FROM nina.guard_secret"));
        Assert.Equal("42501", await FailsAsync(Role.Worker, null, "SELECT * FROM nina.user_credential"));
        Assert.Equal("42501", await FailsAsync(Role.Worker, null, "SELECT * FROM nina.refresh_token"));
        Assert.Equal("42501", await FailsAsync(Role.Worker, null, "SELECT * FROM nina.recovery_request"));
    }

    private async Task AssertSnapshotAsync(string file, string sql)
    {
        var expected = (await File.ReadAllLinesAsync(Path.Combine(AppContext.BaseDirectory, "Snapshots", file)))
            .Where(l => l.Length > 0 && !l.StartsWith('#')).Order(StringComparer.Ordinal).ToArray();
        await using var s = await OpenAsync(Role.Owner);
        var actual = (await s.RowsAsync(sql)).Select(r => (string)r[0]!).Order(StringComparer.Ordinal).ToArray();

        var missing = expected.Except(actual).ToArray();
        var extra = actual.Except(expected).ToArray();
        Assert.True(
            missing.Length == 0 && extra.Length == 0,
            $"{file} divergiu do snapshot aprovado.{Environment.NewLine}Removidos: {string.Join(" ; ", missing)}{Environment.NewLine}Novos: {string.Join(" ; ", extra)}");
    }
}
