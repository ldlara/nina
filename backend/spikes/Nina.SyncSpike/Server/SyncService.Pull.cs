using System.Data;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Nina.SyncSpike.Server;

public sealed partial class SyncService
{
    private sealed record LogRow(long Seq, string Type, Guid Id);

    private sealed record Current(long Version, DateTimeOffset? DeletedAt, JsonObject Json);

    /// <summary>
    /// Pull por bebê. Sem cursor = snapshot paginado (ponto de consistência H fixado na 1ª página); com cursor = delta
    /// por `sync_sequence > cursor`. Seguro sem "horizonte" porque a sequência é contígua e na ordem de commit.
    /// Roda em REPEATABLE READ: change_log, entidades e baby_sync_head vêm do mesmo snapshot.
    /// </summary>
    public async Task<PullResponse> PullAsync(AuthContext ctx, Guid babyId, string? cursor, int? limit = null, CancellationToken ct = default)
    {
        var n = limit ?? _opt.DefaultPullLimit;
        if (n < 1 || n > _opt.MaxPullLimit)
            throw new SyncProblemException(new Problem("VALIDATION_FAILED", 400, "limit must be 1..500", [new FieldError("limit", "RANGE")]));
        var now = clock.UtcNow;

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var env = new Env(conn, tx, ctx, babyId, now);
        await LoadEnvAsync(env, ct);
        switch (env.MembershipStatus)
        {
            case "ACTIVE": break;
            case "REVOKED":
                throw new SyncProblemException(new Problem("ACCESS_REVOKED", 403, "Access revoked"));
            default:
                throw new SyncProblemException(new Problem("BABY_NOT_FOUND", 404, "Baby not found"));
        }
        var epoch = CursorCodec.EpochOf(env.MembershipId!.Value);

        long last, purged;
        await using (var h = Cmd(env, "SELECT last_sequence, purged_through FROM nina_spike.sync_head(@b)"))
        {
            P(h, "b", babyId, NpgsqlDbType.Uuid);
            await using var r = await h.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new SyncProblemException(new Problem("BABY_NOT_FOUND", 404, "Baby not found"));
            last = r.GetInt64(0); purged = r.GetInt64(1);
        }

        CursorPayload? cp = null;
        if (cursor != null)
        {
            cp = cursors.TryDecode(cursor);
            if (cp == null || !cp.Babies.TryGetValue(babyId, out var e0) || e0.Epoch != epoch || e0.Seq > last)
                throw SyncProblemException.CursorExpired("INVALID");     // adulterado, de outro bebê, vínculo refeito ou "do futuro"
            if (now - DateTimeOffset.FromUnixTimeSeconds(cp.IssuedAtUnix) > TimeSpan.FromDays(_opt.RetentionDays) || e0.Seq < purged)
                throw SyncProblemException.CursorExpired("EXPIRED");
        }

        PullResponse resp;
        if (cp == null || cp.Snapshot != null)
            resp = await SnapshotPageAsync(env, babyId, cp, last, n, epoch, ct);
        else
            resp = await DeltaPageAsync(env, babyId, cp.Babies[babyId].Seq, n, epoch, ct);
        await tx.CommitAsync(ct);
        return resp;
    }

    private string Mint(Guid babyId, long seq, string epoch, SnapshotPosition? snap, DateTimeOffset now) =>
        cursors.Encode(new CursorPayload(now.ToUnixTimeSeconds(), new Dictionary<Guid, CursorEntry> { [babyId] = new(seq, epoch) }, snap));

