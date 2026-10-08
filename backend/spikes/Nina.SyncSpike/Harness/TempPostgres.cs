using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace Nina.SyncSpike.Harness;

/// <summary>
/// PostgreSQL 16 temporário: initdb em diretório temporário, porta alta aleatória, aplica 0001_init.sql + extras do spike.
/// Como root o servidor não sobe (recusa), então os binários rodam via `runuser` com um usuário sem privilégios.
/// Tudo (processo e diretório) é removido em DisposeAsync.
/// </summary>
public sealed class TempPostgres : IAsyncDisposable
{
    private readonly string _bin;
    private readonly string? _runAs;
    private bool _started;

    public string Dir { get; }
    public int Port { get; }
    public string Database => "nina";
    public string Version { get; private set; } = "";

    private TempPostgres(string bin, string? runAs, string dir, int port) { _bin = bin; _runAs = runAs; Dir = dir; Port = port; }

    public string ConnectionString(string user, int maxPool = 50, bool pooling = true) =>
        $"Host=127.0.0.1;Port={Port};Username={user};Database={Database};Timezone=UTC;Maximum Pool Size={maxPool};Pooling={pooling};" +
        "Minimum Pool Size=0;Command Timeout=60;No Reset On Close=false";

    public static async Task<TempPostgres> StartAsync(string migrationPath, string extrasSql)
    {
        var bin = Environment.GetEnvironmentVariable("NINA_PG_BIN") ?? "/usr/lib/postgresql/16/bin";
        var isRoot = Environment.UserName == "root";
        var runAs = isRoot ? (Environment.GetEnvironmentVariable("NINA_PG_USER") ?? "claude") : null;
        var dir = Directory.CreateTempSubdirectory("nina-syncspike-pg-").FullName;
        if (runAs != null) Run("chown", $"{runAs} {dir}", null);
        var pg = new TempPostgres(bin, runAs, dir, FreePort());
        try
        {
            await pg.InitAndStartAsync();
            await pg.ApplySqlFileAsync(migrationPath);
            var extras = Path.Combine(dir, "spike_extras.sql");
            await File.WriteAllTextAsync(extras, extrasSql);
            await pg.ApplySqlFileAsync(extras);
            return pg;
        }
        catch
        {
            await pg.DisposeAsync();
            throw;
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static string Run(string file, string args, string? runAs, bool throwOnError = true)
    {
        var psi = runAs == null
            ? new ProcessStartInfo(file, args)
            : new ProcessStartInfo("runuser", $"-u {runAs} -- {file} {args}");
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && throwOnError) throw new InvalidOperationException($"{file} {args} -> {p.ExitCode}: {o}");
        return o;
    }

    private async Task InitAndStartAsync()
    {
        var data = Path.Combine(Dir, "data");
        Run(Path.Combine(_bin, "initdb"), $"-D {data} -A trust -U postgres -E UTF8 --no-sync", _runAs);
        // fsync LIGADO (padrão): as medições de latência incluem o custo real de commit durável.
        var opts = $"-p {Port} -c listen_addresses=127.0.0.1 -k {Dir} -c timezone=UTC -c max_connections=300 " +
                   "-c log_min_messages=warning -c deadlock_timeout=200ms";
        Run(Path.Combine(_bin, "pg_ctl"), $"-D {data} -l {Path.Combine(Dir, "pg.log")} -w -t 60 -o \"{opts}\" start", _runAs);
        _started = true;
        await using var c = new NpgsqlConnection($"Host=127.0.0.1;Port={Port};Username=postgres;Database=postgres");
        await c.OpenAsync();
        await using (var cmd = new NpgsqlCommand("CREATE DATABASE nina", c)) await cmd.ExecuteNonQueryAsync();
        await using (var cmd = new NpgsqlCommand("SHOW server_version", c)) Version = (string)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Aplica um script SQL arbitrário como superuser (psql, ON_ERROR_STOP).</summary>
    public async Task ApplySqlAsync(string sql)
    {
        var f = Path.Combine(Dir, $"adhoc-{Guid.NewGuid():N}.sql");
        await File.WriteAllTextAsync(f, sql);
        await ApplySqlFileAsync(f);
    }

    private Task ApplySqlFileAsync(string path)
    {
        var o = Run(Path.Combine(_bin, "psql"),
            $"-h 127.0.0.1 -p {Port} -U postgres -d {Database} -v ON_ERROR_STOP=1 -q -f {path}", null);
        _ = o;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        if (_started)
        {
            Run(Path.Combine(_bin, "pg_ctl"), $"-D {Path.Combine(Dir, "data")} -m immediate -w stop", _runAs, false);
            _started = false;
        }
        try { Directory.Delete(Dir, true); } catch (IOException) { /* best effort */ }
        await Task.CompletedTask;
    }
}
