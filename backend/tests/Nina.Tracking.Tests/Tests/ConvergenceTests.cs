using System.Text.Json.Nodes;
using Nina.Tracking.Tests.Infrastructure;

namespace Nina.Tracking.Tests.Tests;

/// <summary>RF-047-A7: convergência de aparelhos offline e sequência contígua do bebê sob concorrência (ADR-0003).</summary>
public sealed class ConvergenceTests(PostgresFixture postgres) : TrackingTestBase(postgres)
{
    /// <summary>Aparelho simulado: banco local por versão, fila offline, cursor e a rotina de reconciliação do contrato.</summary>
    private sealed class FakeDevice(ConvergenceTests t, Session session, Guid baby)
    {
        public Dictionary<Guid, (long Version, JsonNode? Entity)> Local { get; } = [];

        public List<JsonObject> Queue { get; } = [];

        private string? _cursor;

        public async Task PullAsync()
        {
            var (changes, cursor) = await t.PullAllAsync(session, baby, _cursor, 50);
            _cursor = cursor;
            foreach (var c in changes)
            {
                var id = c.EntityId();
                var version = c["version"]!.GetValue<long>();
                if (!Local.TryGetValue(id, out var current) || version > current.Version)
                {
                    Local[id] = (version, c.Op() == "UPSERT" ? c["entity"]!.DeepClone() : null);
                }
            }
        }

        public async Task PushAsync()
        {
            var pending = Queue.ToList();
            Queue.Clear();
            for (var i = 0; i < pending.Count; i += 50)
            {
                var batch = pending.Skip(i).Take(50).ToArray();
                var results = await t.PushOkAsync(session, batch);
                foreach (var r in results)
                {
                    if (r!["entity"] is { } entity && r["entity_id"] is { } id)
                    {
                        Local[Guid.Parse(id.GetValue<string>())] = (r.Version(), entity.DeepClone());
                    }
                }
            }
        }

        public string Fingerprint() => string.Join(
            "|",
            Local.Where(kv => kv.Value.Entity is not null && kv.Value.Entity["event_type"] is not null)
                .OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key}:{kv.Value.Version}:{kv.Value.Entity!["notes"]}:{kv.Value.Entity["diaper_type"]}"));
    }

    [Fact]
    public async Task Two_devices_that_edited_offline_converge_to_the_server_state()
    {
        var ana = await UserAsync();
        var bia = await UserAsync();
        var baby = await BabyAsync(ana);
        await MemberAsync(baby, bia, "CAREGIVER");
        var phoneA = new FakeDevice(this, ana, baby);
        var phoneB = new FakeDevice(this, bia, baby);

        // estado inicial compartilhado
        var shared = Guid.NewGuid();
        var doomed = Guid.NewGuid();
        phoneA.Queue.Add(Mut.Create(Mut.Diaper, baby, shared, Mut.DiaperData(T0, "WET", "base")));
        phoneA.Queue.Add(Mut.Create(Mut.Diaper, baby, doomed, Mut.DiaperData(T0.AddMinutes(1))));
        await phoneA.PushAsync();
        await phoneA.PullAsync();
        await phoneB.PullAsync();
        var v = phoneB.Local[shared].Version;
        var vDoomed = phoneB.Local[doomed].Version;

        // ambos ficam offline: criam eventos próprios, editam o MESMO campo e campos distintos e um exclui o que o outro edita
        var ownA = Guid.NewGuid();
        var ownB = Guid.NewGuid();
        phoneA.Queue.AddRange([
            Mut.Create(Mut.Sleep, baby, ownA, Mut.SleepData(T0.AddHours(1), T0.AddHours(2))),
            Mut.Update(Mut.Diaper, baby, shared, v, new JsonObject { ["notes"] = "nota do A" }),
            Mut.Update(Mut.Diaper, baby, doomed, vDoomed, new JsonObject { ["notes"] = "A edita o que B apaga" }),
        ]);
        phoneB.Queue.AddRange([
            Mut.Create(Mut.Sleep, baby, ownB, Mut.SleepData(T0.AddHours(1).AddMinutes(30), T0.AddHours(3))),        // sobrepõe o do A: KEPT_BOTH
            Mut.Update(Mut.Diaper, baby, shared, v, new JsonObject { ["notes"] = "nota do B", ["diaper_type"] = "DIRTY" }),
            Mut.Delete(Mut.Diaper, baby, doomed, vDoomed),
        ]);

        // reconectam em ordens entrelaçadas e repetem a reconciliação até estabilizar
        await phoneB.PushAsync();
        await phoneA.PushAsync();
        for (var round = 0; round < 2; round++)
        {
            await phoneA.PullAsync();
            await phoneB.PullAsync();
        }

        Assert.Equal(phoneA.Fingerprint(), phoneB.Fingerprint());
        Assert.Equal("nota do A", await AdminScalarAsync<string>($"SELECT notes FROM nina.diaper_event WHERE id = '{shared}'"));     // A chegou por último
        Assert.Equal("DIRTY", await AdminScalarAsync<string>($"SELECT diaper_type FROM nina.diaper_event WHERE id = '{shared}'"));    // campo disjunto do B preservado
        Assert.Equal(2L, await AdminScalarAsync<long>($"SELECT count(*) FROM nina.sleep_session WHERE baby_id = '{baby}'"));
        Assert.Null(phoneA.Local[doomed].Entity);                                                                                      // tombstone nos dois
        Assert.Null(phoneB.Local[doomed].Entity);
        Assert.NotNull(await AdminScalarAsync<DateTime?>($"SELECT deleted_at FROM nina.diaper_event WHERE id = '{doomed}'"));
        foreach (var row in await AdminRowsAsync($"SELECT id, version FROM nina.diaper_event WHERE baby_id = '{baby}'"))
        {
            Assert.Equal((long)row[1]!, phoneA.Local[(Guid)row[0]!].Version);
            Assert.Equal((long)row[1]!, phoneB.Local[(Guid)row[0]!].Version);
        }
    }

