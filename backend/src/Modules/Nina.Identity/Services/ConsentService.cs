using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Nina.Identity.Contracts;
using Nina.Identity.Persistence;
using Nina.SharedKernel.Audit;
using Nina.SharedKernel.Data;
using Nina.SharedKernel.Http;

namespace Nina.Identity.Services;

public sealed record AcceptedConsent(string PurposeKey, string Version);

/// <summary>Consentimentos append-only (RF-003, INV-27). Chaves de finalidade: MAIÚSCULAS na API, minúsculas no banco.</summary>
public sealed class ConsentService(NinaDb db, IOptions<IdentityOptions> options, AuditLog audit, TimeProvider time, IRequestContext request)
{
    private static readonly HashSet<string> Sources = ["ONBOARDING", "SETTINGS", "PROMPT"];

    public async Task<LegalDocumentList> ListLegalDocumentsAsync(CancellationToken ct)
    {
        var purposes = await db.InTransactionAsync(null, tx => IdentityStore.ListPurposesAsync(tx), ct);
        return new LegalDocumentList([.. purposes.Where(p => p.InMvp).Select(ToLegal)]);
    }

    /// <summary>Valida o aceite do cadastro (Termos e Política obrigatórios, versões vigentes, finalidades de escopo USER).</summary>
    public static async Task<(List<AcceptedConsent> Accepted, List<FieldError> Errors, List<ConsentAcceptanceDto> MissingRequired)>
        ResolveOnboardingAsync(DbTx tx, List<ConsentAcceptance>? input, string field)
    {
        var purposes = await IdentityStore.ListPurposesAsync(tx);
        var errors = new List<FieldError>();
        var accepted = new Dictionary<string, AcceptedConsent>(StringComparer.Ordinal);
        var items = input ?? [];
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var key = item.PurposeKey?.ToLowerInvariant();
            var purpose = purposes.FirstOrDefault(p => p.PurposeKey == key);
            if (purpose is null || purpose.Scope != "USER")
            {
                errors.Add(new FieldError($"{field}[{i}].purpose_key", "UNSUPPORTED_VALUE"));
            }
            else if (item.DocumentVersion != purpose.CurrentVersion)
            {
                errors.Add(new FieldError($"{field}[{i}].document_version", "STALE_VERSION"));
            }
            else
            {
                accepted[purpose.PurposeKey] = new AcceptedConsent(purpose.PurposeKey, purpose.CurrentVersion);
            }
        }

