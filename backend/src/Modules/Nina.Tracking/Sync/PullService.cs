using System.Data;
using System.Text.Json.Nodes;
using Nina.SharedKernel.Data;
using Nina.SharedKernel.Http;
using Nina.Tracking.Domain;
using Nina.Tracking.Persistence;
using Nina.Tracking.Reads;
using Npgsql;
using NpgsqlTypes;

namespace Nina.Tracking.Sync;

/// <summary>
/// <c>GET /sync/pull</c> (BE-004b, ADR-0003). Uma transação <c>REPEATABLE READ</c> por página: o ponto de consistência (sequência do bebê)
/// e as linhas lidas vêm do mesmo snapshot do banco. Sem cursor: snapshot paginado por keyset (rank do tipo, id) com a sequência <c>H</c>
/// fixada na primeira página e carregada no cursor; o último <c>next_cursor</c> do snapshot continua em delta a partir de <c>H</c>. Com
/// cursor: delta pelo <c>change_log</c> (sem conteúdo) colapsando linhas superadas, com tombstones no feed. A sequência por bebê é contígua
/// e em ordem de commit, então não é preciso horizonte de segurança.
/// </summary>
internal sealed class PullService(NpgsqlDataSource dataSource, TimeProvider time, CursorCodec codec)
{
    private const string PreferencesFeedType = "SLEEP_PREFERENCES";
    private static readonly int[] SnapshotRanks = [0, 1, 2, 3, 4, 5, 6];
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    private sealed record Head(long Last, long Purged);

    public async Task<JsonObject> PullAsync(Guid userId, Guid babyId, string? cursor, int limit, CancellationToken ct)
    {
        await using var tx = await TrackingTx.BeginAsync(dataSource, userId, null, IsolationLevel.RepeatableRead, ct);
        var access = await BabyAccess.ResolveAsync(tx, babyId);
        access.RequireRead();

        var head = await tx.QueryFirstAsync(
            "SELECT last_sequence, purged_through FROM nina.sync_head(@baby)",
            r => new Head(r.GetInt64(0), r.GetInt64(1)),
            Db.Uuid("baby", babyId)) ?? throw ProblemException.NotFound();
        var epoch = CursorCodec.EpochOf(access.MembershipId!.Value);
        var now = time.GetUtcNow();

        CursorPayload? payload = null;
        CursorEntry? entry = null;
        if (cursor is not null)
        {
            payload = codec.TryDecode(cursor) ?? throw Gone("INVALID");
            if (payload.Babies.Count != 1 || !payload.Babies.TryGetValue(babyId, out entry)
                || !string.Equals(entry.Epoch, epoch, StringComparison.Ordinal)
                || entry.Sequence < 0 || entry.Sequence > head.Last                       // do futuro (backup restaurado ou cursor forjado)
                || payload.IssuedAtUnix > now.ToUnixTimeSeconds() + ClockTolerance.TotalSeconds)
            {
                throw Gone("INVALID");
            }

            var retentionDays = await tx.ScalarAsync<int>("SELECT nina.param_int('sync.changelog_retention_days', 90)");
            if (now.ToUnixTimeSeconds() - payload.IssuedAtUnix > retentionDays * 86_400L || entry.Sequence < head.Purged)
            {
                throw Gone("EXPIRED");
            }
        }

        var authors = await BabyAccess.LoadAuthorsAsync(tx, babyId);
        var context = new Context(tx, babyId, access.Role!, authors);
        var (mode, changes, hasMore, nextPayload) = payload is { Snapshot: null } && entry is not null
            ? await DeltaAsync(context, entry, head, limit, epoch, now)
            : await SnapshotAsync(context, payload, entry, head, limit, epoch, now);

        var array = new JsonArray();
        foreach (var change in changes)
        {
            array.Add(change);
        }

        return new JsonObject
        {
            ["mode"] = mode,
            ["baby_id"] = babyId.ToString("D"),
            ["changes"] = array,
            ["has_more"] = hasMore,
            ["next_cursor"] = codec.Encode(nextPayload),
            ["server_time"] = Wire.Instant(now),
        };
    }

    private static ProblemException Gone(string reason) =>
        new(StatusCodes.Status410Gone, "SYNC_CURSOR_EXPIRED", "Sync cursor expired")
        {
            Extensions = new Dictionary<string, object?> { ["reason"] = reason, ["resync_required"] = true },
        };

    private sealed record Context(TrackingTx Tx, Guid BabyId, string Role, Authors Authors);

    // ---------------------------------------------------------------- snapshot

