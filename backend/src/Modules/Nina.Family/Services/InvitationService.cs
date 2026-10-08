using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nina.Family.Contracts;
using Nina.Family.Mail;
using Nina.Family.Persistence;
using Nina.SharedKernel.Http;
using Nina.SharedKernel.Security;

namespace Nina.Family.Services;

/// <summary>Resultado de criar/reenviar: o vínculo e o que o e-mail precisa (o token nunca volta na resposta HTTP).</summary>
public sealed record InvitationIssued(MembershipDto Membership, string Token, string Email, string InviterName, string Locale);

/// <summary>Convites (RF-006): criar, reenviar, ver prévia, aceitar e recusar. O token tem 256 bits e só o HMAC dele é guardado (SEC-004).</summary>
public sealed partial class InvitationService(
    FamilyDb db,
    IOptions<FamilyOptions> options,
    FamilyRateGate gate,
    SecretKeys keys,
    IFamilyMailer mailer,
    IBackgroundWork background,
    ILogger<InvitationService> logger)
{
    private static readonly string[] CaregiverVisible = ["timeline", "charts", "predictions", "record_events"];
    private static readonly string[] ReadOnlyVisible = ["timeline", "charts", "predictions"];

    public async Task<MembershipDto> CreateAsync(Guid userId, Guid babyId, InvitationCreateRequest request, CancellationToken ct)
    {
        gate.General(userId);
        gate.Invite(userId);
        var v = new FamilyValidation();
        var email = v.Email("email", request.Email);
        var role = v.InvitableRole("role", request.Role);
        v.ThrowIfInvalid();

        var issued = await db.RunAsync(userId, async tx =>
        {
            await Guard.RequireAsync(tx, userId, babyId, ownerOnly: true);
            var self = await FamilyStore.GetSelfAsync(tx, userId) ?? throw ProblemException.Unauthorized("INVALID_TOKEN", "Invalid token");
            // RF-006-A8: quem já está vinculado (inclusive o próprio Owner) ou já tem convite vigente não ganha segundo vínculo.
            if (self.EmailNormalized == email)
            {
                throw AlreadyMember();
            }

            if (await FamilyStore.LookupUserIdByEmailAsync(tx, email!) is { } existing && await FamilyStore.HasLiveLinkAsync(tx, babyId, existing))
            {
                throw AlreadyMember();
            }

            return await IssueAsync(tx, userId, babyId, email!, role!, self);
        }, ct);

        Dispatch(issued);
        return issued.Membership;
    }

    /// <summary>
    /// Reenvio: novo token (o anterior deixa de valer) e nova expiração. O banco não tem função de reenvio nem UPDATE de vínculo para o app,
    /// então o convite pendente é revogado e um novo é criado em seguida: <b>o id do vínculo muda</b> (a resposta traz o novo).
    /// </summary>
    public async Task<MembershipDto> ResendAsync(Guid userId, Guid babyId, Guid membershipId, CancellationToken ct)
    {
        gate.General(userId);
        gate.Resend(userId);
        var issued = await db.RunAsync(userId, async tx =>
        {
            await Guard.RequireAsync(tx, userId, babyId, ownerOnly: true);
            var target = await FamilyStore.FindMembershipAsync(tx, babyId, membershipId) ?? throw ProblemException.NotFound();
            if (target is not { Status: "PENDING", InvitedEmail: { } email })
            {
                throw ProblemException.Conflict("INVITATION_NOT_PENDING", "Only pending invitations can be resent");
            }

            var self = await FamilyStore.GetSelfAsync(tx, userId) ?? throw ProblemException.Unauthorized("INVALID_TOKEN", "Invalid token");
            await FamilyStore.RemoveMemberAsync(tx, membershipId);
            return await IssueAsync(tx, userId, babyId, email, target.Role, self);
        }, ct);

        Dispatch(issued);
        return issued.Membership;
    }

    public async Task<InvitationPreviewDto> InspectAsync(Guid userId, InvitationTokenRequest request, CancellationToken ct)
    {
        gate.General(userId);
        gate.InvitationToken(userId);
        var v = new FamilyValidation();
        var token = v.Token("token", request.Token);
        v.ThrowIfInvalid();

        var hash = HashToken(token!);
        var row = await db.RunAsync(userId, tx => FamilyStore.InspectInvitationAsync(tx, hash), ct) ?? throw ProblemException.NotFound();
        return new InvitationPreviewDto(
            row.InviterDisplayName ?? string.Empty,
            row.BabyInitial ?? string.Empty,
            row.Role,
            row.ExpiresAt,
            row.Role == Roles.ReadOnly ? ReadOnlyVisible : CaregiverVisible);
    }

    public async Task<BabyDto> AcceptAsync(ClaimsPrincipal user, InvitationTokenRequest request, CancellationToken ct)
    {
        var userId = user.RequireUserId();
        gate.General(userId);
        gate.InvitationToken(userId);
        var v = new FamilyValidation();
        var token = v.Token("token", request.Token);
        v.ThrowIfInvalid();

        var hash = HashToken(token!);
        var sessionId = user.GetSessionId();
        // O resultado (inclusive falhas) é devolvido pelo banco como código, para que o contador de tentativas inválidas persista;
        // só depois de confirmar a transação a falha vira erro HTTP.
        var outcome = await db.RunAsync<(BabyDto? Baby, ProblemException? Problem)>(userId, async tx =>
        {
            var pending = await FamilyStore.ListPendingRequiredUserConsentsAsync(tx, userId);
            if (pending.Count > 0)
            {
                return (null, ConsentRequired(pending));
            }

            var self = await FamilyStore.GetSelfAsync(tx, userId) ?? throw ProblemException.Unauthorized("INVALID_TOKEN", "Invalid token");
            var version = await FamilyStore.GetPurposeVersionAsync(tx, "caregiver_data_ack") ?? "1.0.0";
            var (platform, appVersion) = await FamilyStore.GetSessionClientAsync(tx, sessionId);
            var result = await FamilyStore.AcceptInvitationAsync(
                tx, hash, version, TextHash("caregiver_data_ack", version), self.Locale ?? options.Value.DefaultLocale, platform ?? "SERVER", appVersion);
            switch (result?.Result)
            {
                case "ACCEPTED" when result.BabyId is { } babyId:
                    var baby = await FamilyStore.GetBabyAsync(tx, babyId) ?? throw ProblemException.NotFound();
                    return (BabyService.ToDto(baby), null);
                case "ALREADY_MEMBER":
                    return (null, AlreadyMember());
                case "EMAIL_NOT_VERIFIED":
                    return (null, ProblemException.Forbidden("EMAIL_NOT_VERIFIED", "Email not verified"));
                case "CONSENT_REQUIRED":
                    return (null, ConsentRequired([new RequiredConsent("caregiver_data_ack", version)]));
                default: // NOT_FOUND, EXPIRED ou destinatário diferente: resposta uniforme (SEC-051)
                    return (null, ProblemException.NotFound());
            }
        }, ct);

        return outcome.Baby ?? throw outcome.Problem!;
    }

    public async Task DeclineAsync(Guid userId, InvitationTokenRequest request, CancellationToken ct)
    {
        gate.General(userId);
        gate.InvitationToken(userId);
        var v = new FamilyValidation();
        var token = v.Token("token", request.Token);
        v.ThrowIfInvalid();

        var hash = HashToken(token!);
        var result = await db.RunAsync(userId, tx => FamilyStore.DeclineInvitationAsync(tx, hash), ct);
        if (result != "DECLINED")
        {
            throw ProblemException.NotFound();
        }
    }

    internal byte[] HashToken(string token) => keys.Hmac("family-invite", token);

    private async Task<InvitationIssued> IssueAsync(
        Nina.SharedKernel.Data.DbTx tx, Guid userId, Guid babyId, string email, string role, SelfProfile self)
    {
        // Convite pendente para o mesmo e-mail: vigente => já convidado; vencido => é revogado e um novo é emitido.
        if (await FamilyStore.HasPendingInviteForEmailAsync(tx, babyId, email))
        {
            var pending = (await FamilyStore.ListMembershipsAsync(tx, babyId)).FirstOrDefault(m => m.Status == "PENDING" && m.InvitedEmail == email);
            if (pending is null || !pending.Expired)
            {
                throw AlreadyMember();
            }

            await FamilyStore.RemoveMemberAsync(tx, pending.Id);
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var id = await FamilyStore.InsertInvitationAsync(tx, babyId, userId, email, role, HashToken(token), options.Value.InvitationTtlDays);
        var row = await FamilyStore.FindMembershipAsync(tx, babyId, id) ?? throw ProblemException.NotFound();
        return new InvitationIssued(
            CaregiverService.ToDto(row, [], viewerIsOwner: true), token, email, self.DisplayName ?? string.Empty, self.Locale ?? options.Value.DefaultLocale);
    }

    private void Dispatch(InvitationIssued issued) =>
        background.Enqueue(async ct =>
        {
            try
            {
                await mailer.SendInvitationAsync(issued.Email, issued.Token, issued.InviterName, issued.Membership.Role, issued.Locale, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogMailFailed(logger, ex.GetType().Name);
            }
        });

    private static ProblemException AlreadyMember() => ProblemException.Conflict("ALREADY_MEMBER", "Already a member");

    private static ProblemException ConsentRequired(IEnumerable<RequiredConsent> pending) =>
        new(StatusCodes.Status403Forbidden, "CONSENT_REQUIRED", "Consent required")
        {
            Extensions = new Dictionary<string, object?>
            {
                ["required_consents"] = pending
                    .Select(p => new Dictionary<string, object?> { ["purpose_key"] = p.PurposeKey.ToUpperInvariant(), ["document_version"] = p.Version })
                    .ToList(),
            },
        };

    // Hash do (finalidade, versão), igual ao do módulo Identity enquanto não houver repositório de textos legais.
    private static string TextHash(string purposeKey, string version) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{purposeKey}|{version}"))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao enviar e-mail de convite ({ExceptionType})")]
    private static partial void LogMailFailed(ILogger logger, string exceptionType);
}
