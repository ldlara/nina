using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Nina.SharedKernel.Data;
using Npgsql;

namespace Nina.Database.Tests.Infrastructure;

/// <summary>Papéis de conexão dos testes. Os logins são membros de UM papel <c>nina_*</c> cada (nunca superuser, nunca BYPASSRLS).</summary>
public enum Role
{
    Owner,
    App,
    Worker,
    Config,
}

/// <summary>
/// PostgreSQL 16 temporário e real (sem Docker): <c>initdb</c> em diretório temporário, migração <b>real</b> (<c>backend/db/migrations</c>)
/// aplicada uma vez num banco-modelo e um banco novo por teste. As provas rodam como <c>nina_app</c>/<c>nina_worker</c>/<c>nina_config_admin</c>
/// (logins sem privilégios especiais); o dono (superusuário do cluster de teste) só monta cenário e inspeciona.
/// Para usar um servidor existente, defina <c>NINA_TEST_PG_ADMIN</c> (conexão superusuário).
/// </summary>
public sealed class PgCluster : IAsyncLifetime
{
    public const string AppLogin = "t_app_login";
    public const string WorkerLogin = "t_worker_login";
    public const string ConfigLogin = "t_config_login";

    private string? _dir;
    private string? _bin;
    private string _adminBase = string.Empty;
    private bool _ownsCluster;

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("NINA_TEST_PG_ADMIN");
        if (!string.IsNullOrWhiteSpace(external))
        {
            _adminBase = external;
        }
        else
        {
            StartCluster();
            _ownsCluster = true;
        }

        await using (var admin = new NpgsqlConnection(AdminConnectionString("postgres")))
        {
            await admin.OpenAsync();
            await ExecAsync(admin, "DROP DATABASE IF EXISTS nina_template WITH (FORCE)");
            await ExecAsync(admin, "CREATE DATABASE nina_template");
        }

        var migrations = MigrationRunner.FindDefaultDirectory(AppContext.BaseDirectory)
                         ?? throw new InvalidOperationException("backend/db/migrations não encontrado");
        await new MigrationRunner(AdminConnectionString("nina_template"), migrations).ApplyAsync(CancellationToken.None);

        // NR-03/NR-09: o migrator/dono instala a chave de assinatura servidor-banco (a API a deriva do Security:MasterKey).
        await new MigrationRunner(AdminConnectionString("nina_template"), migrations).ProvisionServerKeyAsync(ServerSigner.Key, CancellationToken.None);

        await using var superuser = new NpgsqlConnection(AdminConnectionString("nina_template"));
        await superuser.OpenAsync();
        foreach (var (login, role) in new[] { (AppLogin, "nina_app"), (WorkerLogin, "nina_worker"), (ConfigLogin, "nina_config_admin") })
        {
            await ExecAsync(superuser, $"DROP ROLE IF EXISTS {login}");
            await ExecAsync(superuser, $"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE IN ROLE {role}");
            // NR-04: ALTER ROLE ... SET vale para o LOGIN, nao para o papel de grupo: a infraestrutura repete os limites no login.
            await ExecAsync(superuser, $"SELECT nina.apply_role_limits('{login}', '{role}')");
        }
    }

    public Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        if (_ownsCluster)
        {
            Cleanup();
        }

        return Task.CompletedTask;
    }

    /// <summary>Cria um banco novo a partir do modelo migrado.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = "nina_db_" + Guid.NewGuid().ToString("N")[..12];
        await using var admin = new NpgsqlConnection(AdminConnectionString("postgres"));
        await admin.OpenAsync();
        await ExecAsync(admin, $"CREATE DATABASE {name} TEMPLATE nina_template");
        return name;
    }

    public async Task DropDatabaseAsync(string name)
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(AdminConnectionString("postgres"));
        await admin.OpenAsync();
        await ExecAsync(admin, $"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
    }

    public string ConnectionString(Role role, string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(_adminBase) { Database = database, Pooling = false, Timeout = 15 };
        switch (role)
        {
            case Role.App:
                builder.Username = AppLogin;
                builder.Password = null;
                break;
            case Role.Worker:
                builder.Username = WorkerLogin;
                builder.Password = null;
                break;
            case Role.Config:
                builder.Username = ConfigLogin;
                builder.Password = null;
                break;
            default:
                break;
        }

        return builder.ConnectionString;
    }

    public string AdminConnectionString(string database) => ConnectionString(Role.Owner, database);

    private void StartCluster()
    {
        _bin = LocateBin();
        _dir = Directory.CreateTempSubdirectory("nina-dbtests-pg-").FullName;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
        if (Environment.UserName == "root")
        {
            Run("chown", $"postgres {_dir}", asPostgres: false);
        }

        var data = Path.Combine(_dir, "data");
        Run(Path.Combine(_bin, "initdb"), $"-D {data} -U postgres --auth=trust --encoding=UTF8 --locale=C", asPostgres: true);

        var port = FreePort();
        var conf = string.Join('\n',
            $"port = {port}",
            "listen_addresses = '127.0.0.1'",
            $"unix_socket_directories = '{_dir}'",
            "fsync = off",
            "synchronous_commit = off",
            "full_page_writes = off",
            "max_connections = 200",
            "shared_buffers = 64MB");
        File.AppendAllText(Path.Combine(data, "postgresql.conf"), "\n" + conf + "\n");
        Run(Path.Combine(_bin, "pg_ctl"), $"-D {data} -l {Path.Combine(_dir, "pg.log")} -w start", asPostgres: true);
        _adminBase = $"Host=127.0.0.1;Port={port};Username=postgres;Database=postgres;Timeout=15";
    }

    private void Cleanup()
    {
        if (_ownsCluster && _dir is not null && Directory.Exists(_dir) && _bin is not null)
        {
            Run(Path.Combine(_bin, "pg_ctl"), $"-D {Path.Combine(_dir, "data")} -m immediate stop", asPostgres: true, throwOnError: false);
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
                // processo ainda finalizando; o ProcessExit tenta de novo
            }
        }
    }

    private static string LocateBin()
    {
        var env = Environment.GetEnvironmentVariable("NINA_PG_BIN");
        if (!string.IsNullOrEmpty(env))
        {
            return env;
        }

        var root = "/usr/lib/postgresql";
        var candidate = Directory.Exists(root)
            ? Directory.GetDirectories(root).OrderByDescending(d => d, StringComparer.Ordinal).Select(d => Path.Combine(d, "bin")).FirstOrDefault(Directory.Exists)
            : null;
        return candidate ?? throw new InvalidOperationException("Binários do PostgreSQL não encontrados (defina NINA_PG_BIN).");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void Run(string file, string args, bool asPostgres, bool throwOnError = true)
    {
        var useRunuser = asPostgres && Environment.UserName == "root";
        var psi = new ProcessStartInfo(useRunuser ? "runuser" : file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (useRunuser)
        {
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add("postgres");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(file);
        }

        foreach (var arg in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0 && throwOnError)
        {
            throw new InvalidOperationException($"{file} falhou ({process.ExitCode}): {stderr.Result}{stdout.Result}");
        }
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PgClusterGroup : ICollectionFixture<PgCluster>
{
    public const string Name = "pg-cluster";
}
