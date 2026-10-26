using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Nina.SyncSpike.Server;

/// <summary>
/// Lógica de servidor do sync (spike ARCH-003): push idempotente e pull por cursor opaco, sempre como
/// papel nina_app (sujeito a RLS), com `nina.user_id`/`nina.device_id` definidos por transação.
/// </summary>
public sealed partial class SyncService(NpgsqlDataSource appDataSource, CursorCodec cursors, IClock clock, SyncOptions? options = null)
{
    private readonly NpgsqlDataSource _ds = appDataSource;
    private readonly SyncOptions _opt = options ?? new SyncOptions();
    public SyncOptions Options => _opt;
    public CursorCodec Cursors => cursors;
    public IClock TimeSource => clock;

    private static readonly Guid ServerDevice = Guid.Empty;

    // ------------------------------------------------------------------ infra de transação

    private sealed class Env(NpgsqlConnection conn, NpgsqlTransaction tx, AuthContext ctx, Guid babyId, DateTimeOffset now)
    {
        public NpgsqlConnection Conn { get; } = conn;
        public NpgsqlTransaction Tx { get; } = tx;
        public AuthContext Ctx { get; } = ctx;
        public Guid BabyId { get; } = babyId;
        public DateTimeOffset Now { get; } = now;
        public string? Role { get; set; }
        public string? MembershipStatus { get; set; }
        public Guid? MembershipId { get; set; }
        public string OverlapPolicy { get; set; } = "ACCEPT_AND_WARN";
    }

    private static NpgsqlCommand Cmd(Env e, string sql) => new(sql, e.Conn, e.Tx);

