using System.Diagnostics;
using System.Globalization;
using System.Text;
using Nina.SyncSpike.Harness;
using Nina.SyncSpike.Server;
using Npgsql;

namespace Nina.SyncSpike;

/// <summary>Medições básicas de latência e throughput (mesma máquina do PostgreSQL, fsync ligado, pool de 120 conexões).</summary>
public static class Bench
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow.AddDays(-2);
    private static readonly StringBuilder Out = new();

    private static void W(string s = "") { Console.WriteLine(s); Out.AppendLine(s); }

    private sealed record Lat(double P50, double P95, double P99, double Max, double Mean, int N)
    {
        public static Lat Of(List<double> ms)
        {
            var s = ms.OrderBy(x => x).ToList();
            double Q(double q) => s[Math.Min(s.Count - 1, (int)Math.Ceiling(q * s.Count) - 1)];
            return new Lat(Q(.5), Q(.95), Q(.99), s[^1], s.Average(), s.Count);
        }
        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"n={N} média={Mean:F2} p50={P50:F2} p95={P95:F2} p99={P99:F2} máx={Max:F2} ms");
    }

    private static async Task<double> TimeAsync(Func<Task> f)
    {
        var sw = Stopwatch.StartNew();
        await f();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static Mutation Diaper(Guid baby, int i) =>
        new(Guid.NewGuid(), "CREATE", "DIAPER_EVENT", Guid.NewGuid(), baby, 0, T0.AddSeconds(i),
            FakeDevice.D(("occurred_at", T0.AddSeconds(i)), ("tz", "America/Sao_Paulo"), ("diaper_type", "WET"), ("notes", "n")));

    private sealed record Big(Guid User, Guid Baby);

    public static async Task<string> RunAsync(SpikeEnv env)
    {
        W($"# Bench ARCH-003  ({DateTime.UtcNow:u})");
        W($"PostgreSQL {env.Pg.Version}  | fsync=on | pool app=120 | CPUs={Environment.ProcessorCount} | .NET {Environment.Version}");
        W("Cliente e PostgreSQL na mesma máquina (loopback); latências incluem round trips, triggers, RLS e commit durável.");
        W();

        var u = await env.CreateUserAsync();
        var bigBaby = await env.CreateBabyAsync(u);
        await env.NonQueryAsync(
            "INSERT INTO nina.diaper_event (id, baby_id, occurred_at, tz, diaper_type, notes, created_by, last_modified_by) " +
            "SELECT gen_random_uuid(), @b, now() - make_interval(secs => g), 'UTC', 'WET', 'x', @u, @u FROM generate_series(1, 20000) g", ("b", bigBaby.BabyId), ("u", u));
        var big = new Big(u, bigBaby.BabyId);

        W("# FASE 1 - esquema 0001_init.sql + extras do spike (sem ajustes)");
        await PushSection(env, full: true);
        await PullSection(env, big);
        await DbCostSection(env);
        await ThroughputSection(env, new[] { 1, 2, 4, 8, 16 }, 6);
        await StorageSection(env);

        W();
        W("# FASE 2 - mesmo cenário depois de spike_tuning.sql (políticas RLS por conjunto + índices (baby_id,id))");
        await env.ApplyTuningAsync();
        await PushSection(env, full: false);
        await PullSection(env, big);
        await DbCostSection(env);
        await ThroughputSection(env, new[] { 1, 4, 16 }, 6);

        W();
        CursorSection(env);
        return Out.ToString();
    }

    // ---------------------------------------------------------------- A/B) push
    private static async Task PushSection(SpikeEnv env, bool full)
    {
        var u = await env.CreateUserAsync();
        var baby = await env.CreateBabyAsync(u);
        var dev = Guid.NewGuid(); var auth = new AuthContext(u, dev);
        for (var i = 0; i < 50; i++) await env.Service.PushAsync(auth, new PushRequest(dev, [Diaper(baby.BabyId, i)]));   // aquecimento
        var one = new List<double>();
        for (var i = 0; i < 400; i++) one.Add(await TimeAsync(() => env.Service.PushAsync(auth, new PushRequest(dev, [Diaper(baby.BabyId, i)]))));
        W("## Push de 1 mutação (CREATE, requisição sequencial, 1 bebê)");
        W(Lat.Of(one).ToString());

        var dm = Diaper(baby.BabyId, 9999);
        await env.Service.PushAsync(auth, new PushRequest(dev, [dm]));
        var dup = new List<double>();
        for (var i = 0; i < 300; i++) dup.Add(await TimeAsync(() => env.Service.PushAsync(auth, new PushRequest(dev, [dm]))));
        W("Reenvio idempotente (DUPLICATE, 1 mutação): " + Lat.Of(dup));

        var other = Guid.NewGuid(); var otherAuth = new AuthContext(u, other);
        var sid = Guid.NewGuid();
        await env.Service.PushAsync(auth, new PushRequest(dev, [new Mutation(Guid.NewGuid(), "CREATE", "SLEEP_SESSION", sid, baby.BabyId, 0, T0,
            FakeDevice.D(("sleep_type", "NAP"), ("start_at", T0), ("end_at", T0.AddMinutes(30)), ("tz", "UTC"), ("source", "MANUAL")))]));
        var plain = new List<double>(); var conflict = new List<double>();
        for (var i = 0; i < 300; i++)
        {
            var cur = await env.ScalarAsync<long>("SELECT version FROM nina.sleep_session WHERE id=@i", ("i", sid));
            plain.Add(await TimeAsync(() => env.Service.PushAsync(auth, new PushRequest(dev, [new Mutation(Guid.NewGuid(), "UPDATE", "SLEEP_SESSION", sid, baby.BabyId, cur, T0.AddMinutes(i), FakeDevice.D(("notes", "p" + i)))]))));
            conflict.Add(await TimeAsync(() => env.Service.PushAsync(otherAuth, new PushRequest(other, [new Mutation(Guid.NewGuid(), "UPDATE", "SLEEP_SESSION", sid, baby.BabyId, cur, T0.AddMinutes(i + 1), FakeDevice.D(("notes", "c" + i)))]))));
        }
        W("UPDATE sem conflito (base_version atual): " + Lat.Of(plain));
        W("UPDATE em conflito de campo (LWW + audit_event): " + Lat.Of(conflict));
        W();

        W("## Tamanho do lote e modo de transação (CREATE de fraldas, 1 bebê)");
        W("| modo | lote | requisições | latência/req p50 | p95 | ms/mutação (média) | mutações/s |");
        W("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var mode in full ? new[] { PushMode.PerBabyTransaction, PushMode.PerMutationTransaction } : new[] { PushMode.PerBabyTransaction })
        {
            var svc = env.NewService(new SyncOptions { Mode = mode });
            foreach (var size in new[] { 1, 10, 50, 100 })
            {
                var reqs = size == 1 ? 200 : Math.Max(10, 1000 / size);
                var lat = new List<double>();
                var sw = Stopwatch.StartNew();
                for (var r = 0; r < reqs; r++)
                {
                    var ms = Enumerable.Range(0, size).Select(i => Diaper(baby.BabyId, i)).ToList();
                    lat.Add(await TimeAsync(() => svc.PushAsync(auth, new PushRequest(dev, ms))));
                }
                var l = Lat.Of(lat);
                W(string.Create(CultureInfo.InvariantCulture, $"| {mode} | {size} | {reqs} | {l.P50:F1} ms | {l.P95:F1} ms | {l.Mean / size:F2} | {reqs * size / sw.Elapsed.TotalSeconds:F0} |"));
            }
        }
        W();
    }

    // ---------------------------------------------------------------- C) pull
    private static async Task PullSection(SpikeEnv env, Big big)
    {
        var auth = new AuthContext(big.User, Guid.NewGuid());
        W("## Pull (bebê com ~20 mil eventos)");
        var snapPages = 0; var snapRows = 0; string? cur2 = null; var sw2 = Stopwatch.StartNew(); var pageLat = new List<double>();
        while (true)
        {
            PullResponse p = null!;
            pageLat.Add(await TimeAsync(async () => p = await env.Service.PullAsync(auth, big.Baby, cur2, 500)));
            snapPages++; snapRows += p.Changes.Count; cur2 = p.NextCursor;
            if (!p.HasMore) break;
        }
        W($"Snapshot completo, limit=500: {snapPages} páginas, {snapRows} entidades em {sw2.Elapsed.TotalSeconds:F2} s; por página: {Lat.Of(pageLat)}");
        var endCursor = cur2!;
        var empty = new List<double>();
        for (var i = 0; i < 300; i++) empty.Add(await TimeAsync(() => env.Service.PullAsync(auth, big.Baby, endCursor, 200)));
        W("Delta vazio (cursor em dia): " + Lat.Of(empty));
        foreach (var n in new[] { 10, 200, 500 })
        {
            await env.NonQueryAsync(
                "INSERT INTO nina.diaper_event (id, baby_id, occurred_at, tz, diaper_type, created_by, last_modified_by) SELECT gen_random_uuid(), @b, now(), 'UTC', 'DRY', @u, @u FROM generate_series(1, @n)",
                ("b", big.Baby), ("u", big.User), ("n", n));
            var lat = new List<double>();
            for (var i = 0; i < 40; i++) lat.Add(await TimeAsync(() => env.Service.PullAsync(auth, big.Baby, endCursor, 500)));
            W($"Delta com {n} mudanças novas (limit=500): {Lat.Of(lat)}");
            endCursor = (await env.Service.PullAsync(auth, big.Baby, endCursor, 500)).NextCursor;
        }
        async Task<Lat> Timeline(NpgsqlDataSource ds, bool setUser)
        {
            var l = new List<double>();
            for (var i = 0; i < 150; i++)
                l.Add(await TimeAsync(async () =>
                {
                    await using var c = await ds.OpenConnectionAsync();
                    await using var tx = await c.BeginTransactionAsync();
                    if (setUser) await using (var s = new NpgsqlCommand($"SELECT set_config('nina.user_id','{big.User}',true)", c, tx)) await s.ExecuteNonQueryAsync();
                    await using var q = new NpgsqlCommand("SELECT e.id, to_jsonb(e) FROM nina.diaper_event e WHERE e.baby_id=@b AND e.deleted_at IS NULL ORDER BY e.occurred_at DESC LIMIT 500", c, tx);
                    q.Parameters.AddWithValue("b", big.Baby);
                    await using var r = await q.ExecuteReaderAsync();
                    while (await r.ReadAsync()) { }
                }));
            return Lat.Of(l);
        }
        W("Leitura de 500 linhas (timeline), RLS ligada (spike_app): " + await Timeline(env.App, true));
        W("Mesma leitura como superuser (RLS ignorada):              " + await Timeline(env.Super, false));
        W();
    }

    // ---------------------------------------------------------------- custo no banco por INSERT
    private static async Task DbCostSection(SpikeEnv env)
    {
        var u = await env.CreateUserAsync();
        var baby = await env.CreateBabyAsync(u);
        async Task<double> Run(NpgsqlDataSource ds, bool app)
        {
            await using var c = await ds.OpenConnectionAsync();
            await using var tx = await c.BeginTransactionAsync();
            if (app) await using (var s = new NpgsqlCommand($"SELECT set_config('nina.user_id','{u}',true)", c, tx)) await s.ExecuteNonQueryAsync();
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 500; i++)
            {
                await using var q = new NpgsqlCommand(
                    "INSERT INTO nina.diaper_event (id, baby_id, occurred_at, tz, diaper_type, created_by, last_modified_by) VALUES (gen_random_uuid(), @b, now(), 'UTC', 'WET', @u, @u)", c, tx);
                q.Parameters.AddWithValue("b", baby.BabyId); q.Parameters.AddWithValue("u", u);
                await q.ExecuteNonQueryAsync();
            }
            var ms = sw.Elapsed.TotalMilliseconds / 500;
            await tx.RollbackAsync();
            return ms;
        }
        W("## Custo no banco: 500 INSERTs de diaper_event em 1 transação (triggers de sync inclusos, sem commit)");
        var withRls = await Run(env.App, true);
        var noRls = await Run(env.Super, false);
        W(string.Create(CultureInfo.InvariantCulture, $"como spike_app (RLS + definer): {withRls:F2} ms/INSERT; como superuser (RLS ignorada): {noRls:F2} ms/INSERT"));
        W();
    }

    // ---------------------------------------------------------------- D) throughput
    private static async Task ThroughputSection(SpikeEnv env, int[] workerCounts, int seconds)
    {
        W($"## Throughput sob concorrência (lotes de 10 mutações; {seconds} s por configuração)");
        W("| cenário | workers | req/s | mutações/s | latência/req p50 | p95 | p99 | TRANSIENT | contíguo? |");
        W("|---|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var sameBaby in new[] { true, false })
        {
            foreach (var workers in workerCounts)
            {
                var users = new List<Guid>(); var babies = new List<BabyWorld>();
                if (sameBaby)
                {
                    var owner = await env.CreateUserAsync(); users.Add(owner);
                    for (var i = 1; i < workers; i++) users.Add(await env.CreateUserAsync());
                    var b = await env.CreateBabyAsync(owner, users.Skip(1).Select(x => (x, "CAREGIVER")).ToArray());
                    babies = Enumerable.Repeat(b, workers).ToList();
                }
                else
                {
                    for (var i = 0; i < workers; i++) { var us = await env.CreateUserAsync(); users.Add(us); babies.Add(await env.CreateBabyAsync(us)); }
                }
                var lat = new System.Collections.Concurrent.ConcurrentBag<double>();
                long muts = 0, transient = 0;
                var stop = Stopwatch.StartNew();
                var tasks = Enumerable.Range(0, workers).Select(w => Task.Run(async () =>
                {
                    var d = Guid.NewGuid(); var a = new AuthContext(users[w], d);
                    while (stop.Elapsed < TimeSpan.FromSeconds(seconds))
                    {
                        var ms = Enumerable.Range(0, 10).Select(i => Diaper(babies[w].BabyId, i)).ToList();
                        PushResponse? resp = null;
                        lat.Add(await TimeAsync(async () => resp = await env.Service.PushAsync(a, new PushRequest(d, ms))));
                        Interlocked.Add(ref muts, resp!.Results.Count(r => r.Status == "APPLIED"));
                        Interlocked.Add(ref transient, resp.Results.Count(r => r.Problem?.Code == "TRANSIENT"));
                    }
                })).ToArray();
                await Task.WhenAll(tasks);
                var secs = stop.Elapsed.TotalSeconds;
                var l = Lat.Of(lat.ToList());
                var contiguous = true;
                foreach (var b in babies.DistinctBy(x => x.BabyId))
                {
                    var st = await env.LogStatsAsync(b.BabyId);
                    contiguous &= st.Contiguous && st.Min == 1 && st.Count == st.Head && st.ChangedAtMonotonic;
                }
                W(string.Create(CultureInfo.InvariantCulture,
                    $"| {(sameBaby ? "mesmo bebê" : "bebê por worker")} | {workers} | {l.N / secs:F0} | {muts / secs:F0} | {l.P50:F1} ms | {l.P95:F1} ms | {l.P99:F1} ms | {transient} | {(contiguous ? "sim" : "NÃO")} |"));
            }
        }
        W();
    }

    // ---------------------------------------------------------------- armazenamento / cursor
    private static async Task StorageSection(SpikeEnv env)
    {
        W("## Armazenamento por linha (cluster de teste ao fim da fase 1)");
        W("| tabela | linhas | tamanho total (tabela+índices) | bytes/linha |");
        W("|---|---:|---:|---:|");
        foreach (var t in new[] { "nina.change_log", "nina.sync_mutation", "nina_spike.field_clock", "nina.diaper_event", "nina.sleep_session" })
        {
            var n = await env.ScalarAsync<long>($"SELECT count(*) FROM {t}");
            var sz = await env.ScalarAsync<long>($"SELECT pg_total_relation_size('{t}')");
            W($"| {t} | {n} | {sz / 1024.0 / 1024.0:F1} MiB | {(n == 0 ? 0 : sz / n)} |");
        }
        W();
    }

    private static void CursorSection(SpikeEnv env)
    {
        W("## Cursor (JSON compacto + HMAC-SHA256 truncado a 128 bits, base64url)");
        var codec = env.Service.Cursors;
        foreach (var n in new[] { 1, 2, 3, 5, 6, 8 })
        {
            var map = Enumerable.Range(0, n).ToDictionary(_ => Guid.NewGuid(), _ => new CursorEntry(1_234_567, CursorCodec.EpochOf(Guid.NewGuid())));
            W($"{n} bebê(s) no mapa: {codec.Encode(new CursorPayload(1_760_000_000, map)).Length} caracteres; com posição de snapshot: {codec.Encode(new CursorPayload(1_760_000_000, map, new SnapshotPosition(5, Guid.NewGuid()))).Length}");
        }
        var cur3 = codec.Encode(new CursorPayload(1_760_000_000, new Dictionary<Guid, CursorEntry> { [Guid.NewGuid()] = new(1_234_567, "abcdefgh") }));
        var swc = Stopwatch.StartNew();
        for (var i = 0; i < 100_000; i++) codec.TryDecode(cur3);
        W($"decodificar + validar MAC: {swc.Elapsed.TotalMilliseconds * 1000 / 100_000:F1} µs por cursor");
    }
}