    [Fact]
    public async Task The_baby_sequence_stays_contiguous_under_concurrent_writers_with_rejections_and_a_live_reader()
    {
        var owner = await UserAsync();
        var baby = await BabyAsync(owner);
        var writers = new List<Session> { owner };
        for (var i = 0; i < 3; i++)
        {
            var user = await UserAsync();
            await MemberAsync(baby, user, "CAREGIVER");
            writers.Add(user);
            writers.Add(await SecondDeviceAsync(user));
        }

        var reading = true;
        var violations = new List<string>();
        var reader = Task.Run(async () =>
        {
            string? cursor = null;
            long last = 0;
            while (reading)
            {
                var page = await PullAsync(owner, baby, cursor, 500);
                cursor = page.Json!["next_cursor"]!.GetValue<string>();
                foreach (var c in page.Json["changes"]!.AsArray())
                {
                    last = Math.Max(last, c!["version"]!.GetValue<long>());
                }

                await Task.Delay(5);
            }
        });

        const int batches = 6;
        const int perBatch = 10;
        var applied = 0;
        await Task.WhenAll(writers.Select(async (w, n) =>
        {
            for (var b = 0; b < batches; b++)
            {
                var mutations = Enumerable.Range(0, perBatch).Select(i => i == 3
                    ? Mut.Create(Mut.Feeding, baby, Guid.NewGuid(), new JsonObject { ["feeding_type"] = "BOTTLE" })        // rejeitada: não pode deixar lacuna
                    : Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0.AddSeconds((n * 1000) + (b * 10) + i)))).ToArray();
                var results = await PushOkAsync(w, mutations);
                Interlocked.Add(ref applied, results.Count(r => r.Status() == "APPLIED"));
            }
        }));
        reading = false;
        await reader;

        var seqs = (await AdminRowsAsync($"SELECT sync_sequence FROM nina.change_log WHERE baby_id = '{baby}' ORDER BY 1")).Select(r => (long)r[0]!).ToArray();
        Assert.Equal(Enumerable.Range(1, seqs.Length).Select(i => (long)i), seqs);
        Assert.Equal(seqs.Length, await AdminScalarAsync<long>($"SELECT last_sequence FROM nina.baby_sync_head WHERE baby_id = '{baby}'"));
        Assert.Equal(1 + applied, seqs.Length);                                                             // perfil + cada mutação aplicada
        Assert.Equal(applied, (int)await AdminScalarAsync<long>($"SELECT count(DISTINCT version) FROM nina.diaper_event WHERE baby_id = '{baby}'"));
        Assert.Empty(violations);
    }
}
