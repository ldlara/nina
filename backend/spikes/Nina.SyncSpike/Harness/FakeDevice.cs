using System.Text.Json;
using System.Text.Json.Nodes;
using Nina.SyncSpike.Server;

namespace Nina.SyncSpike.Harness;

public sealed class LocalEntity
{
    public string Type { get; init; } = "";
    public Guid Id { get; init; }
    public long Version { get; set; }
    public JsonObject Fields { get; set; } = new();
}

public sealed record SyncReport(int Pushed, IReadOnlyList<MutationResult> Results, int PulledChanges, int PullPages, bool Resynced, string? PullError);

/// <summary>
/// Cliente simulado (um dispositivo de um cuidador): banco local, fila de mutações offline, cursor,
/// e o procedimento de resync do contrato (preserva a fila, descarta o estado, snapshot, reenvia).
/// Fala com o SyncService em processo (sem HTTP), usando os mesmos tipos do contrato.
/// </summary>
public sealed class FakeDevice(string name, SyncService server, Guid userId, Guid babyId, Guid? deviceId = null)
{
    public string Name { get; } = name;
    public Guid UserId { get; } = userId;
    public Guid DeviceId { get; } = deviceId ?? Guid.NewGuid();
    public Guid BabyId { get; } = babyId;
    public bool Online { get; set; } = true;
    public string? Cursor { get; set; }
    public List<Mutation> Outbox { get; } = [];
    public Dictionary<(string, Guid), LocalEntity> Local { get; private set; } = [];
    public HashSet<(string, Guid)> LocallyDeleted { get; } = [];
    public List<(Mutation Mutation, MutationResult Result)> History { get; } = [];
    public List<PullChange> PulledHistory { get; } = [];
    public int FullResyncs { get; private set; }
    public bool Revoked { get; private set; }
    public int BatchSize { get; set; } = 100;
    public int PullLimit { get; set; } = 200;

    /// <summary>Relógio do aparelho (testes fixam/avançam para controlar o LWW por campo).</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public AuthContext Auth => new(UserId, DeviceId);

    public static JsonElement D(params (string Key, object? Value)[] kv) =>
        Json.ToElement(kv.ToDictionary(x => x.Key, x => x.Value));

    // ------------------------------------------------------------------ intenções do usuário (offline-first)

    public Guid Create(string type, JsonElement data, Guid? id = null)
    {
        var eid = id ?? Guid.NewGuid();
        Enqueue(new Mutation(Guid.NewGuid(), "CREATE", type, eid, BabyId, 0, Now(), data));
        return eid;
    }

    public void Update(string type, Guid id, JsonElement patch) =>
        Enqueue(new Mutation(Guid.NewGuid(), "UPDATE", type, id, BabyId, KnownVersion(type, id), Now(), patch));

    public void Delete(string type, Guid id) =>
        Enqueue(new Mutation(Guid.NewGuid(), "DELETE", type, id, BabyId, KnownVersion(type, id), Now(), null));

    private long KnownVersion(string type, Guid id) => Local.TryGetValue((type, id), out var e) ? e.Version : 0;

    private void Enqueue(Mutation m)
    {
        Outbox.Add(m);
        ApplyOptimistic(m);
    }

    private void ApplyOptimistic(Mutation m)
    {
        var key = (m.EntityType, m.EntityId);
        switch (m.Op)
        {
            case "CREATE":
                Local[key] = new LocalEntity { Type = m.EntityType, Id = m.EntityId, Version = 0, Fields = JsonNode.Parse(m.Data!.Value.GetRawText())!.AsObject() };
                break;
            case "UPDATE" when Local.TryGetValue(key, out var e):
                foreach (var p in JsonNode.Parse(m.Data!.Value.GetRawText())!.AsObject().ToList()) e.Fields[p.Key] = p.Value?.DeepClone();
                break;
            case "DELETE":
                Local.Remove(key);
                LocallyDeleted.Add(key);
                break;
        }
    }

    // ------------------------------------------------------------------ sincronização

    public async Task<SyncReport> SyncAsync(bool dropFirstPushResponse = false, CancellationToken ct = default)
    {
        if (!Online) return new SyncReport(0, [], 0, 0, false, "OFFLINE");
        var (pushed, results) = await PushAsync(dropFirstPushResponse, ct);
        var (changes, pages, resynced, err) = await PullAsync(ct);
        if (resynced && Outbox.Count > 0)   // passo 4 do resync: reenviar as pendentes (idempotente)
        {
            var (p2, r2) = await PushAsync(false, ct);
            pushed += p2; results = results.Concat(r2).ToList();
            var (c2, pg2, _, e2) = await PullAsync(ct);
            changes += c2; pages += pg2; err ??= e2;
        }
        return new SyncReport(pushed, results, changes, pages, resynced, err);
    }

