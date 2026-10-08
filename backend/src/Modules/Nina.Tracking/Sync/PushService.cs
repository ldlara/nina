using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nina.SharedKernel.Data;
using Nina.SharedKernel.Http;
using Nina.SharedKernel.Security;
using Nina.Tracking.Domain;
using Nina.Tracking.Persistence;
using Nina.Tracking.Reads;
using Npgsql;
using NpgsqlTypes;

namespace Nina.Tracking.Sync;

/// <summary>
/// <c>POST /sync/push</c> (BE-004a, ADR-0003, contrato 1.0.1). Mutações agrupadas por bebê (ordem de <c>baby_id</c>, sem deadlock entre
/// bebês); uma transação por grupo com <c>SAVEPOINT</c> por mutação; serialização por bebê por lock consultivo (idempotência e
/// conflito sem corrida); idempotência por <c>(usuário, dispositivo, mutation_id)</c> em <c>sync_mutation</c>; conflito por campo com
/// <c>field_versions</c> e ordem de chegada ao servidor (<c>client_created_at</c> é só informativo, SR-014).
/// </summary>
internal sealed partial class PushService(
    NpgsqlDataSource dataSource,
    TimeProvider time,
    IRequestContext request,
    SecretKeys keys,
    IOptions<TrackingOptions> options,
    ILogger<PushService> logger)
{
    private const int MaxAttempts = 5;

    public async Task<JsonObject> PushAsync(Guid userId, ParsedPush push, CancellationToken cancellationToken)
    {
        var groups = push.Mutations.GroupBy(m => m.BabyId).OrderBy(g => g.Key).ToList();
        await EnforceDailyQuotaAsync(userId, push.DeviceId, groups.Select(g => g.Key).ToList(), cancellationToken);

        var results = new JsonObject[push.Mutations.Count];
        foreach (var group in groups)
        {
            var items = group.OrderBy(m => m.Index).ToList();
            var outcome = await RunGroupWithRetryAsync(userId, push.DeviceId, group.Key, items, cancellationToken);
            foreach (var (index, result) in outcome)
            {
                results[index] = result;
            }
        }

        var array = new JsonArray();
        foreach (var result in results)
        {
            array.Add(result);
        }

        return new JsonObject { ["server_time"] = Wire.Instant(time.GetUtcNow()), ["results"] = array };
    }

    // ---------------------------------------------------------------- cota diária (429 do lote inteiro)

    private async Task EnforceDailyQuotaAsync(Guid userId, Guid deviceId, List<Guid> babies, CancellationToken ct)
    {
        await using var tx = await TrackingTx.BeginAsync(dataSource, userId, deviceId, IsolationLevel.ReadCommitted, ct);
        var limit = await tx.ScalarAsync<int>(
            "SELECT nina.param_int('limits.sync_max_events_per_baby_per_day', @def)",
            Db.P("def", options.Value.DefaultMaxEventsPerBabyPerDay, NpgsqlDbType.Integer));
        var used = await tx.QueryAsync(
            """
            SELECT baby_id, count(*) FROM nina.sync_mutation
             WHERE baby_id = ANY(@babies) AND outcome <> 'REJECTED'
               AND received_at >= (date_trunc('day', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC')
             GROUP BY baby_id
            """,
            r => (Baby: r.GetGuid(0), Count: r.GetInt64(1)),
            new NpgsqlParameter("babies", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = babies.ToArray() });
        if (used.Any(u => u.Count >= limit))
        {
            var retryAfter = await tx.ScalarAsync<double>(
                "SELECT extract(epoch FROM ((date_trunc('day', now() AT TIME ZONE 'UTC') + interval '1 day') AT TIME ZONE 'UTC') - now())");
            throw new ProblemException(StatusCodes.Status429TooManyRequests, "QUOTA_EXCEEDED", "Daily quota exceeded")
            {
                RetryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter)),
            };
        }
    }

    // ---------------------------------------------------------------- grupo (um bebê)

    private async Task<Dictionary<int, JsonObject>> RunGroupWithRetryAsync(
        Guid userId, Guid deviceId, Guid babyId, List<PushMutation> items, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return await RunGroupAsync(userId, deviceId, babyId, items, ct);
            }
            catch (Exception ex) when (ex is GroupRetryException || (ex is PostgresException pg && IsRetriable(pg)))
            {
                LogRetry(logger, attempt, (ex as PostgresException)?.SqlState ?? "concurrent");
                await Task.Delay(TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(10, 40) * attempt), ct);
            }
        }

        // Esgotou as tentativas: nada do grupo foi gravado (transação desfeita); o cliente reenvia (idempotente).
        var now = time.GetUtcNow();
        return items.ToDictionary(m => m.Index, m => RejectedResult(m, new Rejection("TRANSIENT", Retryable: true, Persist: false), now));
    }

    private static bool IsRetriable(PostgresException ex) =>
        ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure
            or PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.QueryCanceled;

    private async Task<Dictionary<int, JsonObject>> RunGroupAsync(
        Guid userId, Guid deviceId, Guid babyId, List<PushMutation> items, CancellationToken ct)
    {
        await using var tx = await TrackingTx.BeginAsync(dataSource, userId, deviceId, IsolationLevel.ReadCommitted, ct);
        var results = new Dictionary<int, JsonObject>();
        var access = await BabyAccess.ResolveAsync(tx, babyId);
        if (!access.CanWrite)
        {
            // Autorização por mutação, nada é gravado (nem em sync_mutation: a RLS de INSERT exige escrita no bebê).
            var code = access.State switch
            {
                AccessState.Revoked => "ACCESS_REVOKED",
                AccessState.Active => "FORBIDDEN_ROLE",
                _ => "BABY_NOT_FOUND",
            };
            var now = time.GetUtcNow();
            return items.ToDictionary(m => m.Index, m => RejectedResult(m, new Rejection(code, Persist: false), now));
        }

        // Serializa os escritores do MESMO bebê desde o início: a checagem de idempotência, a de conflito e a de sobreposição
        // enxergam sempre o estado já confirmado pelo grupo anterior (a sequência do bebê já seria serializada no 1º INSERT).
        await tx.ExecAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))",
            Db.Text("key", "nina.sync.baby:" + babyId.ToString("N")));

        var group = await Group.OpenAsync(tx, userId, deviceId, babyId, items, options.Value);
        foreach (var mutation in items)
        {
            results[mutation.Index] = await ProcessOneAsync(group, mutation);
        }

        await WriteAuditAsync(group);
        await tx.CommitAsync();
        return results;
    }

    // ---------------------------------------------------------------- uma mutação

    private static async Task<JsonObject> ProcessOneAsync(Group g, PushMutation m)
    {
        var spec = EntityCatalog.For(m.EntityType);
        var prior = await LoadPriorAsync(g, m.MutationId);
        if (prior is not null)
        {
            return await ReplayAsync(g, spec, m, prior);
        }

        var received = new DateTimeOffset(await g.Tx.ScalarAsync<DateTime>("SELECT clock_timestamp()"), TimeSpan.Zero);
        var savepoint = await g.Tx.SavepointAsync();
        try
        {
            var applied = m.Op switch
            {
                "CREATE" => await ApplyCreateAsync(g, spec, m, received),
                "UPDATE" => await ApplyUpdateAsync(g, spec, m, received),
                _ => await ApplyDeleteAsync(g, spec, m),
            };
            await RecordAsync(g, m, applied.Outcome, applied.Version, null, applied.Resolution, received);
            await g.Tx.ReleaseAsync(savepoint);
            return await AppliedResultAsync(g, spec, m, applied, received, "APPLIED");
        }
        catch (RejectException ex)
        {
            await g.Tx.RollbackToAsync(savepoint);
            return await RejectAsync(g, m, ex.Rejection, received);
        }
        catch (PostgresException ex) when (MapDatabaseError(ex) is { } rejection)
        {
            await g.Tx.RollbackToAsync(savepoint);
            return await RejectAsync(g, m, rejection, received);
        }
    }

    private static async Task<PriorMutation?> LoadPriorAsync(Group g, Guid mutationId) =>
        await g.Tx.QueryFirstAsync(
            """
            SELECT baby_id, entity_type, entity_id, op, outcome, result_version, reject_code, resolution, received_at
              FROM nina.sync_mutation
             WHERE user_id = nina.current_user_id() AND device_id = @device AND mutation_id = @mutation
            """,
            r => new PriorMutation(
                r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetInt64(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                r.GetFieldValue<DateTimeOffset>(8).ToUniversalTime()),
            Db.Uuid("device", g.DeviceId), Db.Uuid("mutation", mutationId));

    private static async Task RecordAsync(
        Group g, PushMutation m, string outcome, long? version, string? rejectCode, string? resolution, DateTimeOffset received)
    {
        var inserted = await g.Tx.ExecAsync(
            """
            INSERT INTO nina.sync_mutation (mutation_id, baby_id, user_id, device_id, entity_type, entity_id, op, base_version,
                                            client_created_at, received_at, outcome, result_version, reject_code, resolution)
            VALUES (@mutation, @baby, nina.current_user_id(), @device, @type, @entity, @op, @base, @client, @received, @outcome,
                    @version, @code, @resolution)
            """,
            Db.Uuid("mutation", m.MutationId), Db.Uuid("baby", g.BabyId), Db.Uuid("device", g.DeviceId),
            Db.Text("type", m.EntityType), Db.Uuid("entity", m.EntityId), Db.Text("op", m.Op),
            Db.P("base", m.BaseVersion, NpgsqlDbType.Bigint), Db.Timestamp("client", m.ClientCreatedAt), Db.Timestamp("received", received),
            Db.Text("outcome", outcome), Db.P("version", version, NpgsqlDbType.Bigint), Db.Text("code", rejectCode), Db.Text("resolution", resolution));
        if (inserted != 1)
        {
            throw new InvalidOperationException("sync_mutation não gravada");
        }
    }

    // ---------------------------------------------------------------- reenvio (DUPLICATE) e rejeição

    private static async Task<JsonObject> ReplayAsync(Group g, EntitySpec spec, PushMutation m, PriorMutation prior)
    {
        if (prior.BabyId != m.BabyId || prior.EntityId != m.EntityId || prior.Op != m.Op
            || !string.Equals(prior.EntityType, m.EntityType, StringComparison.Ordinal))
        {
            // O mesmo mutation_id para outra operação: nunca reaplica nem devolve o resultado alheio.
            return RejectedResult(m, new Rejection("VALIDATION_FAILED", [new FieldError($"mutations[{m.Index}].mutation_id", "REUSED")], Persist: false), prior.ReceivedAt);
        }

        if (prior.Outcome is "REJECTED" or "IGNORED_TOMBSTONE")
        {
            return RejectedResult(m, new Rejection(prior.RejectCode ?? "VALIDATION_FAILED", Persist: false), prior.ReceivedAt);
        }

        var row = await LoadRowAsync(g.Tx, spec, g.BabyId, m.EntityId);
        var live = row is not null && row["deleted_at"] is null;
        var resolution = prior.Resolution ?? (prior.Outcome == "MERGED" ? "MERGED" : "NONE");
        var warnings = new List<JsonObject>();
        if (live && spec.Type == EntityCatalog.SleepSession && resolution == "KEPT_BOTH")
        {
            warnings.AddRange(await SleepWarningsAsync(g, m.EntityId, Wire.ReadInstant(row!["end_at"]) is null));
        }

        var applied = new AppliedMutation(prior.ResultVersion ?? 0, resolution, prior.Outcome, [], warnings, live ? row : null, !live);
        return await AppliedResultAsync(g, spec, m, applied, prior.ReceivedAt, "DUPLICATE");
    }

    private static async Task<JsonObject> RejectAsync(Group g, PushMutation m, Rejection rejection, DateTimeOffset received)
    {
        if (rejection.Persist && ProblemCatalog.IsDeterministic(rejection.Code))
        {
            var outcome = rejection.Code == "ENTITY_DELETED" ? "IGNORED_TOMBSTONE" : "REJECTED";
            await RecordAsync(g, m, outcome, null, rejection.Code, null, received);
        }

        return RejectedResult(m, rejection, received);
    }

    private static JsonObject RejectedResult(PushMutation m, Rejection rejection, DateTimeOffset received) => new()
    {
        ["mutation_id"] = m.MutationId.ToString("D"),
        ["status"] = "REJECTED",
        ["entity_type"] = m.EntityType,
        ["entity_id"] = m.EntityId.ToString("D"),
        ["server_received_at"] = Wire.Instant(received),
        ["retryable"] = rejection.Retryable,
        ["problem"] = ProblemCatalog.Problem(rejection.Code, rejection.Errors),
    };

    private static async Task<JsonObject> AppliedResultAsync(Group g, EntitySpec spec, PushMutation m, AppliedMutation applied, DateTimeOffset received, string status)
    {
        var result = new JsonObject
        {
            ["mutation_id"] = m.MutationId.ToString("D"),
            ["status"] = status,
            ["entity_type"] = m.EntityType,
            ["entity_id"] = m.EntityId.ToString("D"),
            ["server_received_at"] = Wire.Instant(received),
            ["version"] = applied.Version,
            ["resolution"] = applied.Resolution,
        };
        if (applied.Conflicts.Count > 0)
        {
            var conflicts = new JsonArray();
            foreach (var c in applied.Conflicts)
            {
                conflicts.Add(new JsonObject { ["field"] = c.Field, ["kept"] = c.Kept });
            }

            result["conflicts"] = conflicts;
        }

        if (applied.Warnings.Count > 0)
        {
            var warnings = new JsonArray();
            foreach (var w in applied.Warnings)
            {
                warnings.Add(w.DeepClone());
            }

            result["warnings"] = warnings;
        }

        // Estado canônico: sempre no DUPLICATE; no APPLIED, quando houve resolução (o cliente reconcilia sem novo pull).
        if (status == "DUPLICATE" || applied.Resolution != "NONE")
        {
            result["entity"] = applied.EntityRow is { } row ? await EntityAsync(g, spec, row) : null;
        }

        return result;
    }

    private static async Task<JsonNode> EntityAsync(Group g, EntitySpec spec, JsonObject row)
    {
        if (spec.Type == EntityCatalog.WakeEvent)
        {
            return EntityMapper.Wake(row);
        }

        var authors = g.Authors ??= await BabyAccess.LoadAuthorsAsync(g.Tx, g.BabyId);
        int? nights = null;
        if (spec.Type == EntityCatalog.SleepSession)
        {
            nights = await g.Tx.ScalarAsync<int?>(
                "SELECT nina.night_awakenings(@baby, @id)", Db.Uuid("baby", g.BabyId), Db.Uuid("id", Wire.ReadGuid(row["id"])));
        }

        return EntityMapper.Event(spec, row, authors, nights);
    }

    // ---------------------------------------------------------------- CREATE

    private static async Task<AppliedMutation> ApplyCreateAsync(Group g, EntitySpec spec, PushMutation m, DateTimeOffset received)
    {
        if (m.BaseVersion != 0)
        {
            throw RejectException.Of("VALIDATION_FAILED", new FieldError("base_version", "OUT_OF_RANGE"));
        }

        var parsed = DataValidator.Parse(spec, m.Data, create: true, g.Limits);
        if (spec.Type == EntityCatalog.WakeEvent && !parsed.Values.ContainsKey("source"))
        {
            parsed.Values["source"] = "MANUAL";
        }

        if (parsed.Errors.Count == 0)
        {
            DataValidator.ValidateShape(spec, parsed.Values, parsed.Errors);
        }

        if (parsed.Errors.Count > 0)
        {
            throw RejectException.Validation(parsed.Errors);
        }

        var existing = await g.Tx.ScalarAsync<bool?>(
            $"SELECT deleted_at IS NOT NULL FROM nina.{spec.Table} WHERE baby_id = @baby AND id = @id",
            Db.Uuid("baby", g.BabyId), Db.Uuid("id", m.EntityId));
        if (existing is { } isDeleted)
        {
            // O id já existe neste bebê (PK escopada por bebê, R-04): um tombstone nunca ressuscita (INV-20); senão o id não está disponível.
            throw RejectException.Of(isDeleted ? "ENTITY_DELETED" : "ENTITY_ID_UNAVAILABLE");
        }

        await EnsureEntityQuotaAsync(g);
        if (spec.Type == EntityCatalog.WakeEvent)
        {
            await ValidateWakeAgainstSessionAsync(g, parsed.Values);
        }

        var columns = new List<string> { "id", "baby_id" };
        var placeholders = new List<string> { "@id", "@baby" };
        var parameters = new List<NpgsqlParameter> { Db.Uuid("id", m.EntityId), Db.Uuid("baby", g.BabyId) };
        foreach (var (name, value) in parsed.Values)
        {
            columns.Add(name);
            placeholders.Add("@p_" + name);
            parameters.Add(ValueParam(spec.Field(name)!, "p_" + name, value));
        }

        var row = await g.Tx.QueryJsonFirstAsync(
            $"INSERT INTO nina.{spec.Table} AS t ({string.Join(", ", columns)}) VALUES ({string.Join(", ", placeholders)}) RETURNING to_jsonb(t)",
            [.. parameters]) ?? throw new InvalidOperationException("INSERT sem retorno");
        g.LiveEntities++;

        var version = Wire.ReadLong(row["version"])!.Value;
        var warnings = new List<JsonObject>();
        if (spec.Type == EntityCatalog.SleepSession && g.OverlapPolicy != "REJECT")
        {
            warnings.AddRange(await SleepWarningsAsync(g, m.EntityId, Wire.ReadInstant(row["end_at"]) is null));
        }

        AddClockSkew(g, m, received, warnings);
        var resolution = warnings.Any(w => w["code"]!.GetValue<string>() is "SLEEP_OVERLAP" or "OPEN_SLEEP_EXISTS") ? "KEPT_BOTH" : "NONE";
        if (resolution != "NONE")
        {
            g.Audits.Add(new AuditNote(m.EntityType, m.EntityId, resolution, [], null, version));
        }

        return new AppliedMutation(version, resolution, resolution == "NONE" ? "APPLIED" : "MERGED", [], warnings, row, false);
    }

    // ---------------------------------------------------------------- UPDATE

    private static async Task<AppliedMutation> ApplyUpdateAsync(Group g, EntitySpec spec, PushMutation m, DateTimeOffset received)
    {
        var row = await LoadRowAsync(g.Tx, spec, g.BabyId, m.EntityId);
        if (row is null)
        {
            throw RejectException.Of("ENTITY_NOT_FOUND");
        }

        if (row["deleted_at"] is not null)
        {
            throw RejectException.Of("ENTITY_DELETED");           // INV-20: update atrasado não ressuscita
        }

        var current = Wire.ReadLong(row["version"])!.Value;
        if (m.BaseVersion > current)
        {
            throw RejectException.Of("VERSION_AHEAD");
        }

        var parsed = DataValidator.Parse(spec, m.Data, create: false, g.Limits);
        if (parsed.Errors.Count > 0)
        {
            throw RejectException.Validation(parsed.Errors);
        }

        var state = ValuesOf(spec, row);
        var fieldVersions = FieldVersionsOf(row);

        // Campos sem mudança real (mesmo valor) não entram na resolução nem na escrita.
        var patch = parsed.Values.Where(kv => !Equals(kv.Value, state.GetValueOrDefault(kv.Key))).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var conflictsByOthers = ConcurrentFields(fieldVersions, m.BaseVersion, g.DeviceId);

        var conflicts = new List<FieldConflictInfo>();
        var dropped = new HashSet<string>(StringComparer.Ordinal);

        // Valida o estado resultante. Se a combinação só é inválida por causa de edição concorrente de outro aparelho no mesmo grupo
        // atômico (R-12), o servidor mantém o valor já aplicado desse grupo (LWW_SERVER_WON) em vez de rejeitar e perder a edição inteira.
        var candidate = Merge(state, patch, dropped);
        var shape = new List<FieldError>();
        DataValidator.ValidateShape(spec, candidate, shape);
        if (shape.Count > 0)
        {
            foreach (var group in spec.Groups)
            {
                if (group.Any(patch.ContainsKey) && group.Any(conflictsByOthers.Contains))
                {
                    foreach (var field in group.Where(patch.ContainsKey))
                    {
                        dropped.Add(field);
                    }
                }
            }

            candidate = Merge(state, patch, dropped);
            shape.Clear();
            DataValidator.ValidateShape(spec, candidate, shape);
            if (shape.Count > 0 || dropped.Count == 0)
            {
                throw RejectException.Validation(shape);
            }
        }

        var effective = patch.Where(kv => !dropped.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        foreach (var field in dropped)
        {
            conflicts.Add(new FieldConflictInfo(field, "SERVER"));
        }

        foreach (var field in effective.Keys)
        {
            var group = spec.GroupOf(field);
            if (conflictsByOthers.Contains(field) || (group is not null && group.Any(conflictsByOthers.Contains)))
            {
                conflicts.Add(new FieldConflictInfo(field, "CLIENT"));
            }
        }

        var touched = new HashSet<string>(effective.Keys.Concat(dropped), StringComparer.Ordinal);
        var disjointOthers = conflictsByOthers.Any(f => !touched.Contains(f) && !(spec.GroupOf(f) is { } gr && gr.Any(touched.Contains)));
        var resolution = conflicts.Count > 0
            ? (conflicts.All(c => c.Kept == "CLIENT") ? "LWW_CLIENT_WON" : conflicts.All(c => c.Kept == "SERVER") ? "LWW_SERVER_WON" : "MERGED")
            : (disjointOthers ? "MERGED" : "NONE");

        if (spec.Type == EntityCatalog.WakeEvent && (effective.ContainsKey("started_at") || effective.ContainsKey("ended_at")))
        {
            await ValidateWakeAgainstSessionAsync(g, Merge(state, effective, []));
        }

        JsonObject resultRow = row;
        var version = current;
        if (effective.Count > 0)
        {
            resultRow = await WriteUpdateAsync(g, spec, m, row, state, effective, fieldVersions, current);
            version = Wire.ReadLong(resultRow["version"])!.Value;
        }

        var warnings = new List<JsonObject>();
        if (effective.Count > 0 && spec.Type == EntityCatalog.SleepSession && g.OverlapPolicy != "REJECT"
            && (effective.ContainsKey("start_at") || effective.ContainsKey("end_at")))
        {
            warnings.AddRange(await SleepWarningsAsync(g, m.EntityId, Wire.ReadInstant(resultRow["end_at"]) is null));
        }

        AddClockSkew(g, m, received, warnings);
        if (resolution is "NONE" or "MERGED" && warnings.Any(w => w["code"]!.GetValue<string>() is "SLEEP_OVERLAP" or "OPEN_SLEEP_EXISTS"))
        {
            resolution = "KEPT_BOTH";
        }

        if (resolution != "NONE")
        {
            g.Audits.Add(new AuditNote(m.EntityType, m.EntityId, resolution, [.. touched.Order(StringComparer.Ordinal)], m.BaseVersion, version));
        }

        return new AppliedMutation(version, resolution, resolution == "NONE" ? "APPLIED" : "MERGED", conflicts, warnings, resultRow, false);
    }

    private static async Task<JsonObject> WriteUpdateAsync(
        Group g, EntitySpec spec, PushMutation m, JsonObject row, Dictionary<string, object?> state,
        Dictionary<string, object?> effective, JsonObject fieldVersions, long current)
    {
        var assignments = new List<string>();
        var parameters = new List<NpgsqlParameter>
        {
            Db.Uuid("baby", g.BabyId), Db.Uuid("id", m.EntityId), Db.P("expected", current, NpgsqlDbType.Bigint),
        };
        foreach (var (name, value) in effective)
        {
            assignments.Add($"{name} = @p_{name}");
            parameters.Add(ValueParam(spec.Field(name)!, "p_" + name, value));
        }

        // field_versions guarda, por campo, a versão que a entidade TINHA antes desta mudança e o dispositivo que a fez. Como as
        // versões da entidade são números de sequência crescentes, "alguém mudou o campo depois de base_version" equivale a
        // (versão anterior >= base_version): não depende de conhecer a versão nova, que só o gatilho do banco atribui.
        var updated = (JsonObject)fieldVersions.DeepClone();
        foreach (var name in effective.Keys)
        {
            updated[name] = new JsonArray(current, g.DeviceId.ToString("D"));
        }

        assignments.Add("field_versions = @fv");
        parameters.Add(Db.P("fv", updated.ToJsonString(), NpgsqlDbType.Jsonb));

        if (spec.Type == EntityCatalog.WakeEvent && (effective.ContainsKey("started_at") || effective.ContainsKey("ended_at")))
        {
            // Correção manual de despertar inferido/importado: marca MANUAL e preserva o valor original (ADR-0009).
            if (!effective.ContainsKey("source") && !string.Equals(state.GetValueOrDefault("source") as string, "MANUAL", StringComparison.Ordinal))
            {
                assignments.Add("source = 'MANUAL'");
            }

            if (row["manually_corrected"]?.GetValue<bool>() != true)
            {
                assignments.Add("manually_corrected = true");
                assignments.Add("original_started_at = started_at");
                assignments.Add("original_ended_at = ended_at");
            }
        }

        var updatedRow = await g.Tx.QueryJsonFirstAsync(
            $"UPDATE nina.{spec.Table} AS t SET {string.Join(", ", assignments)} WHERE t.baby_id = @baby AND t.id = @id AND t.version = @expected RETURNING to_jsonb(t)",
            [.. parameters]);
        return updatedRow ?? throw new GroupRetryException();
    }

    // ---------------------------------------------------------------- DELETE

    private static async Task<AppliedMutation> ApplyDeleteAsync(Group g, EntitySpec spec, PushMutation m)
    {
        var row = await LoadRowAsync(g.Tx, spec, g.BabyId, m.EntityId);
        if (row is null)
        {
            throw RejectException.Of("ENTITY_NOT_FOUND");
        }

        if (row["deleted_at"] is not null)
        {
            throw RejectException.Of("ENTITY_DELETED");
        }

        var current = Wire.ReadLong(row["version"])!.Value;
        if (m.BaseVersion > current)
        {
            throw RejectException.Of("VERSION_AHEAD");
        }

        // Exclusão concorrente com edição de outro aparelho depois de base_version: a exclusão prevalece (D-36), auditada.
        var editedByOthers = ConcurrentFields(FieldVersionsOf(row), m.BaseVersion, g.DeviceId).Count > 0;
        var deleted = await g.Tx.QueryJsonFirstAsync(
            $"UPDATE nina.{spec.Table} AS t SET deleted_at = now() WHERE t.baby_id = @baby AND t.id = @id AND t.version = @expected RETURNING to_jsonb(t)",
            Db.Uuid("baby", g.BabyId), Db.Uuid("id", m.EntityId), Db.P("expected", current, NpgsqlDbType.Bigint))
            ?? throw new GroupRetryException();
        g.LiveEntities--;
        var version = Wire.ReadLong(deleted["version"])!.Value;
        var resolution = editedByOthers ? "DELETE_WINS" : "NONE";
        if (editedByOthers)
        {
            g.Audits.Add(new AuditNote(m.EntityType, m.EntityId, resolution, [], m.BaseVersion, version));
        }

        return new AppliedMutation(version, resolution, editedByOthers ? "MERGED" : "APPLIED", [], [], null, true);
    }

    // ---------------------------------------------------------------- regras auxiliares

    /// <summary>Campos alterados por OUTRO dispositivo depois de <paramref name="baseVersion"/>.</summary>
    private static HashSet<string> ConcurrentFields(JsonObject fieldVersions, long baseVersion, Guid device)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (field, entry) in fieldVersions)
        {
            if (entry is JsonArray { Count: 2 } pair
                && Wire.ReadLong(pair[0]) is { } previous && previous >= baseVersion
                && Wire.ReadGuid(pair[1]) is { } by && by != device)
            {
                result.Add(field);
            }
        }

        return result;
    }

    private static JsonObject FieldVersionsOf(JsonObject row) => row["field_versions"] as JsonObject ?? [];

    private static Dictionary<string, object?> Merge(Dictionary<string, object?> state, Dictionary<string, object?> patch, HashSet<string> dropped)
    {
        var merged = new Dictionary<string, object?>(state, StringComparer.Ordinal);
        foreach (var (name, value) in patch)
        {
            if (!dropped.Contains(name))
            {
                merged[name] = value;
            }
        }

        return merged;
    }

    private static Dictionary<string, object?> ValuesOf(EntitySpec spec, JsonObject row)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in spec.Fields)
        {
            var node = row[field.Name];
            values[field.Name] = field.Kind switch
            {
                FieldKind.Instant => Wire.ReadInstant(node),
                FieldKind.Int => Wire.ReadLong(node) is { } l ? (int)l : null,
                FieldKind.Guid => Wire.ReadGuid(node),
                _ => Wire.ReadString(node),
            };
        }

        return values;
    }

    private static NpgsqlParameter ValueParam(FieldSpec field, string name, object? value) => field.Kind switch
    {
        FieldKind.Instant => Db.P(name, (value as DateTimeOffset?)?.UtcDateTime, NpgsqlDbType.TimestampTz),
        FieldKind.Int => Db.P(name, value, NpgsqlDbType.Integer),
        FieldKind.Guid => Db.Uuid(name, value as Guid?),
        _ => Db.Text(name, value as string),
    };

    private static Task<JsonObject?> LoadRowAsync(TrackingTx tx, EntitySpec spec, Guid babyId, Guid id) =>
        tx.QueryJsonFirstAsync(
            $"SELECT to_jsonb(t) FROM nina.{spec.Table} t WHERE t.baby_id = @baby AND t.id = @id",
            Db.Uuid("baby", babyId), Db.Uuid("id", id));

    private static async Task ValidateWakeAgainstSessionAsync(Group g, Dictionary<string, object?> values)
    {
        var sessionId = (Guid)values["sleep_session_id"]!;
        var session = await g.Tx.QueryJsonFirstAsync(
            "SELECT to_jsonb(s) FROM nina.sleep_session s WHERE s.baby_id = @baby AND s.id = @id",
            Db.Uuid("baby", g.BabyId), Db.Uuid("id", sessionId));
        if (session is null)
        {
            throw RejectException.Of("ENTITY_NOT_FOUND");         // inexistente ou de outro bebê: indistinguível
        }

        if (session["deleted_at"] is not null)
        {
            throw RejectException.Of("ENTITY_DELETED");
        }

        var errors = new List<FieldError>();
        var start = (DateTimeOffset)values["started_at"]!;
        var end = (DateTimeOffset)values["ended_at"]!;
        var sessionStart = Wire.RequireInstant(session["start_at"]);
        var sessionEnd = Wire.ReadInstant(session["end_at"]);
        if (start < sessionStart)
        {
            errors.Add(new FieldError("data.started_at", "OUTSIDE_SESSION"));
        }

        if (sessionEnd is { } closes && end > closes)
        {
            errors.Add(new FieldError("data.ended_at", "OUTSIDE_SESSION"));
        }

        if (errors.Count > 0)
        {
            throw RejectException.Validation(errors);
        }
    }

    private static async Task<List<JsonObject>> SleepWarningsAsync(Group g, Guid sessionId, bool isOpen)
    {
        var warnings = new List<JsonObject>();
        var overlaps = await g.Tx.QueryAsync(
            "SELECT o FROM nina.sleep_overlaps(@baby, @id) o", r => r.GetGuid(0), Db.Uuid("baby", g.BabyId), Db.Uuid("id", sessionId));
        var otherOpen = isOpen
            ? await g.Tx.QueryAsync(
                "SELECT id FROM nina.sleep_session WHERE baby_id = @baby AND id <> @id AND end_at IS NULL AND deleted_at IS NULL ORDER BY start_at, id",
                r => r.GetGuid(0), Db.Uuid("baby", g.BabyId), Db.Uuid("id", sessionId))
            : [];
        var closedOverlaps = overlaps.Except(otherOpen).ToList();
        if (closedOverlaps.Count > 0)
        {
            warnings.Add(Warning("SLEEP_OVERLAP", closedOverlaps));
        }

        if (otherOpen.Count > 0)
        {
            warnings.Add(Warning("OPEN_SLEEP_EXISTS", otherOpen));
        }

        return warnings;
    }

    private static JsonObject Warning(string code, IEnumerable<Guid> related)
    {
        var ids = new JsonArray();
        foreach (var id in related)
        {
            ids.Add(id.ToString("D"));
        }

        return new JsonObject { ["code"] = code, ["related_entity_ids"] = ids };
    }

    private static void AddClockSkew(Group g, PushMutation m, DateTimeOffset received, List<JsonObject> warnings)
    {
        var skew = (long)Math.Round((m.ClientCreatedAt - received).TotalSeconds, MidpointRounding.AwayFromZero);
        if (Math.Abs(skew) > g.ClockSkewToleranceSeconds)
        {
            warnings.Add(new JsonObject
            {
                ["code"] = "CLIENT_CLOCK_SKEW",
                ["skew_seconds"] = skew,
                ["tolerance_seconds"] = g.ClockSkewToleranceSeconds,
            });
        }
    }

    private static async Task EnsureEntityQuotaAsync(Group g)
    {
        g.LiveEntities ??= await CountLiveEntitiesAsync(g);
        if (g.LiveEntities >= g.MaxEntities)
        {
            throw new RejectException(new Rejection("ENTITY_QUOTA_EXCEEDED", Persist: false));
        }
    }

    private static async Task<long> CountLiveEntitiesAsync(Group g)
    {
        var counts = EntityCatalog.All.Select(s => $"(SELECT count(*) FROM nina.{s.Table} WHERE baby_id = @baby AND deleted_at IS NULL)");
        return await g.Tx.ScalarAsync<long>($"SELECT {string.Join(" + ", counts)}", Db.Uuid("baby", g.BabyId));
    }

    private static Rejection? MapDatabaseError(PostgresException ex) => ex.SqlState switch
    {
        "NN002" => new Rejection("ENTITY_DELETED"),
        "NN006" => new Rejection("SLEEP_OVERLAP"),
        "NN003" => new Rejection("BABY_NOT_FOUND", Persist: false),
        PostgresErrorCodes.UniqueViolation => new Rejection("ENTITY_ID_UNAVAILABLE"),
        PostgresErrorCodes.InsufficientPrivilege => new Rejection("FORBIDDEN_ROLE", Persist: false),
        PostgresErrorCodes.CheckViolation => new Rejection("VALIDATION_FAILED", [new FieldError(FieldOf(ex), "INVALID_VALUE")]),
        PostgresErrorCodes.InvalidParameterValue or PostgresErrorCodes.InvalidTextRepresentation or PostgresErrorCodes.NumericValueOutOfRange
            or PostgresErrorCodes.DatetimeFieldOverflow or PostgresErrorCodes.InvalidDatetimeFormat or PostgresErrorCodes.StringDataRightTruncation =>
            new Rejection("VALIDATION_FAILED", [new FieldError(FieldOf(ex), "INVALID_VALUE")]),
        _ => null,
    };

    private static string FieldOf(PostgresException ex)
    {
        var text = (ex.ConstraintName ?? string.Empty) + " " + ex.MessageText;
        if (text.Contains("time zone", StringComparison.OrdinalIgnoreCase) || text.Contains("iana_tz", StringComparison.OrdinalIgnoreCase))
        {
            return "data.tz";
        }

        return ex.ConstraintName switch
        {
            "sleep_interval_ck" or "feeding_interval_ck" or "pumping_interval_ck" => "data.end_at",
            "wake_interval_ck" => "data.ended_at",
            _ => "data",
        };
    }

    private async Task WriteAuditAsync(Group g)
    {
        if (g.Audits.Count == 0)
        {
            return;
        }

        var ip = request.ClientIp is { } address ? keys.Hmac("audit-ip", address.ToString()) : null;
        foreach (var note in g.Audits)
        {
            var fields = new JsonArray();
            foreach (var f in note.Fields)
            {
                fields.Add(f);
            }

            // Só nomes de campos, nunca valores (INV-28): o texto de `notes` jamais chega à auditoria.
            var metadata = new JsonObject
            {
                ["entity_type"] = note.EntityType,
                ["fields"] = fields,
                ["resolution"] = note.Resolution,
                ["version"] = note.Version,
            };
            if (note.BaseVersion is { } baseVersion)
            {
                metadata["base_version"] = baseVersion;
            }

            await g.Tx.ExecAsync(
                "SELECT nina.audit('sync.conflict_resolved', @type, @entity, @baby, @device, @request, 'SUCCESS', @ip, @meta)",
                Db.Text("type", note.EntityType), Db.Uuid("entity", note.EntityId), Db.Uuid("baby", g.BabyId), Db.Uuid("device", g.DeviceId),
                Db.Text("request", request.RequestId), Db.Bytes("ip", ip), Db.P("meta", metadata.ToJsonString(), NpgsqlDbType.Jsonb));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sync push group retry (attempt {Attempt}, {State})")]
    private static partial void LogRetry(ILogger logger, int attempt, string state);

    // ---------------------------------------------------------------- contexto do grupo

    private sealed class Group
    {
        private Group(TrackingTx tx, Guid userId, Guid deviceId, Guid babyId)
        {
            Tx = tx;
            UserId = userId;
            DeviceId = deviceId;
            BabyId = babyId;
        }

        public TrackingTx Tx { get; }

        public Guid UserId { get; }

        public Guid DeviceId { get; }

        public Guid BabyId { get; }

        public string OverlapPolicy { get; private set; } = "ACCEPT_AND_WARN";

        public int ClockSkewToleranceSeconds { get; private set; } = 86_400;

        public DataLimits Limits { get; private set; } = new();

        public int MaxEntities { get; private set; }

        /// <summary>Entidades vivas do bebê; nulo = ainda não contadas (só se conta quando o teto pode ser atingido).</summary>
        public long? LiveEntities { get; set; }

        public Authors? Authors { get; set; }

        public List<AuditNote> Audits { get; } = [];

        public static async Task<Group> OpenAsync(
            TrackingTx tx, Guid userId, Guid deviceId, Guid babyId, List<PushMutation> items, TrackingOptions options)
        {
            var group = new Group(tx, userId, deviceId, babyId);
            var settings = await tx.QueryFirstAsync(
                """
                SELECT nina.param_text('sleep.overlap_policy', 'ACCEPT_AND_WARN'),
                       nina.param_int('limits.sync_max_clock_skew_seconds', 86400),
                       nina.param_int('limits.bottle_volume_ml_max', 5000),
                       nina.param_int('limits.pumping_volume_ml_max', 5000),
                       nina.param_int('limits.sync_max_entities_per_baby', @entities)
                """,
                r => new { Policy = r.GetString(0), Skew = r.GetInt32(1), Bottle = r.GetInt32(2), Pumping = r.GetInt32(3), Entities = r.GetInt32(4) },
                Db.P("entities", options.DefaultMaxEntitiesPerBaby, NpgsqlDbType.Integer));
            if (settings is not null)
            {
                group.OverlapPolicy = settings.Policy;
                group.ClockSkewToleranceSeconds = settings.Skew;
                group.Limits = new DataLimits(settings.Bottle, settings.Pumping);
                group.MaxEntities = settings.Entities;
            }

            // O número de entidades vivas nunca passa de last_sequence (cada criação consumiu uma sequência): só conta de fato
            // quando o bebê está perto do teto.
            var creates = items.Count(i => i.Op == "CREATE");
            if (creates > 0)
            {
                var head = await tx.ScalarAsync<long?>("SELECT last_sequence FROM nina.sync_head(@baby)", Db.Uuid("baby", babyId));
                if (head is { } last && last + creates <= group.MaxEntities)
                {
                    group.LiveEntities = 0;            // limite superior seguro: o teto não pode ser atingido neste grupo
                    group.MaxEntities = int.MaxValue;
                }
            }

            return group;
        }
    }
}