    private static async Task<(string Mode, List<JsonObject> Changes, bool HasMore, CursorPayload Next)> SnapshotAsync(
        Context c, CursorPayload? payload, CursorEntry? entry, Head head, int limit, string epoch, DateTimeOffset now)
    {
        // H: ponto de consistência fixado na primeira página; as seguintes o recebem do cursor.
        var h = entry?.Sequence ?? head.Last;
        var position = payload?.Snapshot;
        var collected = new List<(int Rank, Guid Id, JsonObject Change)>();
        foreach (var rank in SnapshotRanks.Where(r => position is null || r >= position.Rank))
        {
            var need = limit + 1 - collected.Count;
            if (need <= 0)
            {
                break;
            }

            var after = position is not null && position.Rank == rank ? position.Id : (Guid?)null;
            foreach (var change in await FetchRankAsync(c, rank, after, need))
            {
                collected.Add((rank, Wire.ReadGuid(change["entity_id"])!.Value, change));
            }
        }

        var hasMore = collected.Count > limit;
        if (hasMore)
        {
            collected.RemoveRange(limit, collected.Count - limit);
        }

        var next = new CursorPayload(
            now.ToUnixTimeSeconds(),
            new Dictionary<Guid, CursorEntry> { [c.BabyId] = new CursorEntry(h, epoch) },
            hasMore ? new SnapshotPosition(collected[^1].Rank, collected[^1].Id) : null);
        return ("SNAPSHOT", collected.Select(x => x.Change).ToList(), hasMore, next);
    }

    private static async Task<List<JsonObject>> FetchRankAsync(Context c, int rank, Guid? after, int need)
    {
        var changes = new List<JsonObject>();
        var afterParam = Db.Uuid("after", after ?? Guid.Empty);
        var tail = after is null ? string.Empty : " AND t.id > @after";
        switch (rank)
        {
            case 0:
                {
                    if (after is not null)
                    {
                        return changes;                      // o perfil é uma única linha: já foi entregue
                    }

                    var row = await c.Tx.QueryJsonFirstAsync(BabySql + " WHERE b.id = @baby AND b.deleted_at IS NULL", Db.Uuid("baby", c.BabyId), Db.Text("role", c.Role));
                    if (row is not null)
                    {
                        changes.Add(Upsert("BABY", c.BabyId, row, EntityMapper.Baby(row)));
                    }

                    return changes;
                }

            case 1:
                {
                    var rows = await c.Tx.QueryJsonAsync(
                        $"SELECT to_jsonb(t) FROM nina.sleep_schedule_preference t WHERE t.baby_id = @baby AND t.deleted_at IS NULL{tail} ORDER BY t.id LIMIT @need",
                        Db.Uuid("baby", c.BabyId), afterParam, Db.P("need", need, NpgsqlDbType.Integer));
                    changes.AddRange(rows.Select(r => Upsert(PreferencesFeedType, Wire.ReadGuid(r["id"])!.Value, r, EntityMapper.SleepPreferences(r))));
                    return changes;
                }

            default:
                {
                    var spec = EntityCatalog.All[rank switch { 2 => 0, 3 => 4, 4 => 1, 5 => 2, _ => 3 }];
                    var rows = await c.Tx.QueryJsonAsync(
                        $"SELECT to_jsonb(t) FROM nina.{spec.Table} t WHERE t.baby_id = @baby AND t.deleted_at IS NULL{tail} ORDER BY t.id LIMIT @need",
                        Db.Uuid("baby", c.BabyId), afterParam, Db.P("need", need, NpgsqlDbType.Integer));
                    var nights = spec.Type == EntityCatalog.SleepSession ? await NightsAsync(c, rows) : [];
                    changes.AddRange(rows.Select(r => Upsert(spec.Type, Wire.ReadGuid(r["id"])!.Value, r, MapEntity(c, spec, r, nights))));
                    return changes;
                }
        }
    }

    // ---------------------------------------------------------------- delta

    private sealed record LogRow(long Sequence, string EntityType, Guid EntityId);

