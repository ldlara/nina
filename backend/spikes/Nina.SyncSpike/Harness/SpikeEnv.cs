using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Nina.SyncSpike.Server;
using Npgsql;

namespace Nina.SyncSpike.Harness;

public sealed record BabyWorld(Guid BabyId, Guid OwnerId);

public sealed record LogStats(long Count, long Min, long Max, long Head, long PurgedThrough, bool Contiguous, bool ChangedAtMonotonic);

/// <summary>
/// Ambiente completo do spike: PostgreSQL temporário + SyncService (papel spike_app, sujeito a RLS) +
/// operações administrativas (superuser/worker/config) usadas só pelo harness para montar cenários e inspecionar.
/// </summary>
public sealed class SpikeEnv : IAsyncDisposable
{
    public TempPostgres Pg { get; }
    public NpgsqlDataSource App { get; }
    public NpgsqlDataSource Super { get; }
    public NpgsqlDataSource Worker { get; }
    public NpgsqlDataSource Cfg { get; }
    public ManualClock Clock { get; } = new();
    public SyncService Service { get; private set; }
    public byte[] Secret { get; } = RandomNumberGenerator.GetBytes(32);

    private SpikeEnv(TempPostgres pg)
    {
        Pg = pg;
        App = NpgsqlDataSource.Create(pg.ConnectionString("spike_app", 120));
        Super = NpgsqlDataSource.Create(pg.ConnectionString("postgres", 20));
        Worker = NpgsqlDataSource.Create(pg.ConnectionString("spike_worker", 5));
        Cfg = NpgsqlDataSource.Create(pg.ConnectionString("spike_cfg", 5));
        Service = new SyncService(App, new CursorCodec(Secret), Clock);
    }

    public SyncService NewService(SyncOptions options) => new(App, new CursorCodec(Secret), Clock, options);

    public static async Task<SpikeEnv> StartAsync()
    {
        var migration = FindMigration();
        var asm = typeof(SpikeEnv).Assembly;
        await using var s = asm.GetManifestResourceStream(asm.GetManifestResourceNames().Single(n => n.EndsWith("spike_extras.sql")))!;
        using var rd = new StreamReader(s);
        var pg = await TempPostgres.StartAsync(migration, await rd.ReadToEndAsync());
        return new SpikeEnv(pg);
    }

