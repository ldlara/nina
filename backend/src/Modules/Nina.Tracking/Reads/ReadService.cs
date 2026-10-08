using System.Data;
using System.Globalization;
using System.Text.Json.Nodes;
using Nina.SharedKernel.Data;
using Nina.SharedKernel.Http;
using Nina.Tracking.Domain;
using Nina.Tracking.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace Nina.Tracking.Reads;

internal sealed record TimelineQuery(int Limit, string? PageToken, IReadOnlyList<string>? Types, DateTimeOffset? From, DateTimeOffset? To, bool IncludeOpen);

/// <summary>Leituras do módulo: <c>GET /babies/{id}/timeline</c>, <c>/events/{id}</c> e <c>/events/{id}/history</c> (RF-019, RF-020).</summary>
internal sealed class ReadService(NpgsqlDataSource dataSource, SignedToken tokens)
{
    private sealed record TimelineToken(int Rank, DateTimeOffset At, Guid Id);

    private sealed record HistoryToken(long Sequence);

    private static readonly string[] EventTypes = ["SLEEP", "FEEDING", "PUMPING", "DIAPER"];

    public static IReadOnlyList<string> AllEventTypes => EventTypes;

    private static IEnumerable<EntitySpec> Specs(IEnumerable<string>? types) =>
        EntityCatalog.All.Where(s => s.EventType is not null && (types is null || types.Contains(s.EventType, StringComparer.Ordinal)));

    private static string TimeColumn(EntitySpec spec) => spec.StartField ?? "occurred_at";

    // ---------------------------------------------------------------- timeline

    public async Task<JsonObject> TimelineAsync(Guid userId, Guid babyId, TimelineQuery q, CancellationToken ct)
    {
        await using var tx = await TrackingTx.BeginAsync(dataSource, userId, null, IsolationLevel.ReadCommitted, ct);
        var access = await BabyAccess.ResolveAsync(tx, babyId);
        access.RequireRead();

        TimelineToken? token = null;
        if (q.PageToken is not null)
        {
            token = tokens.Open<TimelineToken>(q.PageToken) ?? throw ProblemException.Validation(new FieldError("page_token", "INVALID_VALUE"));
            if (token.Rank is not (0 or 1))
            {
                throw ProblemException.Validation(new FieldError("page_token", "INVALID_VALUE"));
            }
        }

        var specs = Specs(q.Types).ToList();
        var rows = new List<(int Rank, DateTimeOffset At, Guid Id, EntitySpec Spec, JsonObject Row)>();

        // Sessões em aberto primeiro (rank 0), depois as encerradas e a fralda (rank 1), cada grupo por (instante, id) decrescente.
        if (q.IncludeOpen && (token is null || token.Rank == 0))
        {
            var open = await FetchAsync(tx, babyId, specs.Where(s => s.EndField is not null), q, openOnly: true, token?.Rank == 0 ? token : null, q.Limit + 1);
            rows.AddRange(open.Select(x => (0, x.At, x.Id, x.Spec, x.Row)));
        }

        if (rows.Count < q.Limit + 1)
        {
            var need = q.Limit + 1 - rows.Count;
            var closed = await FetchAsync(tx, babyId, specs, q, openOnly: false, token?.Rank == 1 ? token : null, need);
            rows.AddRange(closed.Select(x => (1, x.At, x.Id, x.Spec, x.Row)));
        }

        var hasMore = rows.Count > q.Limit;
        if (hasMore)
        {
            rows.RemoveRange(q.Limit, rows.Count - q.Limit);
        }

        var authors = await BabyAccess.LoadAuthorsAsync(tx, babyId);
        var nights = await NightsAsync(tx, babyId, rows.Where(r => r.Spec.Type == EntityCatalog.SleepSession).Select(r => r.Id).ToArray());
        var items = new JsonArray();
        foreach (var (_, _, id, spec, row) in rows)
        {
            items.Add(EntityMapper.Event(spec, row, authors, nights.GetValueOrDefault(id)));
        }

        return new JsonObject
        {
            ["items"] = items,
            ["page"] = new JsonObject
            {
                ["has_more"] = hasMore,
                ["next_page_token"] = hasMore ? tokens.Seal(new TimelineToken(rows[^1].Rank, rows[^1].At, rows[^1].Id)) : null,
            },
        };
    }