        var missing = purposes
            .Where(p => p is { Required: true, Scope: "USER" } && !accepted.ContainsKey(p.PurposeKey))
            .Select(p => new ConsentAcceptanceDto(p.PurposeKey.ToUpperInvariant(), p.CurrentVersion))
            .ToList();
        return ([.. accepted.Values], errors, missing);
    }

    public async Task RecordOnboardingAsync(DbTx tx, Guid userId, IEnumerable<AcceptedConsent> accepted, string locale, DeviceInfo? device)
    {
        var now = time.GetUtcNow();
        foreach (var consent in accepted)
        {
            await IdentityStore.InsertConsentAsync(
                tx, userId, null, consent.PurposeKey, consent.Version, TextHash(consent.PurposeKey, consent.Version), locale,
                "GRANTED", "ONBOARDING", device?.AppVersion, device?.Platform ?? "SERVER", now);
            await audit.AppendAsync(tx, new AuditEntry(
                "consent.granted", userId, "consent", null, device?.DeviceId,
                Metadata: new Dictionary<string, object?> { ["purpose_key"] = consent.PurposeKey, ["version"] = consent.Version }));
        }
    }

    /// <summary>Documentos obrigatórios (escopo USER) cuja versão vigente o usuário ainda não aceitou (RF-003-A2).</summary>
    public static async Task<List<ConsentAcceptanceDto>> PendingAsync(DbTx tx, Guid userId)
    {
        var purposes = await IdentityStore.ListPurposesAsync(tx);
        var consents = await IdentityStore.ListConsentsAsync(tx, userId);
        return [.. PendingRequired(purposes, consents).Select(p => new ConsentAcceptanceDto(p.PurposeKey.ToUpperInvariant(), p.CurrentVersion))];
    }

    public async Task<ConsentsResponse> ListAsync(Guid userId, bool includeHistory, CancellationToken ct) =>
        await db.InTransactionAsync(userId, async tx =>
        {
            var purposes = await IdentityStore.ListPurposesAsync(tx);
            var rows = await IdentityStore.ListConsentsAsync(tx, userId); // seq DESC
            var latest = new HashSet<(string, Guid?)>();
            var current = new List<ConsentRecordDto>();
            var history = new List<ConsentRecordDto>();
            foreach (var row in rows)
            {
                var isLatest = latest.Add((row.PurposeKey, row.BabyId));
                if (isLatest)
                {
                    current.Add(ToRecord(row, row.Status));
                }

                if (includeHistory)
                {
                    history.Add(ToRecord(row, !isLatest && row.Status == "GRANTED" ? "SUPERSEDED" : row.Status));
                }
            }

            var pending = PendingRequired(purposes, rows).Select(ToLegal).ToList();
            return new ConsentsResponse(current, pending, includeHistory ? history : null);
        }, ct);

    public async Task<ConsentRecordDto> RecordAsync(Guid userId, ConsentInputRequest input, CancellationToken ct)
    {
        var v = new Validation();
        var key = input.PurposeKey?.ToLowerInvariant();
        v.Required("purpose_key", input.PurposeKey, 64);
        v.Required("document_version", input.DocumentVersion, 32);
        if (input.DocumentVersion is { Length: > 0 and <= 32 } dv && !dv.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-'))
        {
            v.Add(new FieldError("document_version", "INVALID_FORMAT"));
        }

        if (input.Status is not ("GRANTED" or "REVOKED"))
        {
            v.Add(new FieldError("status", input.Status is null ? "REQUIRED" : "UNSUPPORTED_VALUE"));
        }

        if (input.Source is null || !Sources.Contains(input.Source))
        {
            v.Add(new FieldError("source", input.Source is null ? "REQUIRED" : "UNSUPPORTED_VALUE"));
        }

        var locale = v.Locale("locale", input.Locale);
        v.ThrowIfInvalid();

        var now = time.GetUtcNow();
        return await db.InTransactionAsync(userId, async tx =>
        {
            var session = request.SessionId is { } sid ? await IdentityStore.GetSessionAsync(tx, sid) : null;
            var purposes = await IdentityStore.ListPurposesAsync(tx);
            var purpose = purposes.FirstOrDefault(p => p.PurposeKey == key)
                          ?? throw ProblemException.Validation(new FieldError("purpose_key", "UNSUPPORTED_VALUE"));
            if (purpose.Scope == "BABY" != input.BabyId.HasValue)
            {
                throw ProblemException.Validation(new FieldError("baby_id", purpose.Scope == "BABY" ? "REQUIRED" : "NOT_APPLICABLE"));
            }

            if (input.Status == "GRANTED" && input.DocumentVersion != purpose.CurrentVersion)
            {
                throw ProblemException.Validation(new FieldError("document_version", "STALE_VERSION"));
            }

            if (input.Status == "REVOKED" && purpose.Required)
            {
                // Termos/Política não se revogam por aqui; a saída é a exclusão de conta (RF-045).
                throw ProblemException.Validation(new FieldError("status", "REVOCATION_NOT_ALLOWED"));
            }

            if (input.BabyId is { } babyId && !await IdentityStore.CanReadBabyAsync(tx, babyId))
            {
                throw ProblemException.NotFound();
            }

            var effectiveLocale = locale ?? options.Value.DefaultLocale;
            await IdentityStore.InsertConsentAsync(
                tx, userId, input.BabyId, purpose.PurposeKey, input.DocumentVersion!, TextHash(purpose.PurposeKey, input.DocumentVersion!),
                effectiveLocale, input.Status!, input.Source!, session?.AppVersion, session?.Platform ?? "SERVER", now);

            var all = await IdentityStore.ListConsentsAsync(tx, userId);
            var created = all.First(c => c.PurposeKey == purpose.PurposeKey && c.BabyId == input.BabyId);
            await audit.AppendAsync(tx, new AuditEntry(
                input.Status == "GRANTED" ? "consent.granted" : "consent.revoked", userId, "consent", created.Id, session?.DeviceId,
                Metadata: new Dictionary<string, object?> { ["purpose_key"] = purpose.PurposeKey, ["version"] = input.DocumentVersion }));
            return ToRecord(created, created.Status);
        }, ct);
    }

    private static IEnumerable<PurposeRow> PendingRequired(List<PurposeRow> purposes, List<ConsentRow> consentsDesc) =>
        purposes.Where(p =>
            p is { Required: true, Scope: "USER" }
            && !(consentsDesc.FirstOrDefault(c => c.PurposeKey == p.PurposeKey && c.BabyId is null) is { Status: "GRANTED" } last
                 && last.PolicyVersion == p.CurrentVersion));

    private LegalDocumentDto ToLegal(PurposeRow p) => new(
        p.PurposeKey.ToUpperInvariant(),
        p.CurrentVersion,
        $"{options.Value.LegalBaseUrl.TrimEnd('/')}/{p.PurposeKey.Replace('_', '-')}/{p.CurrentVersion}",
        TextHash(p.PurposeKey, p.CurrentVersion),
        options.Value.LegalEffectiveAt,
        p.Required);

    private static ConsentRecordDto ToRecord(ConsentRow row, string status) => new(
        row.Id,
        row.PurposeKey.ToUpperInvariant(),
        row.PolicyVersion,
        status,
        row.RecordedAt,
        row.Status == "REVOKED" ? row.RecordedAt : null,
        row.BabyId,
        row.Source);

    // Sem repositório de documentos legais, o hash identifica (finalidade, versão); trocar pelo hash do texto publicado.
    internal static string TextHash(string purposeKey, string version) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{purposeKey}|{version}"))).ToLowerInvariant();
}
