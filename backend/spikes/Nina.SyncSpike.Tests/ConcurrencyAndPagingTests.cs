using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Nina.SyncSpike.Harness;
using Nina.SyncSpike.Server;
using Npgsql;
using Xunit;

namespace Nina.SyncSpike.Tests;

[Collection("pg")]
public sealed class ConcurrencyTests(SpikeFixture fx)
{
    private SpikeEnv Env => fx.Env;
    private static DateTimeOffset T(int m) => Data.T0.AddMinutes(m);

    /// <summary>Leitor de change_log como nina_app (RLS): prova que quem lê `> cursor` nunca vê um buraco que depois seja preenchido.</summary>
    private async Task<(long Last, List<string> Violations)> ReaderAsync(Guid user, Guid baby, Func<bool> done)
    {
        long last = 0; var bad = new List<string>();
        while (true)
        {
            var finished = done();
            await using var conn = await Env.App.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var s = new NpgsqlCommand($"SELECT set_config('nina.user_id', '{user}', true)", conn, tx)) await s.ExecuteNonQueryAsync();
            await using var q = new NpgsqlCommand("SELECT sync_sequence FROM nina.change_log WHERE baby_id = @b AND sync_sequence > @c ORDER BY sync_sequence", conn, tx);
            q.Parameters.AddWithValue("b", baby); q.Parameters.AddWithValue("c", last);
            await using var r = await q.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var seq = r.GetInt64(0);
                if (seq != last + 1) bad.Add($"esperado {last + 1}, veio {seq}");
                last = seq;
            }
            if (finished) return (last, bad);
        }
    }

    [Fact]
    public async Task Sequence_is_contiguous_in_commit_order_under_concurrent_pushes_with_rollbacks_and_a_live_reader()
    {
        var owner = await Env.CreateUserAsync();
        var users = new List<Guid> { owner };
        for (var i = 0; i < 4; i++) users.Add(await Env.CreateUserAsync());
        var baby = await Env.CreateBabyAsync(owner, users.Skip(1).Select(u => (u, "CAREGIVER")).ToArray());
        var devices = users.Select((u, i) => new FakeDevice("D" + i, Env.Service, u, baby.BabyId)).ToList();
        var shared = devices[0].Create("DIAPER_EVENT", Data.Diaper(T(0)));
        await devices[0].SyncAsync();

        var results = new ConcurrentBag<MutationResult>();
        var workersDone = false;
        var reader = ReaderAsync(owner, baby.BabyId, () => Volatile.Read(ref workersDone));
        var workers = devices.Select((d, w) => Task.Run(async () =>
        {
            var rnd = new Random(w);
            var mine = new List<Guid>();
            Mutation? replay = null;
            for (var it = 0; it < 30; it++)
            {
                var ms = new List<Mutation>();
                for (var k = 0; k < 4; k++)
                {
                    var id = Guid.NewGuid(); mine.Add(id);
                    ms.Add(new Mutation(Guid.NewGuid(), "CREATE", "DIAPER_EVENT", id, baby.BabyId, 0, Data.T0, Data.Diaper(T(w * 1000 + it * 10 + k))));
                }
                for (var k = 0; k < 2; k++)    // inválidas: o savepoint reverte inclusive o incremento do contador
                    ms.Add(new Mutation(Guid.NewGuid(), "CREATE", "SLEEP_SESSION", Guid.NewGuid(), baby.BabyId, 0, Data.T0, Data.Sleep(T(100), T(10))));
                ms.Add(new Mutation(Guid.NewGuid(), "UPDATE", "DIAPER_EVENT", shared, baby.BabyId, 1, T(w * 100 + it), FakeDevice.D(("notes", $"w{w}-{it}"))));
                ms.Add(new Mutation(Guid.NewGuid(), "CREATE", "SLEEP_SESSION", Guid.NewGuid(), baby.BabyId, 0, Data.T0, Data.Sleep(T(5000 + w * 500 + it * 5), T(5000 + w * 500 + it * 5 + 3))));
                if (mine.Count > 8 && rnd.Next(3) == 0)
                    ms.Add(new Mutation(Guid.NewGuid(), "DELETE", "DIAPER_EVENT", mine[rnd.Next(mine.Count - 4)], baby.BabyId, 1, Data.T0, null));
                if (replay != null) ms.Add(replay);          // reenvio de mutação já aplicada num lote anterior
                replay = ms[0];
                var resp = await Env.Service.PushAsync(d.Auth, new PushRequest(d.DeviceId, ms));
                foreach (var r in resp.Results) results.Add(r);
            }
        })).ToArray();
        await Task.WhenAll(workers);
        Volatile.Write(ref workersDone, true);
        var (lastSeen, violations) = await reader;

        Assert.Empty(violations);
        var st = await Env.LogStatsAsync(baby.BabyId);
        Assert.True(st.Contiguous, "change_log sem buracos");
        Assert.Equal(1, st.Min);
        Assert.Equal(st.Head, st.Max);
        Assert.Equal(st.Head, st.Count);                       // 1..N == last_sequence: rollbacks não deixaram lacuna
        Assert.Equal(st.Head, lastSeen);                       // o leitor concorrente alcançou exatamente N
        Assert.True(st.ChangedAtMonotonic, "changed_at acompanha a sequência (ordem de commit)");
        Assert.DoesNotContain(results, r => r.Problem?.Code == "TRANSIENT");
        Assert.True(results.Count(r => r.Status == "REJECTED") >= 5 * 30 * 2);
        Assert.True(results.Count(r => r.Status == "DUPLICATE") >= 5 * 29);
        // cada entidade: version == última sequência dela no log; sem versão repetida entre entidades vivas
        var dbg = await Env.ScalarAsync<string>(
            "SELECT coalesce(string_agg(c.entity_type||' '||c.m||' vs '||e.version, '; '), 'none') FROM (SELECT entity_id, entity_type, max(sync_sequence) m FROM nina.change_log WHERE baby_id=@b AND entity_type<>'BABY' GROUP BY 1,2) c " +
            "JOIN (SELECT id, version FROM nina.diaper_event UNION ALL SELECT id, version FROM nina.sleep_session) e ON e.id=c.entity_id WHERE e.version<>c.m LIMIT 5", ("b", baby.BabyId));
        Assert.True(dbg == "none", dbg);
        Assert.Equal(0, await Env.ScalarAsync<long>(
            "SELECT count(*) FROM (SELECT version FROM (SELECT version FROM nina.diaper_event WHERE baby_id=@b UNION ALL SELECT version FROM nina.sleep_session WHERE baby_id=@b) u GROUP BY version HAVING count(*) > 1) d", ("b", baby.BabyId)));
        // todos os aparelhos convergem
        foreach (var d in devices) await d.SyncAsync();
        foreach (var d in devices) await d.SyncAsync();
        await new Trio { Env = Env, Baby = baby }.AssertConvergedAsync(devices.ToArray());
    }

    [Fact]
    public async Task Multi_baby_batches_in_opposite_order_do_not_deadlock()
    {
        var x = await Env.CreateUserAsync(); var y = await Env.CreateUserAsync();
        var b1 = await Env.CreateBabyAsync(x, (y, "CAREGIVER"));
        var b2 = await Env.CreateBabyAsync(x, (y, "CAREGIVER"));
        var results = new ConcurrentBag<MutationResult>();
        var tasks = Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            var user = w % 2 == 0 ? x : y; var dev = Guid.NewGuid();
            for (var it = 0; it < 25; it++)
            {
                Mutation M(BabyWorld b) => new(Guid.NewGuid(), "CREATE", "DIAPER_EVENT", Guid.NewGuid(), b.BabyId, 0, Data.T0, Data.Diaper(T(it)));
                var ms = w % 2 == 0 ? new[] { M(b1), M(b2) } : new[] { M(b2), M(b1) };      // ordens opostas no pedido
                var resp = await Env.Service.PushAsync(new AuthContext(user, dev), new PushRequest(dev, ms));
                foreach (var r in resp.Results) results.Add(r);
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(8 * 25 * 2, results.Count(r => r.Status == "APPLIED"));
        Assert.DoesNotContain(results, r => r.Problem?.Code == "TRANSIENT");
        foreach (var b in new[] { b1, b2 }) Assert.True((await Env.LogStatsAsync(b.BabyId)).Contiguous);
        Assert.Equal(0, await Env.ScalarAsync<long>("SELECT count(*) FROM pg_stat_database WHERE datname = 'nina' AND deadlocks > 0"));
    }

    [Fact]
    public async Task Per_baby_transaction_and_per_mutation_modes_give_identical_results()
    {
        async Task<(List<string> Summary, SortedDictionary<string, JsonObject> State)> Run(PushMode mode)
        {
            var svc = Env.NewService(new SyncOptions { Mode = mode });
            var u = await Env.CreateUserAsync();
            var baby = await Env.CreateBabyAsync(u);
            var dev = Guid.NewGuid(); var auth = new AuthContext(u, dev);
            var s = Guid.NewGuid(); var d = Guid.NewGuid();
            var ms = new List<Mutation>
            {
                new(Guid.NewGuid(), "CREATE", "SLEEP_SESSION", s, baby.BabyId, 0, Data.T0, Data.Sleep(T(0), T(60))),
                new(Guid.NewGuid(), "CREATE", "SLEEP_SESSION", Guid.NewGuid(), baby.BabyId, 0, Data.T0, Data.Sleep(T(30), T(90))),
                new(Guid.NewGuid(), "CREATE", "SLEEP_SESSION", Guid.NewGuid(), baby.BabyId, 0, Data.T0, Data.Sleep(T(9), T(1))),
                new(Guid.NewGuid(), "CREATE", "DIAPER_EVENT", d, baby.BabyId, 0, Data.T0, Data.Diaper(T(5))),
                new(Guid.NewGuid(), "UPDATE", "DIAPER_EVENT", d, baby.BabyId, 0, T(6), FakeDevice.D(("diaper_type", "DRY"))),
                new(Guid.NewGuid(), "DELETE", "DIAPER_EVENT", d, baby.BabyId, 0, T(7), null),
                new(Guid.NewGuid(), "UPDATE", "DIAPER_EVENT", d, baby.BabyId, 0, T(8), FakeDevice.D(("diaper_type", "WET"))),
            };
            var resp = await svc.PushAsync(auth, new PushRequest(dev, ms));
            var summary = resp.Results.Select(r => $"{r.Status}/{r.Resolution}/{r.Problem?.Code}/{r.Version}").ToList();
            var state = await Env.ServerAliveAsync(baby.BabyId);
            foreach (var k in state.Keys.ToList()) { state[k].Remove("id"); state[k].Remove("baby_id"); state[k].Remove("created_at"); state[k].Remove("updated_at"); state[k].Remove("deleted_at"); }
            return (summary, state);
        }
        var a = await Run(PushMode.PerBabyTransaction);
        var b = await Run(PushMode.PerMutationTransaction);
        Assert.Equal(a.Summary, b.Summary);
        Assert.Equal(a.State.Count, b.State.Count);
    }
}