    private static async Task<List<(DateTimeOffset At, Guid Id, EntitySpec Spec, JsonObject Row)>> FetchAsync(
        TrackingTx tx, Guid babyId, IEnumerable<EntitySpec> specs, TimelineQuery q, bool openOnly, TimelineToken? after, int need)
    {
        var merged = new List<(DateTimeOffset At, Guid Id, EntitySpec Spec, JsonObject Row)>();
        foreach (var spec in specs)
        {
            var time = TimeColumn(spec);
            var filters = new List<string> { "t.baby_id = @baby", "t.deleted_at IS NULL" };
            var parameters = new List<NpgsqlParameter> { Db.Uuid("baby", babyId), Db.P("need", need, NpgsqlDbType.Integer) };
            if (spec.EndField is not null)
            {
                filters.Add(openOnly ? $"t.{spec.EndField} IS NULL" : $"t.{spec.EndField} IS NOT NULL");
            }
            else if (openOnly)
            {
                continue;
            }

            if (q.From is { } from)
            {
                filters.Add($"t.{time} >= @from");
                parameters.Add(Db.Timestamp("from", from));
            }

            if (q.To is { } to)
            {
                filters.Add($"t.{time} < @to");
                parameters.Add(Db.Timestamp("to", to));
            }

            if (after is not null)
            {
                filters.Add($"(t.{time}, t.id) < (@after_at, @after_id)");
                parameters.Add(Db.Timestamp("after_at", after.At));
                parameters.Add(Db.Uuid("after_id", after.Id));
            }

            var rows = await tx.QueryJsonAsync(
                $"SELECT to_jsonb(t) FROM nina.{spec.Table} t WHERE {string.Join(" AND ", filters)} ORDER BY t.{time} DESC, t.id DESC LIMIT @need",
                [.. parameters]);
            merged.AddRange(rows.Select(r => (Wire.RequireInstant(r[time]), Wire.ReadGuid(r["id"])!.Value, spec, r)));
        }

        return [.. merged
            .OrderByDescending(x => x.At)
            .ThenByDescending(x => x.Id.ToString("D"), StringComparer.Ordinal)
            .Take(need)];
    }

    private static async Task<Dictionary<Guid, int?>> NightsAsync(TrackingTx tx, Guid babyId, Guid[] sleepIds)
    {
        var result = new Dictionary<Guid, int?>();
        if (sleepIds.Length == 0)
        {
            return result;
        }

        var rows = await tx.QueryAsync(
            "SELECT s.id, nina.night_awakenings(@baby, s.id) FROM unnest(@ids) AS s(id)",
            r => (Id: r.GetGuid(0), Nights: r.IsDBNull(1) ? (int?)null : r.GetInt32(1)),
            Db.Uuid("baby", babyId), new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = sleepIds });
        foreach (var (id, nights) in rows)
        {
            result[id] = nights;
        }

