using System.Text.Json;
using System.Text.Json.Nodes;
using Nina.SharedKernel.Http;
using Nina.Tracking.Domain;

namespace Nina.Tracking.Sync;

internal sealed record PushMutation(
    int Index,
    Guid MutationId,
    string Op,
    string EntityType,
    Guid EntityId,
    Guid BabyId,
    long BaseVersion,
    DateTimeOffset ClientCreatedAt,
    JsonElement? Data);

/// <summary>Pedido já validado no envelope. Mantém o <see cref="JsonDocument"/> vivo: <c>Data</c> aponta para ele.</summary>
internal sealed class ParsedPush(JsonDocument document, Guid deviceId, IReadOnlyList<PushMutation> mutations) : IDisposable
{
    public Guid DeviceId { get; } = deviceId;

    public IReadOnlyList<PushMutation> Mutations { get; } = mutations;

    public void Dispose() => document.Dispose();
}

/// <summary>Rejeição por mutação (<c>status=REJECTED</c>). <c>Persist</c>: determinística, gravada em <c>sync_mutation</c> para o reenvio repetir a resposta.</summary>
internal sealed record Rejection(string Code, IReadOnlyList<FieldError>? Errors = null, bool Retryable = false, bool Persist = true);

/// <summary>Desvia o fluxo da mutação para uma rejeição (o savepoint desfaz qualquer escrita parcial).</summary>
internal sealed class RejectException(Rejection rejection) : Exception(rejection.Code)
{
    public Rejection Rejection { get; } = rejection;

    public static RejectException Of(string code, params FieldError[] errors) => new(new Rejection(code, errors.Length == 0 ? null : errors));

    public static RejectException Validation(IEnumerable<FieldError> errors) => new(new Rejection("VALIDATION_FAILED", [.. errors]));
}

/// <summary>A linha mudou entre a leitura e a escrita por fora do lock do bebê: refaz o grupo inteiro (idempotente).</summary>
internal sealed class GroupRetryException() : Exception("concurrent modification");

internal sealed record PriorMutation(
    Guid BabyId, string EntityType, Guid EntityId, string Op, string Outcome, long? ResultVersion, string? RejectCode, string? Resolution, DateTimeOffset ReceivedAt);

internal sealed record AuditNote(string EntityType, Guid EntityId, string Resolution, IReadOnlyList<string> Fields, long? BaseVersion, long Version);

internal sealed record FieldConflictInfo(string Field, string Kept);

internal sealed record AppliedMutation(
    long Version,
    string Resolution,
    string Outcome,
    IReadOnlyList<FieldConflictInfo> Conflicts,
    IReadOnlyList<JsonObject> Warnings,
    JsonObject? EntityRow,
    bool EntityDeleted);

internal static class ProblemCatalog
{
    private static readonly Dictionary<string, (int Status, string Title)> Known = new(StringComparer.Ordinal)
    {
        ["VALIDATION_FAILED"] = (400, "Validation failed"),
        ["SLEEP_OVERLAP"] = (409, "Sleep session overlaps another"),
        ["ENTITY_DELETED"] = (409, "Entity already deleted"),
        ["ENTITY_NOT_FOUND"] = (404, "Entity not found"),
        ["ENTITY_ID_UNAVAILABLE"] = (409, "Entity id unavailable"),
        ["ENTITY_QUOTA_EXCEEDED"] = (429, "Entity quota exceeded"),
        ["VERSION_AHEAD"] = (409, "Base version ahead of the server"),
        ["FORBIDDEN_ROLE"] = (403, "Role not allowed"),
        ["ACCESS_REVOKED"] = (403, "Access revoked"),
        ["BABY_NOT_FOUND"] = (404, "Baby not found"),
        ["TRANSIENT"] = (503, "Temporary failure, retry later"),
    };

    public static JsonObject Problem(string code, IReadOnlyList<FieldError>? errors)
    {
        var (status, title) = Known.TryGetValue(code, out var known) ? known : (409, "Rejected");
        var problem = new JsonObject
        {
            ["type"] = ProblemWriter.TypeFor(code),
            ["title"] = title,
            ["status"] = status,
            ["code"] = code,
        };
        if (errors is { Count: > 0 })
        {
            var array = new JsonArray();
            foreach (var e in errors)
            {
                array.Add(new JsonObject { ["field"] = e.Field, ["code"] = e.Code });
            }

            problem["errors"] = array;
        }

        return problem;
    }

    public static bool IsDeterministic(string code) =>
        code is "VALIDATION_FAILED" or "SLEEP_OVERLAP" or "ENTITY_DELETED" or "ENTITY_NOT_FOUND" or "ENTITY_ID_UNAVAILABLE" or "VERSION_AHEAD";
}