    private async Task<PullResponse> DeltaPageAsync(Env env, Guid babyId, long from, int n, string epoch, CancellationToken ct)
    {
        var rows = new List<LogRow>();
        await using (var q = Cmd(env,
            "SELECT sync_sequence, entity_type, entity_id FROM nina.change_log WHERE baby_id = @b AND sync_sequence > @c ORDER BY sync_sequence LIMIT @n"))
        {
            P(q, "b", babyId, NpgsqlDbType.Uuid); P(q, "c", from, NpgsqlDbType.Bigint); P(q, "n", n + 1, NpgsqlDbType.Integer);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add(new LogRow(r.GetInt64(0), r.GetString(1), r.GetGuid(2)));
        }
        var hasMore = rows.Count > n;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var position = rows.Count > 0 ? rows[^1].Seq : from;

        var current = await LoadCurrentAsync(env, rows.Select(r => (r.Type, r.Id)).Distinct().ToList(), ct);
        var changes = new List<PullChange>();
        foreach (var row in rows)
        {
            if (!current.TryGetValue((row.Type, row.Id), out var c)) continue;      // purgada
            if (c.Version > row.Seq) continue;                                        // há mudança mais nova no log: ela emite
            changes.Add(ToChange(row.Type, row.Id, c));
        }
        return new PullResponse("DELTA", babyId, changes, hasMore, Mint(babyId, position, epoch, null, env.Now), env.Now);
    }

    private async Task<PullResponse> SnapshotPageAsync(Env env, Guid babyId, CursorPayload? cp, long head, int n, string epoch, CancellationToken ct)
    {
        // H (ponto de consistência) = last_sequence lido no 1º request; continuações carregam o mesmo H no cursor.
        var h = cp != null ? cp.Babies[babyId].Seq : head;
        var pos = cp?.Snapshot;
        // keyset por tipo, na ordem do feed: (rank, id) > posição. Cada consulta usa `id > @after ORDER BY id LIMIT` (uma só tabela).
        var rows = new List<(int Rank, string Type, Guid Id)>();
        foreach (var f in EntityCatalog.Feed)
        {
            var remaining = n + 1 - rows.Count;
            if (remaining <= 0) break;
            if (pos != null && f.Rank < pos.Rank) continue;
            await using var q = Cmd(env,
                $"SELECT id FROM nina.{f.Table} WHERE {f.BabyCol} = @b AND deleted_at IS NULL AND (@after IS NULL OR id > @after) ORDER BY id LIMIT @n");
            P(q, "b", babyId, NpgsqlDbType.Uuid);
            P(q, "after", pos != null && f.Rank == pos.Rank ? pos.Id : null, NpgsqlDbType.Uuid);
            P(q, "n", remaining, NpgsqlDbType.Integer);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add((f.Rank, f.LogType, r.GetGuid(0)));
        }
        var hasMore = rows.Count > n;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var current = await LoadCurrentAsync(env, rows.Select(r => (r.Type, r.Id)).ToList(), ct);
        var changes = rows.Where(r => current.ContainsKey((r.Type, r.Id))).Select(r => ToChange(r.Type, r.Id, current[(r.Type, r.Id)])).ToList();
        var next = hasMore
            ? Mint(babyId, h, epoch, new SnapshotPosition(rows[^1].Rank, rows[^1].Id), env.Now)
            : Mint(babyId, h, epoch, null, env.Now);       // fim do snapshot: o cursor continua em delta a partir de H
        return new PullResponse("SNAPSHOT", babyId, changes, hasMore, next, env.Now);
    }

    private static PullChange ToChange(string logType, Guid id, Current c)
    {
        var contract = EntityCatalog.Feed.First(f => f.LogType == logType).ContractType;
        return c.DeletedAt == null
            ? new PullChange("UPSERT", contract, id, c.Version, c.Json)
            : new PullChange("TOMBSTONE", contract, id, c.Version, null, c.DeletedAt);   // sem conteúdo (tombstone não vaza)
    }

    private static async Task<Dictionary<(string, Guid), Current>> LoadCurrentAsync(Env env, List<(string Type, Guid Id)> keys, CancellationToken ct)
    {
        var result = new Dictionary<(string, Guid), Current>();
        foreach (var g in keys.GroupBy(k => k.Type))
        {
            var feed = EntityCatalog.Feed.First(f => f.LogType == g.Key);
            await using var c = Cmd(env,
                $"SELECT id, version, deleted_at, to_jsonb(t) - 'created_by' - 'last_modified_by' FROM nina.{feed.Table} t WHERE id = ANY(@ids)");
            P(c, "ids", g.Select(k => k.Id).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                result[(g.Key, r.GetGuid(0))] = new Current(r.GetInt64(1), r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2),
                    JsonNode.Parse(r.GetFieldValue<string>(3))!.AsObject());
        }
        return result;
    }
}