    private static void P(NpgsqlCommand c, string name, object? value, NpgsqlDbType type) =>
        c.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });

    private static async Task ExecAsync(Env e, string sql, CancellationToken ct)
    {
        await using var c = Cmd(e, sql);
        await c.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Contexto de requisição + vínculo do usuário + política de sobreposição em um round trip.</summary>
    private static async Task LoadEnvAsync(Env e, CancellationToken ct)
    {
        await using var b = new NpgsqlBatch(e.Conn, e.Tx);
        var ctxCmd = new NpgsqlBatchCommand("SELECT set_config('nina.user_id', $1, true), set_config('nina.device_id', $2, true)");
        ctxCmd.Parameters.Add(new NpgsqlParameter { Value = e.Ctx.UserId.ToString() });
        ctxCmd.Parameters.Add(new NpgsqlParameter { Value = e.Ctx.DeviceId.ToString() });
        b.BatchCommands.Add(ctxCmd);
        var mem = new NpgsqlBatchCommand(
            "SELECT id, role, status FROM nina.caregiver_membership WHERE baby_id = $1 AND user_id = $2 " +
            "ORDER BY (status = 'ACTIVE') DESC, created_at DESC LIMIT 1");
        mem.Parameters.Add(new NpgsqlParameter { Value = e.BabyId });
        mem.Parameters.Add(new NpgsqlParameter { Value = e.Ctx.UserId });
        b.BatchCommands.Add(mem);
        b.BatchCommands.Add(new NpgsqlBatchCommand("SELECT nina.param_text('sleep.overlap_policy', 'ACCEPT_AND_WARN')"));
        await using var r = await b.ExecuteReaderAsync(ct);
        await r.NextResultAsync(ct);
        if (await r.ReadAsync(ct))
        {
            e.MembershipId = r.GetGuid(0);
            e.Role = r.GetString(1);
            e.MembershipStatus = r.GetString(2);
        }
        await r.NextResultAsync(ct);
        if (await r.ReadAsync(ct)) e.OverlapPolicy = r.GetString(0);
    }

    // ------------------------------------------------------------------ rejeições

    private sealed class RejectException(string code, IReadOnlyList<FieldError>? errors = null, bool retryable = false,
        bool record = true, string syncOutcome = "REJECTED") : Exception(code)
    {
        public string Code { get; } = code;
        public IReadOnlyList<FieldError>? Errors { get; } = errors;
        public bool Retryable { get; } = retryable;
        public bool Record { get; } = record;
        public string SyncOutcome { get; } = syncOutcome;
    }

    private static RejectException Invalid(string field, string code) => new("VALIDATION_FAILED", [new FieldError(field, code)]);

    private static RejectException Deleted() => new("ENTITY_DELETED", syncOutcome: "IGNORED_TOMBSTONE");

    private static (int Status, string Title) Describe(string code) => code switch
    {
        "VALIDATION_FAILED" => (422, "Validation failed"),
        "SLEEP_OVERLAP" => (409, "Sleep session overlaps another"),
        "ENTITY_DELETED" => (409, "Entity already deleted"),
        "ENTITY_NOT_FOUND" => (404, "Entity not found"),
        "VERSION_AHEAD" => (409, "base_version ahead of server"),
        "FORBIDDEN_ROLE" => (403, "Role cannot write"),
        "ACCESS_REVOKED" => (403, "Access revoked"),
        "BABY_NOT_FOUND" => (404, "Baby not found"),
        "MUTATION_ID_REUSE" => (422, "mutation_id reused with a different mutation"),
        "ENTITY_ID_UNAVAILABLE" => (409, "entity_id already used"),
        _ => (503, "Transient failure"),
    };

    private static MutationResult Rejected(Mutation m, string code, IReadOnlyList<FieldError>? errors, bool retryable, IReadOnlyList<PushWarning>? warnings = null)
    {
        var (status, title) = Describe(code);
        return new MutationResult(m.MutationId, "REJECTED", m.EntityType, m.EntityId, Retryable: retryable,
            Warnings: warnings, Problem: new Problem(code, status, title, errors));
    }

    private static RejectException MapPg(PostgresException e) => e.SqlState switch
    {
        "NN002" => Deleted(),
        "NN003" => new RejectException("BABY_NOT_FOUND"),
        "NN006" => new RejectException("SLEEP_OVERLAP"),
        "NN001" => Invalid("id", "IMMUTABLE"),
        "42501" => new RejectException("FORBIDDEN_ROLE"),
        "23503" => new RejectException("ENTITY_NOT_FOUND"),
        "23505" when e.ConstraintName?.EndsWith("_pkey", StringComparison.Ordinal) == true => new RejectException("ENTITY_ID_UNAVAILABLE"),   // id de OUTRO bebê (invisível pela RLS)
        var s when s.StartsWith("23", StringComparison.Ordinal) || s.StartsWith("22", StringComparison.Ordinal) =>
            Invalid(e.ConstraintName ?? e.ColumnName ?? "data", e.SqlState),
        _ => throw e,
    };

    private static bool IsTxRetry(PostgresException e) => e.SqlState is "40P01" or "40001";

    // ------------------------------------------------------------------ push

    public async Task<PushResponse> PushAsync(AuthContext ctx, PushRequest req, long? payloadBytes = null, CancellationToken ct = default)
    {
        if (req.DeviceId != ctx.DeviceId)
            throw new SyncProblemException(new Problem("VALIDATION_FAILED", 400, "device_id differs from session",
                [new FieldError("device_id", "SESSION_MISMATCH")]));
        if (req.Mutations.Count is < 1 || req.Mutations.Count > _opt.MaxBatch)
            throw new SyncProblemException(new Problem("VALIDATION_FAILED", 400, "1..100 mutations per push",
                [new FieldError("mutations", "SIZE")]));
        if (payloadBytes > _opt.MaxPayloadBytes)
            throw new SyncProblemException(new Problem("PAYLOAD_TOO_LARGE", 413, "Push above 256 KiB"));

        var results = new MutationResult[req.Mutations.Count];
        // Um grupo por bebê, em ordem de baby_id: locks de baby_sync_head sempre na mesma ordem => sem deadlock
        // entre pushes concorrentes que tocam os mesmos bebês. A ordem relativa dentro do bebê é preservada.
        var groups = req.Mutations.Select((m, i) => (m, i)).GroupBy(x => x.m.BabyId).OrderBy(g => g.Key);
        foreach (var g in groups)
        {
            var items = g.ToList();
            if (_opt.Mode == PushMode.PerMutationTransaction)
            {
                foreach (var it in items)
                    results[it.i] = (await ProcessGroupAsync(ctx, g.Key, [it.m], ct))[0];
            }
            else
            {
                var res = await ProcessGroupAsync(ctx, g.Key, items.Select(x => x.m).ToList(), ct);
                for (var k = 0; k < items.Count; k++) results[items[k].i] = res[k];
            }
        }
        return new PushResponse(clock.UtcNow, results);
    }

    private async Task<MutationResult[]> ProcessGroupAsync(AuthContext ctx, Guid babyId, IReadOnlyList<Mutation> ms, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ProcessGroupOnceAsync(ctx, babyId, ms, ct);
            }
            catch (PostgresException e) when (IsTxRetry(e))
            {
                if (attempt + 1 >= _opt.MaxTxRetries)
                    return ms.Select(m => Rejected(m, "TRANSIENT", null, retryable: true)).ToArray();
                await Task.Delay(Random.Shared.Next(2, 10 << attempt), ct);
            }
        }
    }

    private async Task<MutationResult[]> ProcessGroupOnceAsync(AuthContext ctx, Guid babyId, IReadOnlyList<Mutation> ms, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var env = new Env(conn, tx, ctx, babyId, clock.UtcNow);
        await LoadEnvAsync(env, ct);

        string? denied = env.MembershipStatus switch
        {
            null => "BABY_NOT_FOUND",
            "REVOKED" => "ACCESS_REVOKED",
            "ACTIVE" => env.Role == "READ_ONLY" ? "FORBIDDEN_ROLE" : null,
            _ => "BABY_NOT_FOUND",
        };
        if (denied != null)
        {
            await tx.RollbackAsync(ct);
            return ms.Select(m => Rejected(m, denied, null, false) with { ServerReceivedAt = env.Now }).ToArray();
        }

        var results = new MutationResult[ms.Count];
        for (var i = 0; i < ms.Count; i++) results[i] = await RunOneAsync(env, ms[i], ct);
        await tx.CommitAsync(ct);
        return results;
    }

    private async Task<MutationResult> RunOneAsync(Env env, Mutation m, CancellationToken ct)
    {
        await ExecAsync(env, "SAVEPOINT m", ct);
        for (var attempt = 0; ; attempt++)
        {
            RejectException rj;
            try
            {
                var a = await ApplyAsync(env, m, ct);
                if (!a.IsDuplicate) await RecordAsync(env, m, a.SyncOutcome, a.ResultVersion, null, ct);
                await ExecAsync(env, "RELEASE SAVEPOINT m", ct);
                return a.Result with { ServerReceivedAt = env.Now };
            }
            catch (RejectException r)
            {
                rj = r;
            }
            catch (PostgresException pg) when (pg.SqlState == "23505" && pg.ConstraintName == "sleep_one_open_uq" && attempt < 2)
            {
                // outro dispositivo abriu um sono ao mesmo tempo: refazer vendo a sessão aberta já confirmada
                await ExecAsync(env, "ROLLBACK TO SAVEPOINT m", ct);
                continue;
            }
            catch (PostgresException pg) when (!IsTxRetry(pg))
            {
                rj = MapPg(pg);
            }
            await ExecAsync(env, "ROLLBACK TO SAVEPOINT m", ct);
            if (!rj.Retryable && rj.Record && m.Op is "CREATE" or "UPDATE" or "DELETE")
                await RecordAsync(env, m, rj.SyncOutcome, null, rj.Code, ct);
            return Rejected(m, rj.Code, rj.Errors, rj.Retryable) with { ServerReceivedAt = env.Now };
        }
    }

    private static async Task RecordAsync(Env env, Mutation m, string outcome, long? resultVersion, string? rejectCode, CancellationToken ct)
    {
        await using var c = Cmd(env,
            "INSERT INTO nina.sync_mutation (mutation_id, baby_id, user_id, device_id, entity_type, entity_id, op, base_version, " +
            "client_created_at, outcome, result_version, reject_code) VALUES (@id,@b,@u,@d,@et,@eid,@op,@bv,@cc,@o,@rv,@rc) " +
            "ON CONFLICT (mutation_id) DO NOTHING");
        P(c, "id", m.MutationId, NpgsqlDbType.Uuid); P(c, "b", env.BabyId, NpgsqlDbType.Uuid);
        P(c, "u", env.Ctx.UserId, NpgsqlDbType.Uuid); P(c, "d", env.Ctx.DeviceId, NpgsqlDbType.Uuid);
        P(c, "et", m.EntityType, NpgsqlDbType.Text); P(c, "eid", m.EntityId, NpgsqlDbType.Uuid);
        P(c, "op", m.Op, NpgsqlDbType.Text); P(c, "bv", m.BaseVersion, NpgsqlDbType.Bigint);
        P(c, "cc", m.ClientCreatedAt.UtcDateTime, NpgsqlDbType.TimestampTz); P(c, "o", outcome, NpgsqlDbType.Text);
        P(c, "rv", resultVersion, NpgsqlDbType.Bigint); P(c, "rc", rejectCode, NpgsqlDbType.Text);
        // 0 linhas = o mutation_id já existe em linha INVISÍVEL para este usuário (RLS): a PK de sync_mutation é global.
        // Sem esta checagem a mutação seria aplicada sem registro (reenvio duplicaria o efeito).
        if (await c.ExecuteNonQueryAsync(ct) == 0 && !await ExistsVisibleAsync(env, m.MutationId, ct)) throw new RejectException("MUTATION_ID_REUSE", record: false);
    }

    private static async Task<bool> ExistsVisibleAsync(Env env, Guid mutationId, CancellationToken ct)
    {
        await using var c = Cmd(env, "SELECT 1 FROM nina.sync_mutation WHERE mutation_id = @id");
        P(c, "id", mutationId, NpgsqlDbType.Uuid);
        return await c.ExecuteScalarAsync(ct) != null;
    }

    private sealed record Applied(MutationResult Result, string SyncOutcome, long? ResultVersion, bool IsDuplicate = false);

    private sealed record StoredMutation(Guid BabyId, string EntityType, Guid EntityId, string Op, string Outcome, long? ResultVersion, string? RejectCode);

    private async Task<Applied> ApplyAsync(Env env, Mutation m, CancellationToken ct)
    {
        if (!EntityCatalog.Push.TryGetValue(m.EntityType, out var spec)) throw Invalid("entity_type", "UNKNOWN");
        if (m.Op is not ("CREATE" or "UPDATE" or "DELETE")) throw new RejectException("VALIDATION_FAILED", [new FieldError("op", "UNKNOWN")], record: false);

        // 1) idempotência: o lock consultivo por mutation_id serializa reenvios simultâneos da mesma mutação
        var dup = await LockAndFindAsync(env, m, ct);
        if (dup != null) return await DuplicateAsync(env, m, spec, dup, ct);

        // ordem de resolução: chegada ao servidor (contrato v1.0.1) ou client_created_at limitado ao recebimento (ADR-0003 original)
        var t = _opt.ConflictOrder == ConflictOrder.ServerArrival || m.ClientCreatedAt > env.Now ? env.Now : m.ClientCreatedAt;
        var warnings = new List<PushWarning>();
        var skew = (int)(m.ClientCreatedAt - env.Now).TotalSeconds;
        if (Math.Abs(skew) > _opt.ClockSkewWarnSeconds)
            warnings.Add(new PushWarning("CLIENT_CLOCK_SKEW", SkewSeconds: skew, ToleranceSeconds: _opt.ClockSkewWarnSeconds));

        return m.Op switch
        {
            "CREATE" => await CreateAsync(env, m, spec, t, warnings, ct),
            "UPDATE" => await UpdateAsync(env, m, spec, t, warnings, ct),
            _ => await DeleteAsync(env, m, spec, t, warnings, ct),
        };
    }

    private static async Task<StoredMutation?> LockAndFindAsync(Env env, Mutation m, CancellationToken ct)
    {
        await using var b = new NpgsqlBatch(env.Conn, env.Tx);
        var l = new NpgsqlBatchCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))");
        l.Parameters.Add(new NpgsqlParameter { Value = m.MutationId.ToString() });
        b.BatchCommands.Add(l);
        var s = new NpgsqlBatchCommand(
            "SELECT baby_id, entity_type, entity_id, op, outcome, result_version, reject_code FROM nina.sync_mutation WHERE mutation_id = $1");
        s.Parameters.Add(new NpgsqlParameter { Value = m.MutationId });
        b.BatchCommands.Add(s);
        await using var r = await b.ExecuteReaderAsync(ct);
        await r.NextResultAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new StoredMutation(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4),
            r.IsDBNull(5) ? null : r.GetInt64(5), r.IsDBNull(6) ? null : r.GetString(6));
    }

    private async Task<Applied> DuplicateAsync(Env env, Mutation m, EntitySpec spec, StoredMutation d, CancellationToken ct)
    {
        if (d.BabyId != m.BabyId || d.EntityId != m.EntityId || d.Op != m.Op || d.EntityType != m.EntityType)
            throw new RejectException("MUTATION_ID_REUSE", record: false);
        if (d.Outcome is "REJECTED" or "IGNORED_TOMBSTONE")
            return new Applied(Rejected(m, d.RejectCode ?? "VALIDATION_FAILED", null, false), d.Outcome, null, true);
        var row = await LoadRowAsync(env, spec, m.EntityId, false, ct);
        var alive = row != null && row["deleted_at"] is null;
        return new Applied(new MutationResult(m.MutationId, "DUPLICATE", m.EntityType, m.EntityId, d.ResultVersion,
            d.Outcome == "MERGED" ? "MERGED" : "NONE", Entity: alive ? Strip(row!) : null), d.Outcome, d.ResultVersion, true);
    }

    // ------------------------------------------------------------------ leitura de entidade / relógios

    private static JsonObject Strip(JsonObject row)
    {
        var c = (JsonObject)row.DeepClone();
        c.Remove("created_by"); c.Remove("last_modified_by");
        return c;
    }

    private static async Task<JsonObject?> LoadRowAsync(Env env, EntitySpec spec, Guid id, bool forUpdate, CancellationToken ct)
    {
        await using var c = Cmd(env, $"SELECT to_jsonb(t) FROM {spec.Qualified} t WHERE t.id = @id" + (forUpdate ? " FOR UPDATE" : ""));
        P(c, "id", id, NpgsqlDbType.Uuid);
        await using var r = await c.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return JsonNode.Parse(r.GetFieldValue<string>(0))!.AsObject();
    }

    private sealed record FieldClock(DateTimeOffset Ts, long Version, Guid Device);

    private static async Task<Dictionary<string, FieldClock>> LoadClocksAsync(Env env, Guid entityId, CancellationToken ct)
    {
        var d = new Dictionary<string, FieldClock>();
        await using var c = Cmd(env, "SELECT field, ts, version, device_id FROM nina_spike.field_clock WHERE entity_id = @id");
        P(c, "id", entityId, NpgsqlDbType.Uuid);
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            d[r.GetString(0)] = new FieldClock(r.GetFieldValue<DateTimeOffset>(1), r.GetInt64(2), r.GetGuid(3));
        return d;
    }

    private static async Task UpsertClocksAsync(Env env, Mutation m, Guid entityId, IEnumerable<string> fields, DateTimeOffset ts,
        long version, Guid device, Guid? mutationId, CancellationToken ct, bool overwriteTs = false)
    {
        var arr = fields.ToArray();
        if (arr.Length == 0) return;
        await using var c = Cmd(env,
            "INSERT INTO nina_spike.field_clock (entity_id, field, baby_id, ts, version, user_id, device_id, mutation_id) " +
            "SELECT @e, f, @b, @ts, @v, @u, @d, @m FROM unnest(@f) AS f " +
            "ON CONFLICT (entity_id, field) DO UPDATE SET ts = CASE WHEN @ow THEN EXCLUDED.ts ELSE GREATEST(nina_spike.field_clock.ts, EXCLUDED.ts) END, " +
            "version = EXCLUDED.version, user_id = EXCLUDED.user_id, device_id = EXCLUDED.device_id, mutation_id = EXCLUDED.mutation_id");
        P(c, "e", entityId, NpgsqlDbType.Uuid); P(c, "b", m.BabyId, NpgsqlDbType.Uuid);
        P(c, "ts", ts.UtcDateTime, NpgsqlDbType.TimestampTz); P(c, "v", version, NpgsqlDbType.Bigint);
        P(c, "u", device == ServerDevice ? null : env.Ctx.UserId, NpgsqlDbType.Uuid); P(c, "d", device, NpgsqlDbType.Uuid);
        P(c, "m", mutationId, NpgsqlDbType.Uuid); P(c, "f", arr, NpgsqlDbType.Array | NpgsqlDbType.Text); P(c, "ow", overwriteTs, NpgsqlDbType.Boolean);
        await c.ExecuteNonQueryAsync(ct);
    }

    private static async Task AuditAsync(Env env, Mutation m, string action, JsonObject meta, CancellationToken ct)
    {
        // metadata_safe: apenas nomes de campos/enumerações/ids (nunca valores: notes e afins são C3)
        await using var c = Cmd(env,
            "INSERT INTO nina.audit_event (actor_user_id, actor_type, action, entity_type, entity_id, baby_id, device_id, metadata_safe) " +
            "VALUES (@u, 'USER', @a, @et, @eid, @b, @d, @meta)");
        P(c, "u", env.Ctx.UserId, NpgsqlDbType.Uuid); P(c, "a", action, NpgsqlDbType.Text);
        P(c, "et", m.EntityType, NpgsqlDbType.Text); P(c, "eid", m.EntityId, NpgsqlDbType.Uuid);
        P(c, "b", env.BabyId, NpgsqlDbType.Uuid); P(c, "d", env.Ctx.DeviceId, NpgsqlDbType.Uuid);
        P(c, "meta", meta.ToJsonString(), NpgsqlDbType.Jsonb);
        await c.ExecuteNonQueryAsync(ct);
    }

    // ------------------------------------------------------------------ parse de dados

    private sealed record Field(string Name, object? Value, NpgsqlDbType Type);

    private static List<Field> ParseData(EntitySpec spec, JsonElement? data, bool create)
    {
        if (data is not { ValueKind: JsonValueKind.Object } obj) throw Invalid("data", "REQUIRED");
        var list = new List<Field>();
        foreach (var p in obj.EnumerateObject())
        {
            var f = spec.Find(p.Name) ?? throw Invalid(p.Name, "UNKNOWN_FIELD");
            if (f.CreateOnly && !create) throw Invalid(p.Name, "IMMUTABLE");
            if (!EntityCatalog.TryParse(f, p.Value, out var v, out var t)) throw Invalid(p.Name, "INVALID_TYPE");
            list.Add(new Field(p.Name, v, t));
        }
        if (create)
        {
            foreach (var f in spec.Fields.Where(f => f.RequiredOnCreate && list.All(x => x.Name != f.Name)))
                throw Invalid(f.Name, "REQUIRED");
        }
        else if (list.Count == 0) throw Invalid("data", "EMPTY_PATCH");
        return list;
    }

    private static DateTimeOffset? Ts(JsonObject row, string key) =>
        row[key] is JsonValue v && v.TryGetValue<string>(out var s) ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture) : null;

    // ------------------------------------------------------------------ CREATE

    private async Task<Applied> CreateAsync(Env env, Mutation m, EntitySpec spec, DateTimeOffset t, List<PushWarning> warnings, CancellationToken ct)
    {
        var cur = await LoadRowAsync(env, spec, m.EntityId, true, ct);
        if (cur != null)
        {
            if (cur["deleted_at"] is not null) throw Deleted();
            throw new RejectException("ENTITY_ID_UNAVAILABLE");
        }
        if (m.BaseVersion != 0) throw Invalid("base_version", "MUST_BE_ZERO");
        var fields = ParseData(spec, m.Data, create: true);
        string? resolution = null;

        if (m.EntityType == "WAKE_EVENT")
        {
            var sess = await LoadRowAsync(env, EntityCatalog.Push["SLEEP_SESSION"], (Guid)fields.First(f => f.Name == "sleep_session_id").Value!, false, ct);
            if (sess == null || sess["baby_id"]!.GetValue<string>() != env.BabyId.ToString()) throw new RejectException("ENTITY_NOT_FOUND");
            if (sess["deleted_at"] is not null) throw Deleted();      // o banco NÃO impede apontar para sessão com tombstone
            ValidateWake(sess, (DateTime)fields.First(f => f.Name == "started_at").Value!,
                fields.First(f => f.Name == "ended_at").Value as DateTime?);
            fields.Add(new Field("tz", sess["tz"]!.GetValue<string>(), NpgsqlDbType.Text));   // tz é NOT NULL no banco e ausente no contrato
            if (fields.All(f => f.Name != "source")) fields.Add(new Field("source", "MANUAL", NpgsqlDbType.Text));
        }

        Guid? autoClosed = null;
        if (m.EntityType == "SLEEP_SESSION" && env.OverlapPolicy == "ACCEPT_AND_WARN"
            && (fields.FirstOrDefault(f => f.Name == "end_at") is null or { Value: DBNull }))
        {
            autoClosed = await HandleOpenSleepAsync(env, m, fields, warnings, ct);
            if (autoClosed != null) resolution = "KEPT_BOTH";
        }

        var cols = string.Join(", ", fields.Select(f => f.Name));
        var ps = string.Join(", ", fields.Select((_, i) => "@p" + i));
        await using var c = Cmd(env,
            $"INSERT INTO {spec.Qualified} (id, baby_id, {cols}, created_by, last_modified_by) VALUES (@id, @b, {ps}, @u, @u) RETURNING version");
        P(c, "id", m.EntityId, NpgsqlDbType.Uuid); P(c, "b", env.BabyId, NpgsqlDbType.Uuid); P(c, "u", env.Ctx.UserId, NpgsqlDbType.Uuid);
        for (var i = 0; i < fields.Count; i++) P(c, "p" + i, fields[i].Value, fields[i].Type);
        var version = (long)(await c.ExecuteScalarAsync(ct))!;

        await UpsertClocksAsync(env, m, m.EntityId, fields.Select(f => f.Name), t, version, env.Ctx.DeviceId, m.MutationId, ct);

        if (m.EntityType == "SLEEP_SESSION")
        {
            var overlaps = await OverlapsAsync(env, m.EntityId, ct);
            if (overlaps.Count > 0)
            {
                warnings.Add(new PushWarning("SLEEP_OVERLAP", overlaps));
                resolution = "KEPT_BOTH";
            }
        }
        JsonObject? entity = null;
        if (resolution != null)
        {
            entity = Strip((await LoadRowAsync(env, spec, m.EntityId, false, ct))!);
            await AuditAsync(env, m, "sync.conflict_resolved", new JsonObject
            {
                ["entity_type"] = m.EntityType, ["resolution"] = resolution, ["base_version"] = 0, ["server_version"] = version,
                ["mutation_id"] = m.MutationId.ToString(), ["warnings"] = new JsonArray(warnings.Select(w => (JsonNode)w.Code).ToArray()),
            }, ct);
        }
        return new Applied(new MutationResult(m.MutationId, "APPLIED", m.EntityType, m.EntityId, version, resolution ?? "NONE",
            Warnings: warnings.Count > 0 ? warnings : null, Entity: entity), resolution == null ? "APPLIED" : "MERGED", version);
    }

    /// <summary>
    /// ACCEPT_AND_WARN + índice sleep_one_open_uq (INV-02): o banco só admite UMA sessão aberta por bebê, então
    /// "manter as duas" exige decidir quem fecha. Estratégia do spike: permanece aberta a de início mais recente;
    /// a outra é fechada no início dela (heurística, relógio de servidor mínimo para nunca vencer edição do usuário).
    /// </summary>
    private async Task<Guid?> HandleOpenSleepAsync(Env env, Mutation m, List<Field> fields, List<PushWarning> warnings, CancellationToken ct)
    {
        // sem linha para travar quando não há sessão aberta: serializa por bebê com lock consultivo (senão 23505 em corrida)
        await using (var lk = Cmd(env, "SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))"))
        {
            P(lk, "k", "open-sleep:" + env.BabyId, NpgsqlDbType.Text);
            await lk.ExecuteNonQueryAsync(ct);
        }
        Guid existing; DateTime existingStart;
        await using (var q = Cmd(env, "SELECT id, start_at FROM nina.sleep_session WHERE baby_id = @b AND end_at IS NULL AND deleted_at IS NULL FOR UPDATE"))
        {
            P(q, "b", env.BabyId, NpgsqlDbType.Uuid);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            existing = r.GetGuid(0); existingStart = r.GetDateTime(1);
        }
        var myStart = (DateTime)fields.First(f => f.Name == "start_at").Value!;
        warnings.Add(new PushWarning("OPEN_SLEEP_EXISTS", [existing]));
        if (existingStart <= myStart)
        {
            await using var u = Cmd(env, "UPDATE nina.sleep_session SET end_at = @e, last_modified_by = @u WHERE id = @id RETURNING version");
            P(u, "e", myStart, NpgsqlDbType.TimestampTz); P(u, "u", env.Ctx.UserId, NpgsqlDbType.Uuid); P(u, "id", existing, NpgsqlDbType.Uuid);
            var v = (long)(await u.ExecuteScalarAsync(ct))!;
            await UpsertClocksAsync(env, m, existing, ["end_at"], DateTimeOffset.UnixEpoch, v, ServerDevice, null, ct, overwriteTs: true);
        }
        else
        {
            var f = fields.FindIndex(x => x.Name == "end_at");
            var closed = new Field("end_at", existingStart, NpgsqlDbType.TimestampTz);
            if (f >= 0) fields[f] = closed; else fields.Add(closed);
        }
        return existing;
    }

    private static async Task<List<Guid>> OverlapsAsync(Env env, Guid sessionId, CancellationToken ct)
    {
        var l = new List<Guid>();
        await using var c = Cmd(env, "SELECT * FROM nina.sleep_overlaps(@id)");
        P(c, "id", sessionId, NpgsqlDbType.Uuid);
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) l.Add(r.GetGuid(0));
        return l;
    }

    private static void ValidateWake(JsonObject session, DateTime started, DateTime? ended)
    {
        var s = Ts(session, "start_at")!.Value.UtcDateTime;
        var e = Ts(session, "end_at")?.UtcDateTime;
        if (started < s || (e != null && (started > e || (ended != null && ended > e))))
            throw Invalid("started_at", "WAKE_OUTSIDE_SESSION");
    }

    // ------------------------------------------------------------------ UPDATE

    private async Task<Applied> UpdateAsync(Env env, Mutation m, EntitySpec spec, DateTimeOffset t, List<PushWarning> warnings, CancellationToken ct)
    {
        var cur = await LoadRowAsync(env, spec, m.EntityId, true, ct);
        if (cur == null || cur["baby_id"]!.GetValue<string>() != env.BabyId.ToString()) throw new RejectException("ENTITY_NOT_FOUND");
        if (cur["deleted_at"] is not null) throw Deleted();        // INV-20: não ressuscita
        var curVersion = cur["version"]!.GetValue<long>();
        if (m.BaseVersion > curVersion) throw new RejectException("VERSION_AHEAD");
        var patch = ParseData(spec, m.Data, create: false);

        // LWW por campo. Só há conflito para campos gravados por OUTRO dispositivo depois de base_version;
        // escritas anteriores do mesmo dispositivo (fila offline com base_version velho) são sucessão causal.
        var clocks = m.BaseVersion < curVersion ? await LoadClocksAsync(env, m.EntityId, ct) : [];
        var othersChanged = clocks.Values.Any(c => c.Version > m.BaseVersion && c.Device != env.Ctx.DeviceId);
        var winners = new List<Field>();
        var conflicts = new List<FieldConflict>();
        foreach (var f in patch)
        {
            if (clocks.TryGetValue(f.Name, out var clk) && clk.Version > m.BaseVersion && clk.Device != env.Ctx.DeviceId)
            {
                var clientWins = _opt.ConflictOrder == ConflictOrder.ServerArrival   // quem chega depois vence (o último a commitar)
                                 || t > clk.Ts || (t == clk.Ts && env.Ctx.DeviceId.CompareTo(clk.Device) > 0);
                conflicts.Add(new FieldConflict(f.Name, clientWins ? "CLIENT" : "SERVER"));
                if (clientWins) winners.Add(f);
            }
            else winners.Add(f);
        }
        string resolution =
            conflicts.Count == 0 ? (othersChanged ? "MERGED" : "NONE")
            : conflicts.All(c => c.Kept == "CLIENT") ? "LWW_CLIENT_WON"
            : conflicts.All(c => c.Kept == "SERVER") ? "LWW_SERVER_WON" : "MERGED";

        long version = curVersion;
        if (winners.Count > 0)
        {
            var sets = new List<string>(); var extra = new List<(string, object?, NpgsqlDbType)>();
            foreach (var w in winners) sets.Add($"{w.Name} = @w_{w.Name}");
            if (m.EntityType == "WAKE_EVENT")
            {
                var sess = await LoadRowAsync(env, EntityCatalog.Push["SLEEP_SESSION"], Guid.Parse(cur["sleep_session_id"]!.GetValue<string>()), false, ct);
                var ns = (DateTime?)winners.FirstOrDefault(w => w.Name == "started_at")?.Value ?? Ts(cur, "started_at")!.Value.UtcDateTime;
                var ne = winners.Any(w => w.Name == "ended_at") ? winners.First(w => w.Name == "ended_at").Value as DateTime? : Ts(cur, "ended_at")?.UtcDateTime;
                if (sess != null) ValidateWake(sess, ns, ne);
                var touchesTimes = winners.Any(w => w.Name is "started_at" or "ended_at");
                var src = cur["source"]!.GetValue<string>();
                if (touchesTimes && src != "MANUAL" && cur["manually_corrected"]!.GetValue<bool>() == false)
                {
                    // correção manual preserva o valor original (inferido/importado) e marca source=MANUAL
                    sets.Add("manually_corrected = true"); sets.Add("original_started_at = started_at"); sets.Add("original_ended_at = ended_at");
                    if (winners.All(w => w.Name != "source")) sets.Add("source = 'MANUAL'");
                }
            }
            await using var c = Cmd(env, $"UPDATE {spec.Qualified} SET {string.Join(", ", sets)}, last_modified_by = @u WHERE id = @id RETURNING version");
            foreach (var w in winners) P(c, "w_" + w.Name, w.Value, w.Type);
            P(c, "u", env.Ctx.UserId, NpgsqlDbType.Uuid); P(c, "id", m.EntityId, NpgsqlDbType.Uuid);
            version = (long)(await c.ExecuteScalarAsync(ct))!;
            await UpsertClocksAsync(env, m, m.EntityId, winners.Select(w => w.Name), t, version, env.Ctx.DeviceId, m.MutationId, ct);

            if (m.EntityType == "SLEEP_SESSION" && winners.Any(w => w.Name is "start_at" or "end_at"))
            {
                var overlaps = await OverlapsAsync(env, m.EntityId, ct);
                if (overlaps.Count > 0) { warnings.Add(new PushWarning("SLEEP_OVERLAP", overlaps)); if (resolution == "NONE") resolution = "KEPT_BOTH"; }
            }
        }
        JsonObject? entity = null;
        if (resolution != "NONE")
        {
            entity = Strip((await LoadRowAsync(env, spec, m.EntityId, false, ct))!);
            await AuditAsync(env, m, "sync.conflict_resolved", new JsonObject
            {
                ["entity_type"] = m.EntityType, ["resolution"] = resolution, ["base_version"] = m.BaseVersion, ["server_version"] = curVersion,
                ["mutation_id"] = m.MutationId.ToString(),
                ["fields"] = new JsonArray(patch.Select(p => (JsonNode)p.Name).ToArray()),
                ["kept"] = new JsonObject(conflicts.Select(c => KeyValuePair.Create(c.Field, (JsonNode?)c.Kept))),
            }, ct);
        }
        return new Applied(new MutationResult(m.MutationId, "APPLIED", m.EntityType, m.EntityId, version, resolution,
            conflicts.Count > 0 ? conflicts : null, warnings.Count > 0 ? warnings : null, entity),
            resolution == "NONE" ? "APPLIED" : "MERGED", version);
    }

    // ------------------------------------------------------------------ DELETE

    private async Task<Applied> DeleteAsync(Env env, Mutation m, EntitySpec spec, DateTimeOffset t, List<PushWarning> warnings, CancellationToken ct)
    {
        var cur = await LoadRowAsync(env, spec, m.EntityId, true, ct);
        if (cur == null || cur["baby_id"]!.GetValue<string>() != env.BabyId.ToString()) throw new RejectException("ENTITY_NOT_FOUND");
        if (cur["deleted_at"] is not null) throw Deleted();
        var curVersion = cur["version"]!.GetValue<long>();
        if (m.BaseVersion > curVersion) throw new RejectException("VERSION_AHEAD");
        var othersChanged = false;
        if (m.BaseVersion < curVersion)
            othersChanged = (await LoadClocksAsync(env, m.EntityId, ct)).Values.Any(c => c.Version > m.BaseVersion && c.Device != env.Ctx.DeviceId);

        await using var c = Cmd(env, $"UPDATE {spec.Qualified} SET deleted_at = now(), last_modified_by = @u WHERE id = @id RETURNING version");
        P(c, "u", env.Ctx.UserId, NpgsqlDbType.Uuid); P(c, "id", m.EntityId, NpgsqlDbType.Uuid);
        var version = (long)(await c.ExecuteScalarAsync(ct))!;
        var resolution = othersChanged ? "DELETE_WINS" : "NONE";
        if (othersChanged)
            await AuditAsync(env, m, "sync.conflict_resolved", new JsonObject
            {
                ["entity_type"] = m.EntityType, ["resolution"] = resolution, ["base_version"] = m.BaseVersion,
                ["server_version"] = curVersion, ["mutation_id"] = m.MutationId.ToString(),
            }, ct);
        return new Applied(new MutationResult(m.MutationId, "APPLIED", m.EntityType, m.EntityId, version, resolution,
            Warnings: warnings.Count > 0 ? warnings : null), othersChanged ? "MERGED" : "APPLIED", version);
    }
}