    private static async Task<(string Mode, List<JsonObject> Changes, bool HasMore, CursorPayload Next)> DeltaAsync(
        Context c, CursorEntry entry, Head head, int limit, string epoch, DateTimeOffset now)
    {
        var log = await c.Tx.QueryAsync(
            """
            SELECT sync_sequence, entity_type, entity_id FROM nina.change_log
             WHERE baby_id = @baby AND sync_sequence > @cursor ORDER BY sync_sequence LIMIT @n
            """,
            r => new LogRow(r.GetInt64(0), r.GetString(1), r.GetGuid(2)),
            Db.Uuid("baby", c.BabyId), Db.P("cursor", entry.Sequence, NpgsqlDbType.Bigint), Db.P("n", limit + 1, NpgsqlDbType.Integer));
        var hasMore = log.Count > limit;
        if (hasMore)
        {
            log.RemoveRange(limit, log.Count - limit);
        }

        var states = new Dictionary<(string Type, Guid Id), JsonObject>();
        foreach (var group in log.GroupBy(l => l.EntityType))
        {
            var ids = group.Select(l => l.EntityId).Distinct().ToArray();
            foreach (var row in await LoadStatesAsync(c, group.Key, ids))
            {
                states[(group.Key, Wire.ReadGuid(row["id"])!.Value)] = row;
            }
        }

        var sleepRows = states.Where(s => s.Key.Type == EntityCatalog.SleepSession && s.Value["deleted_at"] is null).Select(s => s.Value).ToList();
        var nights = await NightsAsync(c, sleepRows);

        var changes = new List<JsonObject>();
        foreach (var item in log)
        {
            // Colapsa: só a linha de log que corresponde à versão atual da entidade emite; as superadas serão emitidas pela mais nova.
            if (!states.TryGetValue((item.EntityType, item.EntityId), out var state) || Wire.ReadLong(state["version"]) != item.Sequence)
            {
                continue;
            }

            var feedType = FeedType(item.EntityType);
            if (state["deleted_at"] is not null)
            {
                changes.Add(new JsonObject
                {
                    ["op"] = "TOMBSTONE",
                    ["entity_type"] = feedType,
                    ["entity_id"] = item.EntityId.ToString("D"),
                    ["version"] = item.Sequence,
                    ["deleted_at"] = Wire.InstantOrNull(Wire.ReadInstant(state["deleted_at"])),
                });
                continue;
            }

            var entity = item.EntityType switch
            {
                "BABY" => EntityMapper.Baby(state),
                "SLEEP_SCHEDULE_PREFERENCE" => EntityMapper.SleepPreferences(state),
                _ => MapEntity(c, EntityCatalog.For(item.EntityType), state, nights),
            };
            changes.Add(Upsert(feedType, item.EntityId, state, entity));
        }

        var nextSequence = hasMore ? log[^1].Sequence : Math.Max(entry.Sequence, head.Last);
        var next = new CursorPayload(
            now.ToUnixTimeSeconds(), new Dictionary<Guid, CursorEntry> { [c.BabyId] = new CursorEntry(nextSequence, epoch) });
        return ("DELTA", changes, hasMore, next);
    }

    private const string BabySql =
        """
        SELECT to_jsonb(b) || jsonb_build_object(
                 'my_role', @role::text,
                 'local_today', ((now() AT TIME ZONE b.timezone)::date)::text,
                 'age_calc', (SELECT to_jsonb(a) FROM nina.age_calculation(b.birth_date, b.due_date, (now() AT TIME ZONE b.timezone)::date) a))
          FROM nina.baby b
        """;

    private static async Task<List<JsonObject>> LoadStatesAsync(Context c, string entityType, Guid[] ids)
    {
        var idParam = new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids };
        switch (entityType)
        {
            case "BABY":
                return await c.Tx.QueryJsonAsync(BabySql + " WHERE b.id = @baby", Db.Uuid("baby", c.BabyId), Db.Text("role", c.Role));
            case "SLEEP_SCHEDULE_PREFERENCE":
                return await c.Tx.QueryJsonAsync(
                    "SELECT to_jsonb(t) FROM nina.sleep_schedule_preference t WHERE t.baby_id = @baby AND t.id = ANY(@ids)", Db.Uuid("baby", c.BabyId), idParam);
            default:
                var spec = EntityCatalog.ByType(entityType);
                return spec is null
                    ? []
                    : await c.Tx.QueryJsonAsync($"SELECT to_jsonb(t) FROM nina.{spec.Table} t WHERE t.baby_id = @baby AND t.id = ANY(@ids)", Db.Uuid("baby", c.BabyId), idParam);
        }
    }

    // ---------------------------------------------------------------- mapeamento

    private static string FeedType(string logType) => logType == "SLEEP_SCHEDULE_PREFERENCE" ? PreferencesFeedType : logType;

    private static JsonObject Upsert(string feedType, Guid id, JsonObject row, JsonObject entity) => new()
    {
        ["op"] = "UPSERT",
        ["entity_type"] = feedType,
        ["entity_id"] = id.ToString("D"),
        ["version"] = Wire.ReadLong(row["version"]),
        ["entity"] = entity,
    };

    private static JsonObject MapEntity(Context c, EntitySpec spec, JsonObject row, IReadOnlyDictionary<Guid, int?> nights)
    {
        if (spec.Type == EntityCatalog.WakeEvent)
        {
            return EntityMapper.Wake(row);
        }

        var id = Wire.ReadGuid(row["id"])!.Value;
        return EntityMapper.Event(spec, row, c.Authors, nights.GetValueOrDefault(id));
    }

    private static async Task<Dictionary<Guid, int?>> NightsAsync(Context c, List<JsonObject> sleepRows)
    {
        var result = new Dictionary<Guid, int?>();
        if (sleepRows.Count == 0)
        {
            return result;
        }

        var ids = sleepRows.Select(r => Wire.ReadGuid(r["id"])!.Value).ToArray();
        var rows = await c.Tx.QueryAsync(
            "SELECT s.id, nina.night_awakenings(@baby, s.id) FROM unnest(@ids) AS s(id)",
            r => (Id: r.GetGuid(0), Nights: r.IsDBNull(1) ? (int?)null : r.GetInt32(1)),
            Db.Uuid("baby", c.BabyId), new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids });
        foreach (var (id, nights) in rows)
        {
            result[id] = nights;
        }

        return result;
    }
}
