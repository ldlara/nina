using Nina.SharedKernel.Data;
using Nina.SharedKernel.Http;
using Nina.Tracking.Reads;

namespace Nina.Tracking.Persistence;

internal enum AccessState
{
    /// <summary>Sem vínculo (ou bebê inexistente/excluído): indistinguível, <c>404</c> uniforme (AD-07, RF-004-A6).</summary>
    None,
    Active,
    Revoked,
}

/// <summary>Vínculo do usuário do contexto com um bebê, lido sob RLS (<c>caregiver_membership</c>); o papel nunca vem do token.</summary>
internal sealed record BabyAccess(AccessState State, string? Role, Guid? MembershipId)
{
    public bool CanRead => State == AccessState.Active;

    public bool CanWrite => State == AccessState.Active && Role is "OWNER" or "CAREGIVER";

    public static async Task<BabyAccess> ResolveAsync(TrackingTx tx, Guid babyId)
    {
        var found = await tx.QueryFirstAsync(
            """
            SELECT m.id, m.role, m.status FROM nina.caregiver_membership m
             WHERE m.baby_id = @baby AND m.user_id = nina.current_user_id() AND m.status IN ('ACTIVE', 'REVOKED')
             ORDER BY (m.status = 'ACTIVE') DESC, m.revoked_at DESC NULLS LAST, m.accepted_at DESC NULLS LAST
             LIMIT 1
            """,
            r => new BabyAccess(r.GetString(2) == "ACTIVE" ? AccessState.Active : AccessState.Revoked, r.GetString(1), r.GetGuid(0)),
            Db.Uuid("baby", babyId));
        return found ?? new BabyAccess(AccessState.None, null, null);
    }

    /// <summary>Leitura: sem vínculo <c>404 NOT_FOUND</c>; vínculo revogado <c>403 ACCESS_REVOKED</c> (o app apaga os dados locais).</summary>
    public void RequireRead()
    {
        switch (State)
        {
            case AccessState.None:
                throw ProblemException.NotFound();
            case AccessState.Revoked:
                throw ProblemException.Forbidden("ACCESS_REVOKED", "Access revoked");
            default:
                break;
        }
    }

    public static async Task<Authors> LoadAuthorsAsync(TrackingTx tx, Guid babyId)
    {
        var names = await tx.QueryAsync(
            "SELECT user_id, display_name FROM nina.baby_member_refs(@baby)",
            r => (Id: r.GetGuid(0), Name: r.GetString(1)),
            Db.Uuid("baby", babyId));
        return new Authors(names.ToDictionary(x => x.Id, x => x.Name));
    }
}
