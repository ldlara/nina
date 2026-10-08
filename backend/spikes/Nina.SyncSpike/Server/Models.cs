using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nina.SyncSpike.Server;

/// <summary>Contexto autenticado da requisição (a camada Identity real valida token/sessão antes).</summary>
public sealed record AuthContext(Guid UserId, Guid DeviceId);

public sealed record Mutation(
    Guid MutationId, string Op, string EntityType, Guid EntityId, Guid BabyId,
    long BaseVersion, DateTimeOffset ClientCreatedAt, JsonElement? Data);

public sealed record PushRequest(Guid DeviceId, IReadOnlyList<Mutation> Mutations);

public sealed record FieldError(string Field, string Code);

public sealed record Problem(string Code, int Status, string Title, IReadOnlyList<FieldError>? Errors = null,
    string? Reason = null, bool? ResyncRequired = null);

public sealed record FieldConflict(string Field, string Kept);

public sealed record PushWarning(string Code, IReadOnlyList<Guid>? RelatedEntityIds = null, int? SkewSeconds = null);

public sealed record MutationResult(
    Guid MutationId, string Status, string? EntityType = null, Guid? EntityId = null, long? Version = null,
    string? Resolution = null, IReadOnlyList<FieldConflict>? Conflicts = null,
    IReadOnlyList<PushWarning>? Warnings = null, JsonObject? Entity = null,
    bool? Retryable = null, Problem? Problem = null);

public sealed record PushResponse(DateTimeOffset ServerTime, IReadOnlyList<MutationResult> Results);

public sealed record PullChange(string Op, string EntityType, Guid EntityId, long Version,
    JsonObject? Entity = null, DateTimeOffset? DeletedAt = null);

public sealed record PullResponse(string Mode, Guid BabyId, IReadOnlyList<PullChange> Changes, bool HasMore,
    string NextCursor, DateTimeOffset ServerTime);

/// <summary>Falha de envelope/cota/cursor/autorização (equivale a um Problem HTTP).</summary>
public sealed class SyncProblemException(Problem problem) : Exception($"{problem.Status} {problem.Code}")
{
    public Problem Problem { get; } = problem;
    public static SyncProblemException CursorExpired(string reason) =>
        new(new Problem("SYNC_CURSOR_EXPIRED", 410, "Sync cursor expired", Reason: reason, ResyncRequired: true));
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static JsonElement ToElement(object value) => JsonSerializer.SerializeToElement(value, Options);
}

public interface IClock { DateTimeOffset UtcNow { get; } }

public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

/// <summary>Relógio ajustável (testes de expiração de cursor sem esperar 90 dias).</summary>
public sealed class ManualClock : IClock
{
    private long _offsetTicks;
    public TimeSpan Offset { get => new(Interlocked.Read(ref _offsetTicks)); set => Interlocked.Exchange(ref _offsetTicks, value.Ticks); }
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow + Offset;
}

public enum PushMode
{
    /// <summary>Uma transação por bebê (grupo), um SAVEPOINT por mutação. Padrão recomendado.</summary>
    PerBabyTransaction,
    /// <summary>Uma transação por mutação (referência para comparação de latência/contenção).</summary>
    PerMutationTransaction,
}

public sealed class SyncOptions
{
    public int MaxBatch { get; init; } = 100;
    public int MaxPayloadBytes { get; init; } = 256 * 1024;
    public int DefaultPullLimit { get; init; } = 200;
    public int MaxPullLimit { get; init; } = 500;
    public int RetentionDays { get; init; } = 90;
    public int ClockSkewWarnSeconds { get; init; } = 300;
    public int MaxTxRetries { get; init; } = 5;
    public PushMode Mode { get; init; } = PushMode.PerBabyTransaction;
}