[Collection("pg")]
public sealed class PagingAndClockTests(SpikeFixture fx)
{
    private SpikeEnv Env => fx.Env;
    private static DateTimeOffset T(int m) => Data.T0.AddMinutes(m);

    private sealed class Applier
    {
        public Dictionary<string, (long Ver, JsonObject Json)> Alive { get; } = [];
        public Dictionary<string, long> Tomb { get; } = [];
        public void Apply(PullChange c)
        {
            var k = $"{c.EntityType}:{c.EntityId}";
            if (c.Op == "TOMBSTONE") { Alive.Remove(k); Tomb[k] = c.Version; return; }
            if (Tomb.TryGetValue(k, out var tv) && tv > c.Version) return;
            if (Alive.TryGetValue(k, out var cur) && cur.Ver >= c.Version) return;
            Alive[k] = (c.Version, (JsonObject)c.Entity!.DeepClone());
        }
    }

    [Fact]
    public async Task Paginated_snapshot_with_writes_in_between_converges_with_the_following_delta()
    {
        var t = await Trio.CreateAsync(Env);
        var diapers = new List<Guid>();
        for (var i = 0; i < 300; i++) diapers.Add(t.A.Create("DIAPER_EVENT", Data.Diaper(T(i))));
        for (var i = 0; i < 100; i++) t.A.Create("SLEEP_SESSION", Data.Sleep(T(1000 + i * 100), T(1000 + i * 100 + 50)));
        for (var i = 0; i < 50; i++) t.A.Create("FEEDING_SESSION", Data.Bottle(T(20000 + i * 30)));
        await t.A.SyncAsync();

        var ap = new Applier(); string? cursor = null; var pages = 0; var modes = new List<string>();
        PullResponse page;
        do
        {
            page = await Env.Service.PullAsync(t.B.Auth, t.Baby.BabyId, cursor, 100);
            modes.Add(page.Mode); pages++;
            foreach (var c in page.Changes) ap.Apply(c);
            cursor = page.NextCursor;
            if (pages == 2)                                    // escritas concorrentes no meio do snapshot
            {
                foreach (var d in diapers.Take(20)) t.A.Delete("DIAPER_EVENT", d);
                for (var i = 0; i < 30; i++) t.A.Create("DIAPER_EVENT", Data.Diaper(T(50000 + i)));
                foreach (var d in diapers.Skip(100).Take(10)) t.A.Update("DIAPER_EVENT", d, FakeDevice.D(("notes", "mudou")));
                await t.A.SyncAsync();
            }
        } while (page.HasMore);
        Assert.True(pages >= 5, $"páginas: {pages}");
        Assert.All(modes, m => Assert.Equal("SNAPSHOT", m));
        // o último next_cursor do snapshot continua em delta
        do
        {
            page = await Env.Service.PullAsync(t.B.Auth, t.Baby.BabyId, cursor, 100);
            Assert.Equal("DELTA", page.Mode);
            foreach (var c in page.Changes) ap.Apply(c);
            cursor = page.NextCursor;
        } while (page.HasMore);
        var server = await Env.ServerAliveAsync(t.Baby.BabyId);
        Assert.Equal(server.Keys, ap.Alive.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (k, v) in server) Assert.True(JsonNode.DeepEquals(v, ap.Alive[k].Json), k);
    }