    public async Task<(int Pushed, List<MutationResult> Results)> PushAsync(bool dropFirstResponse = false, CancellationToken ct = default)
    {
        var all = new List<MutationResult>();
        var pushed = 0;
        var dropped = false;
        while (Outbox.Count > 0 && !Revoked)
        {
            var batch = Outbox.Take(BatchSize).ToList();
            PushResponse resp;
            try
            {
                resp = await server.PushAsync(Auth, new PushRequest(DeviceId, batch), null, ct);
            }
            catch (SyncProblemException) { break; }
            pushed += batch.Count;
            if (dropFirstResponse && !dropped)
            {
                dropped = true;      // a requisição chegou ao servidor, mas a resposta se perdeu: a fila fica intacta e será reenviada
                break;
            }
            for (var i = 0; i < batch.Count; i++)
            {
                var r = resp.Results[i];
                History.Add((batch[i], r));
                all.Add(r);
                if (r is { Status: "REJECTED", Retryable: true }) continue;
                Outbox.Remove(batch[i]);
                HandleResult(batch[i], r);
            }
            if (Outbox.Count > 0 && batch.All(b => Outbox.Contains(b))) break;   // nada andou
        }
        return (pushed, all);
    }

    private void HandleResult(Mutation m, MutationResult r)
    {
        var key = (m.EntityType, m.EntityId);
        if (r.Status == "REJECTED")
        {
            if (r.Problem?.Code == "ACCESS_REVOKED") { WipeLocal(); return; }
            if (m.Op == "CREATE") Local.Remove(key);     // criação recusada: some do local; edição recusada converge no próximo pull
            return;
        }
        if (Local.TryGetValue(key, out var e))
        {
            if (r.Version is { } v && v > e.Version) e.Version = v;
            if (r.Entity != null) { e.Fields = (JsonObject)r.Entity.DeepClone(); e.Version = r.Entity["version"]!.GetValue<long>(); }
        }
    }

    private void WipeLocal()
    {
        Revoked = true;
        Local.Clear(); Outbox.Clear(); Cursor = null;
    }

    public async Task<(int Changes, int Pages, bool Resynced, string? Error)> PullAsync(CancellationToken ct = default)
    {
        var changes = 0; var pages = 0; var resynced = false;
        while (true)
        {
            PullResponse resp;
            try
            {
                resp = await server.PullAsync(Auth, BabyId, Cursor, PullLimit, ct);
            }
            catch (SyncProblemException p) when (p.Problem.Code == "SYNC_CURSOR_EXPIRED")
            {
                // (1) fila preservada; (2) descarta o estado sincronizado; (3) snapshot sem cursor; (4) reenvio pelo SyncAsync
                Local = []; LocallyDeleted.Clear(); Cursor = null; FullResyncs++; resynced = true;
                continue;
            }
            catch (SyncProblemException p) when (p.Problem.Code == "ACCESS_REVOKED")
            {
                WipeLocal();
                return (changes, pages, resynced, "ACCESS_REVOKED");
            }
            catch (SyncProblemException p)
            {
                return (changes, pages, resynced, p.Problem.Code);
            }
            pages++;
            foreach (var c in resp.Changes) { Apply(c); changes++; PulledHistory.Add(c); }
            Cursor = resp.NextCursor;
            if (!resp.HasMore) break;
        }
        if (resynced) foreach (var m in Outbox) ApplyOptimistic(m);
        return (changes, pages, resynced, null);
    }

    private void Apply(PullChange c)
    {
        var key = (c.EntityType, c.EntityId);
        if (c.Op == "TOMBSTONE")
        {
            if (!Local.TryGetValue(key, out var e) || c.Version >= e.Version) Local.Remove(key);
            LocallyDeleted.Add(key);
            return;
        }
        if (Local.TryGetValue(key, out var cur) && c.Version < cur.Version) return;   // idempotente por version (descarta mais velho)
        Local[key] = new LocalEntity { Type = c.EntityType, Id = c.EntityId, Version = c.Version, Fields = (JsonObject)c.Entity!.DeepClone() };
        LocallyDeleted.Remove(key);
        foreach (var m in Outbox.Where(o => o.EntityId == c.EntityId && o.Op == "UPDATE")) ApplyOptimistic(m);   // edições pendentes continuam visíveis
    }

    // ------------------------------------------------------------------ inspeção

    public SortedDictionary<string, JsonObject> View() =>
        new(Local.ToDictionary(kv => $"{kv.Key.Item1}:{kv.Key.Item2}", kv => kv.Value.Fields), StringComparer.Ordinal);

    public JsonObject? Get(string type, Guid id) => Local.TryGetValue((type, id), out var e) ? e.Fields : null;

    public static bool Equal(SortedDictionary<string, JsonObject> a, SortedDictionary<string, JsonObject> b, out string diff)
    {
        diff = "";
        foreach (var k in a.Keys.Union(b.Keys))
        {
            if (!a.TryGetValue(k, out var x)) { diff = $"{k}: ausente no primeiro"; return false; }
            if (!b.TryGetValue(k, out var y)) { diff = $"{k}: ausente no segundo"; return false; }
            if (!JsonNode.DeepEquals(x, y)) { diff = $"{k}: {x.ToJsonString()} != {y.ToJsonString()}"; return false; }
        }
        return true;
    }
}
