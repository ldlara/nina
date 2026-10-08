using Nina.SharedKernel.Data;
using Nina.SharedKernel.Http;
using Nina.SharedKernel.Security;
using NpgsqlTypes;

namespace Nina.SharedKernel.Audit;

/// <summary>Evento de auditoria. <c>Metadata</c> não pode conter PII nem conteúdo livre (INV-28, SEC-030).</summary>
public sealed record AuditEntry(
    string Action,
    Guid? ActorUserId,
    string? EntityType = null,
    Guid? EntityId = null,
    Guid? DeviceId = null,
    string Result = "SUCCESS",
    bool IsCritical = false,
    IReadOnlyDictionary<string, object?>? Metadata = null);

/// <summary>Grava em <c>nina.audit_event</c> (append-only; <c>nina_app</c> só tem INSERT). IP é guardado só como HMAC.</summary>
public sealed class AuditLog(IRequestContext request, SecretKeys keys)
{
    public async Task AppendAsync(DbTx tx, AuditEntry entry)
    {
        byte[]? ipHash = request.ClientIp is { } ip ? keys.Hmac("audit-ip", ip.ToString()) : null;
        var metadata = System.Text.Json.JsonSerializer.Serialize(entry.Metadata ?? new Dictionary<string, object?>());
        await tx.ExecAsync(
            """
            INSERT INTO nina.audit_event
              (actor_user_id, actor_type, action, entity_type, entity_id, device_id, request_id, result, ip_hash, metadata_safe, is_critical)
            VALUES (@actor, @actor_type, @action, @entity_type, @entity_id, @device, @request, @result, @ip, @meta, @critical)
            """,
            Db.Uuid("actor", entry.ActorUserId),
            Db.Text("actor_type", entry.ActorUserId is null ? "SYSTEM" : "USER"),
            Db.Text("action", entry.Action),
            Db.Text("entity_type", entry.EntityType),
            Db.Uuid("entity_id", entry.EntityId),
            Db.Uuid("device", entry.DeviceId),
            Db.Text("request", request.RequestId),
            Db.Text("result", entry.Result),
            Db.Bytes("ip", ipHash),
            Db.P("meta", metadata, NpgsqlDbType.Jsonb),
            Db.P("critical", entry.IsCritical));
    }
}
