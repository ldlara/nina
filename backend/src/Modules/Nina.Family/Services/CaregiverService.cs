using System.Security.Claims;
using Nina.Family.Contracts;
using Nina.Family.Persistence;
using Nina.SharedKernel.Data;
using Nina.SharedKernel.Http;
using Nina.SharedKernel.Security;

namespace Nina.Family.Services;

/// <summary>Cuidadores e papéis (RF-006/RF-007): listar, mudar papel, remover/sair e transferir a propriedade.</summary>
public sealed class CaregiverService(FamilyDb db, FamilyRateGate gate, SecretKeys keys, IReauthVerifier reauth)
{
    public async Task<MembershipList> ListAsync(Guid userId, Guid babyId, CancellationToken ct)
    {
        gate.General(userId);
        return await db.RunAsync(userId, async tx =>
        {
            var role = await Guard.RequireAsync(tx, userId, babyId, ownerOnly: false);
            var refs = await FamilyStore.ListMemberRefsAsync(tx, babyId);
            var baby = await FamilyStore.GetBabyAsync(tx, babyId) ?? throw ProblemException.NotFound();
            if (role == Roles.Owner)
            {
                var rows = await FamilyStore.ListMembershipsAsync(tx, babyId);
                return new MembershipList([.. rows.Select(r => ToDto(r, refs, viewerIsOwner: true))]);
            }

            // Caregiver/ReadOnly: a RLS só deixa ver o PRÓPRIO vínculo; os demais membros ativos vêm de baby_member_refs
            // (sem e-mail e sem convites pendentes). Datas dos outros membros não são expostas ao não-Owner: ver o relatório (BE-002).
            var own = (await FamilyStore.ListMembershipsAsync(tx, babyId)).ToDictionary(r => r.Id);
            var items = refs.Select(m => own.TryGetValue(m.MembershipId, out var row)
                ? ToDto(row, refs, viewerIsOwner: false)
                : new MembershipDto(
                    m.MembershipId, babyId, new UserRefDto(m.UserId, m.DisplayName ?? string.Empty), null, m.Role, "ACTIVE",
                    baby.CreatedAt, null, null));
            return new MembershipList([.. items]);
        }, ct);
    }

    public async Task<MembershipDto> ChangeRoleAsync(Guid userId, Guid babyId, Guid membershipId, RoleChangeRequest request, CancellationToken ct)
    {
        gate.General(userId);
        var v = new FamilyValidation();
        var role = v.InvitableRole("role", request.Role);
        v.ThrowIfInvalid();

        return await db.RunAsync(userId, async tx =>
        {
            await Guard.RequireAsync(tx, userId, babyId, ownerOnly: true);
            var target = await FamilyStore.FindMembershipAsync(tx, babyId, membershipId) ?? throw ProblemException.NotFound();
            if (target.Role == Roles.Owner || target.UserId == userId)
            {
                // RF-006-A7: o Owner não rebaixa a si mesmo; só transferindo a propriedade.
                throw ProblemException.Conflict("OWNER_MUST_TRANSFER", "Transfer the ownership first");
            }

            if (target.Status != "ACTIVE")
            {
                throw ProblemException.Conflict("MEMBERSHIP_NOT_ACTIVE", "Only active memberships can change role");
            }

            await FamilyStore.SetMemberRoleAsync(tx, membershipId, role!);
            var updated = await FamilyStore.FindMembershipAsync(tx, babyId, membershipId) ?? throw ProblemException.NotFound();
            return ToDto(updated, await FamilyStore.ListMemberRefsAsync(tx, babyId), viewerIsOwner: true);
        }, ct);
    }

    /// <summary>
    /// Owner remove um cuidador ativo ou cancela um convite pendente; Caregiver/ReadOnly só remove o PRÓPRIO vínculo (sair).
    /// O Owner não sai sem transferir a propriedade (409 <c>OWNER_MUST_TRANSFER</c>).
    /// </summary>
    public async Task RemoveAsync(Guid userId, Guid babyId, Guid membershipId, CancellationToken ct)
    {
        gate.General(userId);
        await db.RunAsync(userId, async tx =>
        {
            var role = await Guard.RequireAsync(tx, userId, babyId, ownerOnly: false);
            var own = await FamilyStore.GetOwnActiveMembershipIdAsync(tx, babyId, userId);
            if (own == membershipId)
            {
                if (role == Roles.Owner)
                {
                    throw ProblemException.Conflict("OWNER_MUST_TRANSFER", "Transfer the ownership first");
                }

                await FamilyStore.LeaveBabyAsync(tx, babyId);
                return;
            }

            if (role != Roles.Owner)
            {
                throw Guard.ForbiddenRole();
            }

            var target = await FamilyStore.FindMembershipAsync(tx, babyId, membershipId) ?? throw ProblemException.NotFound();
            if (target.Status is "PENDING" or "ACTIVE")
            {
                await FamilyStore.RemoveMemberAsync(tx, membershipId);
            }

            // vínculo já encerrado (revogado/recusado): idempotente
        }, ct);
    }

    /// <summary>Transferência de propriedade (reautenticação com escopo <c>OWNERSHIP_TRANSFER</c>); o antigo Owner vira Caregiver.</summary>
    public async Task<BabyDto> TransferOwnershipAsync(
        ClaimsPrincipal user, Guid babyId, OwnershipTransferRequest request, string? reauthToken, CancellationToken ct)
    {
        var userId = user.RequireUserId();
        gate.General(userId);
        gate.Sensitive(userId);
        if (request.NewOwnerMembershipId is not { } targetId || targetId == Guid.Empty)
        {
            throw ProblemException.Validation(new FieldError("new_owner_membership_id", "REQUIRED"));
        }

        await db.RunAsync(userId, async tx =>
        {
            await Guard.RequireAsync(tx, userId, babyId, ownerOnly: true);
            var target = await FamilyStore.FindMembershipAsync(tx, babyId, targetId) ?? throw ProblemException.NotFound();
            if (target.Status != "ACTIVE" || target.Role == Roles.Owner || target.UserId == userId)
            {
                throw ProblemException.Conflict("INVALID_TRANSFER_TARGET", "The new owner must be another active caregiver of the baby");
            }
        }, ct);

        var proof = await reauth.RequireAsync(user, reauthToken, ReauthScopes.OwnershipTransfer, ct);
        var jtiHash = keys.Hmac("reauth-jti", proof.Jti);
        return await db.RunAsync(userId, async tx =>
        {
            await FamilyStore.TransferOwnershipAsync(tx, babyId, targetId, jtiHash);
            return BabyService.ToDto(await FamilyStore.GetBabyAsync(tx, babyId) ?? throw ProblemException.NotFound());
        }, ct);
    }

    internal static MembershipDto ToDto(MembershipRow r, IReadOnlyList<MemberRef> refs, bool viewerIsOwner)
    {
        UserRefDto? user = null;
        if (r.UserId is { } uid)
        {
            var name = refs.FirstOrDefault(m => m.UserId == uid)?.DisplayName;
            user = new UserRefDto(uid, name ?? string.Empty);
        }

        // EXPIRED é derivado de invitation_expires_at (o banco não tem esse estado, SR-021). E-mail do convidado só ao Owner.
        var status = r.Expired ? "EXPIRED" : r.Status;
        return new MembershipDto(
            r.Id, r.BabyId, user, viewerIsOwner ? r.InvitedEmail : null, r.Role, status, r.InvitedAt, r.InviteExpiresAt, r.AcceptedAt);
    }
}
