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

    public static async Task<string> RunAsync(SpikeEnv env)
    {
        W($"# Bench ARCH-003  ({DateTime.UtcNow:u})");
        W($"PostgreSQL {env.Pg.Version}  | fsync=on | pool app=120 | CPUs={Environment.ProcessorCount} | .NET {Environment.Version}");
        W();

        // ---------------------------------------------------------------- A) push, 1 mutação
        var u = await env.CreateUserAsync();
        var baby = await env.CreateBabyAsync(u);
        var dev = Guid.NewGuid(); var auth = new AuthContext(u, dev);
        for (var i = 0; i < 50; i++) await env.Service.PushAsync(auth, new PushRequest(dev, [Diaper(baby.BabyId, i)]));   // aquecimento
        var one = new List<double>();
        for (var i = 0; i < 500; i++) one.Add(await TimeAsync(() => env.Service.PushAsync(auth, new PushRequest(dev, [Diaper(baby.BabyId, i)]))));
        W("## A. Push de 1 mutação (CREATE, requisição sequencial)");
        W(Lat.Of(one).ToString());

        // duplicata (reenvio)
        var dm = Diaper(baby.BabyId, 9999);
        await env.Service.PushAsync(auth, new PushRequest(dev, [dm]));
        var dup = new List<double>();
        for (var i = 0; i < 300; i++) dup.Add(await TimeAsync(() => env.Service.PushAsync(auth, new PushRequest(dev, [dm]))));
        W("Reenvio idempotente (DUPLICATE, 1 mutação): " + Lat.Of(dup));

        // update sem conflito x com conflito
        var other = Guid.NewGuid(); var otherAuth = new AuthContext(u, other);
        var sid = Guid.NewGuid();
        await env.Service.PushAsync(auth, new PushRequest(dev, [new Mutation(Guid.NewGuid(), "CREATE", "SLEEP_SESSION", sid, baby.BabyId, 0, T0, FakeDevice.D(("sleep_type", "NAP"), ("start_at", T0), ("end_at", T0.AddMinutes(30)), ("tz", "UTC"), ("source", "MANUAL")))]));
        var plain = new List<double>(); var conflict = new List<double>();
        for (var i = 0; i < 300; i++)
        {
            var cur = await env.ScalarAsync<long>("SELECT version FROM nina.sleep_session WHERE id=@i", ("i", sid));
            plain.Add(await TimeAsync(() => env.Service.PushAsync(auth, new PushRequest(dev, [new Mutation(Guid.NewGuid(), "UPDATE", "SLEEP_SESSION", sid, baby.BabyId, cur, T0.AddMinutes(i), FakeDevice.D(("notes", "p" + i)))]))));
            conflict.Add(await TimeAsync(() => env.Service.PushAsync(otherAuth, new PushRequest(other, [new Mutation(Guid.NewGuid(), "UPDATE", "SLEEP_SESSION", sid, baby.BabyId, cur, T0.AddMinutes(i + 1), FakeDevice.D(("notes", "c" + i)))]))));
        }
        W("UPDATE sem conflito (base_version atual): " + Lat.Of(plain));
        W("UPDATE em conflito de campo (LWW + audit_event):  " + Lat.Of(conflict));
        W();

        // ---------------------------------------------------------------- B) tamanho do lote e modo
        W("## B. Tamanho do lote (CREATE de fraldas, 1 bebê) e modo de transação");
        W("| modo | lote | requisições | latência/req p50 | p95 | ms/mutação (média) | mutações/s |");
        W("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var mode in new[] { PushMode.PerBabyTransaction, PushMode.PerMutationTransaction })
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
                var total = sw.Elapsed.TotalSeconds;
                var l = Lat.Of(lat);
                W(string.Create(CultureInfo.InvariantCulture, $"| {mode} | {size} | {reqs} | {l.P50:F1} ms | {l.P95:F1} ms | {l.Mean / size:F2} | {reqs * size / total:F0} |"));
            }
        }
        W();

        // ---------------------------------------------------------------- C) pull
        W("## C. Pull");
        var big = await env.CreateBabyAsync(u);
        await env.NonQueryAsync(
            "INSERT INTO nina.diaper_event (id, baby_id, occurred_at, tz, diaper_type, notes, created_by, last_modified_by) " +
            "SELECT gen_random_uuid(), @b, now() - make_interval(secs => g), 'UTC', 'WET', 'x', @u, @u FROM generate_series(1, 20000) g", ("b", big.BabyId), ("u", u));
        var head = (await env.LogStatsAsync(big.BabyId)).Head;
        W($"Bebê de teste: {head} linhas em change_log (20.000 fraldas + bebê).");
        var snapPages = 0; var snapRows = 0; string? cur2 = null; var sw2 = Stopwatch.StartNew(); var pageLat = new List<double>();
        while (true)
        {
            PullResponse p = null!;
            pageLat.Add(await TimeAsync(async () => p = await env.Service.PullAsync(auth, big.BabyId, cur2, 500)));
            snapPages++; snapRows += p.Changes.Count; cur2 = p.NextCursor;
            if (!p.HasMore) break;
        }
        W($"Snapshot completo, limit=500: {snapPages} páginas, {snapRows} entidades em {sw2.Elapsed.TotalSeconds:F2} s; por página: {Lat.Of(pageLat)}");
        var endCursor = cur2!;
        var empty = new List<double>();
        for (var i = 0; i < 300; i++) empty.Add(await TimeAsync(() => env.Service.PullAsync(auth, big.BabyId, endCursor, 200)));
        W("Delta vazio (cursor em dia): " + Lat.Of(empty));
        // gera 200 / 500 mudanças e mede delta
        foreach (var n in new[] { 10, 200, 500 })
        {
            await env.NonQueryAsync(
                "INSERT INTO nina.diaper_event (id, baby_id, occurred_at, tz, diaper_type, created_by, last_modified_by) SELECT gen_random_uuid(), @b, now(), 'UTC', 'DRY', @u, @u FROM generate_series(1, @n)",
                ("b", big.BabyId), ("u", u), ("n", n));
            var lat = new List<double>();
            for (var i = 0; i < 50; i++) lat.Add(await TimeAsync(() => env.Service.PullAsync(auth, big.BabyId, endCursor, Math.Max(n, 1) > 500 ? 500 : 500)));
            W($"Delta com {n} mudanças novas (limit=500): {Lat.Of(lat)}");
            var pp = await env.Service.PullAsync(auth, big.BabyId, endCursor, 500);
            endCursor = pp.NextCursor;
        }

        // RLS: o mesmo SELECT do delta como spike_app (RLS) e como superuser (bypass)
        async Task<Lat> Sql(NpgsqlDataSource ds, bool setUser)
        {
            var l = new List<double>();
            for (var i = 0; i < 200; i++)
            {
                l.Add(await TimeAsync(async () =>
                {
                    await using var c = await ds.OpenConnectionAsync();
                    await using var tx = await c.BeginTransactionAsync();
                    if (setUser) await using (var s = new NpgsqlCommand($"SELECT set_config('nina.user_id','{u}',true)", c, tx)) await s.ExecuteNonQueryAsync();
                    await using var q = new NpgsqlCommand("SELECT e.id, to_jsonb(e) FROM nina.diaper_event e WHERE e.baby_id=@b AND e.deleted_at IS NULL ORDER BY e.occurred_at DESC LIMIT 500", c, tx);
                    q.Parameters.AddWithValue("b", big.BabyId);
                    await using var r = await q.ExecuteReaderAsync();
                    while (await r.ReadAsync()) { }
                }));
            }
            return Lat.Of(l);
        }
        W("Leitura de 500 linhas de diaper_event (timeline), RLS ligada (spike_app): " + await Sql(env.App, true));
        W("Mesma leitura como superuser (RLS ignorada):                              " + await Sql(env.Super, false));
        W();

        // ---------------------------------------------------------------- D) throughput
        W("## D. Throughput sob concorrência (lotes de 10 mutações; 6 s por configuração)");
        W("| cenário | workers | req/s | mutações/s | latência/req p50 | p95 | p99 | TRANSIENT | contíguo? |");
        W("|---|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var sameBaby in new[] { true, false })
        {
            foreach (var workers in new[] { 1, 2, 4, 8, 16 })
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
                    while (stop.Elapsed < TimeSpan.FromSeconds(6))
                    {
                        var ms = Enumerable.Range(0, 10).Select(i => Diaper(babies[w].BabyId, i)).ToList();
                        PushResponse? resp = null;
                        var t = await TimeAsync(async () => resp = await env.Service.PushAsync(a, new PushRequest(d, ms)));
                        lat.Add(t);
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

        // ---------------------------------------------------------------- E) armazenamento / cursor
        W("## E. Armazenamento por mutação (bebê de teste do item A/B)");
        var rows = await env.ScalarAsync<long>("SELECT count(*) FROM nina.change_log WHERE baby_id=@b", ("b", baby.BabyId));
        var muts2 = await env.ScalarAsync<long>("SELECT count(*) FROM nina.sync_mutation WHERE baby_id=@b", ("b", baby.BabyId));
        W($"change_log: {rows} linhas; sync_mutation: {muts2} linhas;");
        W("pg_total_relation_size (bytes totais da tabela, global do cluster de teste): " + await env.ScalarAsync<string>(
            "SELECT string_agg(relname || '=' || pg_total_relation_size(c.oid), ', ') FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE (n.nspname='nina' AND relname IN ('change_log','sync_mutation','tombstone','diaper_event','baby_sync_head')) OR (n.nspname='nina_spike' AND relname='field_clock')"));
        W();
        W("## F. Cursor (JSON compacto + HMAC-128)");
        var codec = env.Service.Cursors;
        foreach (var n in new[] { 1, 2, 3, 5, 8 })
        {
            var map = Enumerable.Range(0, n).ToDictionary(_ => Guid.NewGuid(), _ => new CursorEntry(1_234_567, CursorCodec.EpochOf(Guid.NewGuid())));
            W($"{n} bebê(s) no mapa: {codec.Encode(new CursorPayload(1_760_000_000, map)).Length} caracteres; com posição de snapshot: {codec.Encode(new CursorPayload(1_760_000_000, map, new SnapshotPosition(5, Guid.NewGuid()))).Length}");
        }
        var cur3 = codec.Encode(new CursorPayload(1_760_000_000, new Dictionary<Guid, CursorEntry> { [Guid.NewGuid()] = new(1_234_567, "abcdefgh") }));
        var swc = Stopwatch.StartNew();
        for (var i = 0; i < 100_000; i++) codec.TryDecode(cur3);
        W($"decodificar+validar MAC: {swc.Elapsed.TotalMilliseconds * 1000 / 100_000:F1} µs por cursor");
        return Out.ToString();
    }
}