    [Fact]
    public async Task Delta_collapses_repeated_changes_to_the_same_entity()
    {
        var t = await Trio.CreateAsync(Env);
        var s = t.A.Create("SLEEP_SESSION", Data.Sleep(T(0), T(30)));
        await t.SyncBothTwiceAsync();
        for (var i = 0; i < 50; i++) { t.A.Now = () => T(100 + i); t.A.Update("SLEEP_SESSION", s, FakeDevice.D(("notes", "v" + i))); }
        await t.A.SyncAsync();
        var page = await Env.Service.PullAsync(t.B.Auth, t.Baby.BabyId, t.B.Cursor);
        var ch = Assert.Single(page.Changes);                                    // 50 linhas no log, 1 mudança emitida
        Assert.Equal("v49", ch.Entity!["notes"]!.GetValue<string>());
        Assert.False(page.HasMore);
        // paginação por linhas do log: limite 10 => 5 páginas, ainda 1 mudança útil no total
        var total = 0; string? cur = t.B.Cursor; PullResponse p;
        do { p = await Env.Service.PullAsync(t.B.Auth, t.Baby.BabyId, cur, 10); total += p.Changes.Count; cur = p.NextCursor; } while (p.HasMore);
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task Fast_client_clock_is_clamped_to_server_receive_time_with_client_clock_policy()
    {
        var svc = Env.NewService(new SyncOptions { ConflictOrder = ConflictOrder.ClientClockClamped, ClockSkewWarnSeconds = 300 });
        var t = await Trio.CreateAsync(Env, svc: svc);
        t.A.Now = () => DateTimeOffset.UtcNow.AddMinutes(-5);
        var s = t.A.Create("SLEEP_SESSION", Data.Sleep(T(0), T(30), "NAP", "base"));
        await t.SyncBothTwiceAsync();
        t.A.Now = () => DateTimeOffset.UtcNow.AddHours(1);                      // relógio do aparelho A adiantado em 1 h
        t.A.Update("SLEEP_SESSION", s, FakeDevice.D(("notes", "A com relógio adiantado")));
        var ra = (await t.A.SyncAsync()).Results.Single();
        var w = Assert.Single(ra.Warnings!);
        Assert.Equal("CLIENT_CLOCK_SKEW", w.Code);
        Assert.InRange(w.SkewSeconds!.Value, 3500, 3700);
        Assert.Equal(300, w.ToleranceSeconds);
        Assert.Equal("APPLIED", ra.Status);                                      // aceita, só avisa
        await Task.Delay(50);
        t.B.Now = () => DateTimeOffset.UtcNow;                                   // B edita depois (hora real), com base antiga
        t.B.Update("SLEEP_SESSION", s, FakeDevice.D(("notes", "B depois")));
        var rb = (await t.B.SyncAsync()).Results.Single();
        Assert.Equal("LWW_CLIENT_WON", rb.Resolution);                           // sem o clamp, o +1h de A venceria B indevidamente
        await t.SyncBothTwiceAsync();
        Assert.Equal("B depois", Data.S(t.A.Get("SLEEP_SESSION", s), "notes"));
    }

    [Fact]
    public async Task Server_arrival_policy_ignores_client_clock_and_warns_beyond_tolerance()
    {
        var t = await Trio.CreateAsync(Env);                                      // padrão: ServerArrival, tolerância 86400 s
        var s = t.A.Create("SLEEP_SESSION", Data.Sleep(T(0), T(30), "NAP", "base"));
        await t.SyncBothTwiceAsync();
        t.A.Now = () => DateTimeOffset.UtcNow.AddHours(1);
        t.A.Update("SLEEP_SESSION", s, FakeDevice.D(("notes", "A +1h")));
        var ok = (await t.A.SyncAsync()).Results.Single();
        Assert.Null(ok.Warnings);                                                // 1 h < 24 h: sem aviso
        t.A.Now = () => DateTimeOffset.UtcNow.AddDays(-3);                       // -3 dias (offline longo ou relógio atrasado)
        t.A.Update("SLEEP_SESSION", s, FakeDevice.D(("method_or_place", "berço")));
        var late = (await t.A.SyncAsync()).Results.Single();
        var w = Assert.Single(late.Warnings!);
        Assert.Equal(("CLIENT_CLOCK_SKEW", 86_400), (w.Code, w.ToleranceSeconds));
        Assert.InRange(w.SkewSeconds!.Value, -3 * 86_400 - 5, -3 * 86_400 + 5);
        Assert.Equal("APPLIED", late.Status);
        // B edita depois com base antiga: vence por chegada, não importa o relógio de A (+1h)
        t.B.Update("SLEEP_SESSION", s, FakeDevice.D(("notes", "B chegou depois")));
        var rb = (await t.B.SyncAsync()).Results.Single();
        Assert.Equal("LWW_CLIENT_WON", rb.Resolution);
        await t.SyncBothTwiceAsync();
        Assert.Equal("B chegou depois", Data.S(t.A.Get("SLEEP_SESSION", s), "notes"));
    }
}
