using Nina.SharedKernel.Data;
using Nina.SharedKernel.Http;
using Nina.SharedKernel.Security;
using NpgsqlTypes;

namespace Nina.SharedKernel.Audit;

/// <summary>
/// Evento de auditoria. <c>Metadata</c> não pode conter PII nem conteúdo livre (INV-28, SEC-030). O banco (SR-006) força o ator
/// (<c>nina.user_id</c> da transação), valida a ação/chaves contra o catálogo <c>nina.audit_action</c> e define a severidade
/// (<c>IsCritical</c> é só informativo no código). <c>PreAuth</c>: tentativa sem sessão (login/verificação falhos), registrada
/// por <c>audit_auth_attempt</c> com o usuário-alvo como ator.
/// </summary>
public sealed record AuditEntry(
    string Action,
    Guid? ActorUserId,
    string? EntityType = null,
    Guid? EntityId = null,
    Guid? DeviceId = null,
    string Result = "SUCCESS",
    bool IsCritical = false,
    IReadOnlyDictionary<string, object?>? Metadata = null,
    bool PreAuth = false);

/// <summary>Grava a auditoria pelas funções <c>nina.audit</c>/<c>nina.audit_auth_attempt</c> (<c>nina_app</c> não tem INSERT na tabela). IP é guardado só como HMAC.</summary>
public sealed class AuditLog(IRequestContext request, SecretKeys keys)
{
    public async Task AppendAsync(DbTx tx, AuditEntry entry)
    {
        byte[]? ipHash = request.ClientIp is { } ip ? keys.Hmac("audit-ip", ip.ToString()) : null;
        var metadata = System.Text.Json.JsonSerializer.Serialize(entry.Metadata ?? new Dictionary<string, object?>());
        if (entry.PreAuth)
        {
            await tx.ExecAsync(
                "SELECT nina.audit_auth_attempt(@action, @target, @device, @request, @ip, @meta)",
                Db.Text("action", entry.Action),
                Db.Uuid("target", entry.ActorUserId),
                Db.Uuid("device", entry.DeviceId),
                Db.Text("request", request.RequestId),
                Db.Bytes("ip", ipHash),
                Db.P("meta", metadata, NpgsqlDbType.Jsonb));
            return;
        }

        await tx.ExecAsync(
            "SELECT nina.audit(@action, @entity_type, @entity_id, NULL, @device, @request, @result, @ip, @meta)",
            Db.Text("action", entry.Action),
            Db.Text("entity_type", entry.EntityType),
            Db.Uuid("entity_id", entry.EntityId),
            Db.Uuid("device", entry.DeviceId),
            Db.Text("request", request.RequestId),
            Db.Text("result", entry.Result),
            Db.Bytes("ip", ipHash),
            Db.P("meta", metadata, NpgsqlDbType.Jsonb));
    }
}