    public static string FindMigration()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "backend", "db", "migrations", "0001_init.sql");
            if (File.Exists(p)) return p;
            p = Path.Combine(d.FullName, "db", "migrations", "0001_init.sql");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("0001_init.sql não encontrado a partir de " + AppContext.BaseDirectory);
    }

    // ------------------------------------------------------------ montagem de cenário (superuser)

    public async Task<Guid> CreateUserAsync(string? email = null)
    {
        var id = Guid.NewGuid();
        await using var c = Super.CreateCommand("INSERT INTO nina.app_user (id, email) VALUES (@id, @e)");
        c.Parameters.AddWithValue("id", id);
        c.Parameters.AddWithValue("e", email ?? $"u{id:N}@example.test");
        await c.ExecuteNonQueryAsync();
        return id;
    }

    public async Task<BabyWorld> CreateBabyAsync(Guid? owner = null, params (Guid User, string Role)[] others)
    {
        var o = owner ?? await CreateUserAsync();
        var baby = Guid.NewGuid();
        await using var conn = await Super.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        async Task Exec(string sql, params (string, object)[] ps)
        {
            await using var c = new NpgsqlCommand(sql, conn, tx);
            foreach (var (k, v) in ps) c.Parameters.AddWithValue(k, v);
            await c.ExecuteNonQueryAsync();
        }
        var family = Guid.NewGuid();
        await Exec("INSERT INTO nina.family (id, owner_user_id) VALUES (@f, @o) ON CONFLICT (owner_user_id) DO NOTHING", ("f", family), ("o", o));
        await using (var q = new NpgsqlCommand("SELECT id FROM nina.family WHERE owner_user_id = @o", conn, tx))
        {
            q.Parameters.AddWithValue("o", o);
            family = (Guid)(await q.ExecuteScalarAsync())!;
        }
        await Exec("INSERT INTO nina.baby (id, family_id, display_name, birth_date, timezone, created_by, last_modified_by) " +
                   "VALUES (@b, @f, 'Bebe Teste', current_date - 40, 'America/Sao_Paulo', @o, @o)", ("b", baby), ("f", family), ("o", o));
        await Exec("INSERT INTO nina.caregiver_membership (baby_id, user_id, role, status, accepted_at) VALUES (@b, @o, 'OWNER', 'ACTIVE', now())",
            ("b", baby), ("o", o));
        foreach (var (user, role) in others)
            await Exec("INSERT INTO nina.caregiver_membership (baby_id, user_id, role, status, accepted_at, invited_by) VALUES (@b, @u, @r, 'ACTIVE', now(), @o)",
                ("b", baby), ("u", user), ("r", role), ("o", o));
        await tx.CommitAsync();
        return new BabyWorld(baby, o);
    }

    public Task AddMemberAsync(Guid baby, Guid user, string role) =>
        NonQueryAsync("INSERT INTO nina.caregiver_membership (baby_id, user_id, role, status, accepted_at) VALUES (@b, @u, @r, 'ACTIVE', now())",
            ("b", baby), ("u", user), ("r", role));

    public Task RevokeAsync(Guid baby, Guid user) =>
        NonQueryAsync("UPDATE nina.caregiver_membership SET status = 'REVOKED', revoked_at = now(), revoked_reason = 'OWNER_REMOVED' " +
                      "WHERE baby_id = @b AND user_id = @u AND status = 'ACTIVE'", ("b", baby), ("u", user));

    public async Task NonQueryAsync(string sql, params (string, object?)[] ps)
    {
        await using var c = Super.CreateCommand(sql);
        foreach (var (k, v) in ps) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        await c.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql, params (string, object?)[] ps)
    {
        await using var c = Super.CreateCommand(sql);
        foreach (var (k, v) in ps) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return (T)(await c.ExecuteScalarAsync())!;
    }

    /// <summary>Flag de política via papel nina_config_admin (auditada por trigger; exige nina.user_id).</summary>
    public async Task SetOverlapPolicyAsync(string policy, Guid actor)
    {
        await using var conn = await Cfg.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var s = new NpgsqlCommand("SELECT set_config('nina.user_id', @u, true)", conn, tx))
        {
            s.Parameters.AddWithValue("u", actor.ToString());
            await s.ExecuteNonQueryAsync();
        }
        await using (var u = new NpgsqlCommand(
            "UPDATE nina.app_parameter SET value = to_jsonb(@p::text) WHERE param_key = 'sleep.overlap_policy'", conn, tx))
        {
            u.Parameters.AddWithValue("p", policy);
            await u.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    /// <summary>Envelhece change_log/tombstone do bebê (simula passagem de tempo) e roda a purga como nina_worker.</summary>
    public async Task<(long ChangeLog, long Tombstones, long Mutations)> AgeAndPurgeAsync(Guid baby, int days, long? throughSeq = null)
    {
        await NonQueryAsync("UPDATE nina.change_log SET changed_at = changed_at - make_interval(days => @d) WHERE baby_id = @b AND sync_sequence <= @s",
            ("d", days), ("b", baby), ("s", throughSeq ?? long.MaxValue));
        await NonQueryAsync("UPDATE nina.tombstone SET deleted_at = deleted_at - make_interval(days => @d), expires_at = expires_at - make_interval(days => @d) " +
                            "WHERE baby_id = @b AND version <= @s", ("d", days), ("b", baby), ("s", throughSeq ?? long.MaxValue));
        await NonQueryAsync("UPDATE nina.sync_mutation SET received_at = received_at - make_interval(days => @d) WHERE baby_id = @b", ("d", days), ("b", baby));
        await using var c = Worker.CreateCommand("SELECT * FROM nina.purge_expired_sync_data(100000)");
        await using var r = await c.ExecuteReaderAsync();
        await r.ReadAsync();
        return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
    }

    // ------------------------------------------------------------ inspeção

    public async Task<LogStats> LogStatsAsync(Guid baby)
    {
        await using var c = Super.CreateCommand(
            "SELECT count(*), coalesce(min(sync_sequence),0), coalesce(max(sync_sequence),0), " +
            "(SELECT last_sequence FROM nina.baby_sync_head WHERE baby_id = @b), (SELECT purged_through FROM nina.baby_sync_head WHERE baby_id = @b), " +
            "NOT EXISTS (SELECT 1 FROM (SELECT changed_at, lag(changed_at) OVER (ORDER BY sync_sequence) p FROM nina.change_log WHERE baby_id = @b) z WHERE z.changed_at < z.p) " +
            "FROM nina.change_log WHERE baby_id = @b");
        c.Parameters.AddWithValue("b", baby);
        await using var r = await c.ExecuteReaderAsync();
        await r.ReadAsync();
        long count = r.GetInt64(0), min = r.GetInt64(1), max = r.GetInt64(2), head = r.GetInt64(3), purged = r.GetInt64(4);
        return new LogStats(count, min, max, head, purged, count == 0 || (max - min + 1 == count), r.GetBoolean(5));
    }

    /// <summary>Estado vivo canônico no servidor (mesma projeção do pull), para comparar convergência.</summary>
    public async Task<SortedDictionary<string, JsonObject>> ServerAliveAsync(Guid baby)
    {
        var d = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var f in EntityCatalog.Feed)
        {
            await using var c = Super.CreateCommand(
                $"SELECT id, to_jsonb(t) - 'created_by' - 'last_modified_by' FROM nina.{f.Table} t WHERE {f.BabyCol} = @b AND deleted_at IS NULL");
            c.Parameters.AddWithValue("b", baby);
            await using var r = await c.ExecuteReaderAsync();
            while (await r.ReadAsync()) d[$"{f.ContractType}:{r.GetGuid(0)}"] = JsonNode.Parse(r.GetFieldValue<string>(1))!.AsObject();
        }
        return d;
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync(); await Super.DisposeAsync(); await Worker.DisposeAsync(); await Cfg.DisposeAsync();
        await Pg.DisposeAsync();
    }
}