        return result;
    }

    // ---------------------------------------------------------------- evento

    public async Task<JsonObject> GetEventAsync(Guid userId, Guid babyId, Guid eventId, CancellationToken ct)
    {
        await using var tx = await TrackingTx.BeginAsync(dataSource, userId, null, IsolationLevel.ReadCommitted, ct);
        var access = await BabyAccess.ResolveAsync(tx, babyId);
        access.RequireRead();

        foreach (var spec in Specs(null))
        {
            var row = await tx.QueryJsonFirstAsync(
                $"SELECT to_jsonb(t) FROM nina.{spec.Table} t WHERE t.baby_id = @baby AND t.id = @id AND t.deleted_at IS NULL",
                Db.Uuid("baby", babyId), Db.Uuid("id", eventId));
            if (row is null)
            {
                continue;
            }

            var authors = await BabyAccess.LoadAuthorsAsync(tx, babyId);
            var nights = spec.Type == EntityCatalog.SleepSession ? (await NightsAsync(tx, babyId, [eventId])).GetValueOrDefault(eventId) : null;
            return EntityMapper.Event(spec, row, authors, nights);
        }

        throw ProblemException.NotFound();
    }

    // ---------------------------------------------------------------- histórico (RF-020, RF-055)

    public async Task<JsonObject> HistoryAsync(Guid userId, Guid babyId, Guid eventId, int limit, string? pageToken, CancellationToken ct)
    {
        await using var tx = await TrackingTx.BeginAsync(dataSource, userId, null, IsolationLevel.ReadCommitted, ct);
        var access = await BabyAccess.ResolveAsync(tx, babyId);
        access.RequireRead();
        if (access.Role is not ("OWNER" or "CAREGIVER"))
        {
            throw ProblemException.Forbidden("FORBIDDEN_ROLE", "Role not allowed");          // D-25: ReadOnly não vê o histórico
        }

        HistoryToken? token = null;
        if (pageToken is not null)
        {
            token = tokens.Open<HistoryToken>(pageToken) ?? throw ProblemException.Validation(new FieldError("page_token", "INVALID_VALUE"));
        }

        EntitySpec? found = null;
        DateTimeOffset createdAt = default;
        foreach (var spec in Specs(null))
        {
            var created = await tx.ScalarAsync<DateTime?>(
                $"SELECT created_at FROM nina.{spec.Table} WHERE baby_id = @baby AND id = @id", Db.Uuid("baby", babyId), Db.Uuid("id", eventId));
            if (created is { } at)
            {
                found = spec;
                createdAt = new DateTimeOffset(at, TimeSpan.Zero);
                break;
            }
        }

        if (found is null)
        {
            throw ProblemException.NotFound();
        }

        var log = await tx.QueryAsync(
            """
            SELECT sync_sequence, op, actor_user_id, changed_at FROM nina.change_log
             WHERE baby_id = @baby AND entity_type = @type AND entity_id = @id AND sync_sequence < @before
             ORDER BY sync_sequence DESC LIMIT @n
            """,
            r => (Sequence: r.GetInt64(0), Op: r.GetString(1), Actor: r.IsDBNull(2) ? (Guid?)null : r.GetGuid(2), At: r.GetFieldValue<DateTimeOffset>(3).ToUniversalTime()),
            Db.Uuid("baby", babyId), Db.Text("type", found.Type), Db.Uuid("id", eventId),
            Db.P("before", token?.Sequence ?? long.MaxValue, NpgsqlDbType.Bigint), Db.P("n", limit + 1, NpgsqlDbType.Integer));
        var hasMore = log.Count > limit;
        if (hasMore)
        {
            log.RemoveRange(limit, log.Count - limit);
        }

        var oldest = await tx.ScalarAsync<long?>(
            "SELECT min(sync_sequence) FROM nina.change_log WHERE baby_id = @baby AND entity_type = @type AND entity_id = @id",
            Db.Uuid("baby", babyId), Db.Text("type", found.Type), Db.Uuid("id", eventId));
        var authors = await BabyAccess.LoadAuthorsAsync(tx, babyId);
        var items = new JsonArray();
        foreach (var entry in log)
        {
            var action = entry.Op == "DELETE"
                ? "DELETED"
                : entry.Sequence == oldest && Math.Abs((entry.At - createdAt).TotalSeconds) < 5 ? "CREATED" : "UPDATED";
            items.Add(new JsonObject
            {
                ["at"] = Wire.Instant(entry.At),
                ["actor"] = authors.Ref(entry.Actor),
                ["action"] = action,
            });
        }

        return new JsonObject
        {
            ["items"] = items,
            ["page"] = new JsonObject
            {
                ["has_more"] = hasMore,
                ["next_page_token"] = hasMore ? tokens.Seal(new HistoryToken(log[^1].Sequence)) : null,
            },
        };
    }

    internal static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
