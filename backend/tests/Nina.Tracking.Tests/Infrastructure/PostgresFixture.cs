using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Nina.SharedKernel.Data;
using Npgsql;

namespace Nina.Tracking.Tests.Infrastructure;

/// <summary>
/// PostgreSQL 16 temporário e real (sem Docker): <c>initdb</c> em diretório temporário, porta alta em 127.0.0.1,
/// migração aplicada uma vez em um banco-modelo e um banco por classe de teste (<c>CREATE DATABASE ... TEMPLATE</c>).
/// Como root, os binários rodam como o usuário <c>postgres</c> via <c>runuser</c>. Remove tudo ao final.
/// Para usar um servidor existente, defina <c>NINA_TEST_PG_ADMIN</c> (conexão superusuário).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string AppPassword = "nina_app_test_password";
    private string? _dir;
    private string? _bin;
    private int _port;
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
            await ExecAsync(admin, "DROP DATABASE IF EXISTS nina_template");
            await ExecAsync(admin, "CREATE DATABASE nina_template");
        }

        var migrations = MigrationRunner.FindDefaultDirectory(AppContext.BaseDirectory)
                         ?? throw new InvalidOperationException("backend/db/migrations não encontrado");
        var runner = new MigrationRunner(AdminConnectionString("nina_template"), migrations);
        await runner.ApplyAsync(CancellationToken.None);
        // NR-03/NR-09: o migrator/dono instala a chave de assinatura servidor-banco. Mesma derivação de ServerMac.Key (SecretKeys.Derive("db-mac")):
        // HMAC-SHA256(Security:MasterKey, "nina.v1:db-mac"), com a MasterKey que o ApiFactory configura.
        var serverKey = System.Security.Cryptography.HMACSHA256.HashData(
            Convert.FromBase64String(TestConstants.MasterKey), System.Text.Encoding.UTF8.GetBytes("nina.v1:db-mac"));
        await runner.ProvisionServerKeyAsync(serverKey, CancellationToken.None);

        await using var superuser = new NpgsqlConnection(AdminConnectionString("nina_template"));
        await superuser.OpenAsync();
        // O papel nina_app nasce NOLOGIN na migração; a infraestrutura lhe dá login (aqui, só no cluster de teste).
        await ExecAsync(superuser, $"ALTER ROLE nina_app LOGIN PASSWORD '{AppPassword}'");
    }

    public Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        if (_ownsCluster && _dir is not null && _bin is not null)
        {
            Run(Path.Combine(_bin, "pg_ctl"), $"-D {Path.Combine(_dir, "data")} -m immediate -w stop", asPostgres: true, throwOnError: false);
            DeleteDirectory(_dir);
        }

        return Task.CompletedTask;
    }

    /// <summary>Cria um banco novo a partir do modelo migrado e devolve as conexões (dono e <c>nina_app</c>).</summary>
    public async Task<TestDatabase> CreateDatabaseAsync()
    {
        var name = "nina_t_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(AdminConnectionString("postgres")))
        {
            await admin.OpenAsync();
            await ExecAsync(admin, $"CREATE DATABASE {name} TEMPLATE nina_template");
        }

        return new TestDatabase(name, AdminConnectionString(name), AppConnectionString(name, pooling: true));
    }

    public string AdminConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(_adminBase) { Database = database, Pooling = false }.ConnectionString;

    public string AppConnectionString(string database, bool pooling) =>
        new NpgsqlConnectionStringBuilder(_adminBase)
        {
            Database = database,
            Username = "nina_app",
            Password = AppPassword,
            Pooling = pooling,
            MaxPoolSize = 20,
        }.ConnectionString;

    private void StartCluster()
    {
        _bin = LocateBin();
        _dir = Directory.CreateTempSubdirectory("nina-pg-").FullName;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
        var asRoot = Environment.UserName == "root";
        if (asRoot)
        {
            Run("chown", $"postgres {_dir}", asPostgres: false);
        }

        var data = Path.Combine(_dir, "data");
        Run(Path.Combine(_bin, "initdb"), $"-D {data} -U postgres --auth=trust --encoding=UTF8 --locale=C", asPostgres: true);

        _port = FreePort();
        var conf = string.Join('\n',
            $"port = {_port}",
            "listen_addresses = '127.0.0.1'",
            $"unix_socket_directories = '{_dir}'",
            "fsync = off",
            "synchronous_commit = off",
            "full_page_writes = off",
            "max_connections = 100",
            "shared_buffers = 64MB");
        File.AppendAllText(Path.Combine(data, "postgresql.conf"), "\n" + conf + "\n");
        Run(Path.Combine(_bin, "pg_ctl"), $"-D {data} -l {Path.Combine(_dir, "pg.log")} -w start", asPostgres: true);
        _adminBase = $"Host=127.0.0.1;Port={_port};Username=postgres;Database=postgres;Timeout=15";
    }

    private void Cleanup()
    {
        if (_ownsCluster && _dir is not null && Directory.Exists(_dir) && _bin is not null)
        {
            Run(Path.Combine(_bin, "pg_ctl"), $"-D {Path.Combine(_dir, "data")} -m immediate stop", asPostgres: true, throwOnError: false);
            DeleteDirectory(_dir);
        }
    }

    private static void DeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // processo ainda finalizando; o ProcessExit tenta de novo
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

public sealed record TestDatabase(string Name, string AdminConnectionString, string AppConnectionString);

[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
