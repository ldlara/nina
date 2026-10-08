using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Nina.Family.Contracts;
using Nina.Family.Persistence;
using Nina.SharedKernel.Data;
using Nina.SharedKernel.Http;
using Nina.SharedKernel.Security;
using NpgsqlTypes;

namespace Nina.Family.Services;

/// <summary>Perfis de bebê (RF-004/RF-005). Idade sempre derivada na data local do bebê; só o Owner edita (ADR-0009, decisão 6).</summary>
public sealed class BabyService(
    FamilyDb db, IOptions<FamilyOptions> options, FamilyRateGate gate, SecretKeys keys, IReauthVerifier reauth)
{
    private static readonly string[] PatchFields = ["display_name", "birth_date", "due_date", "sex", "timezone"];

    public async Task<BabyList> ListAsync(Guid userId, CancellationToken ct)
    {
        gate.General(userId);
        var rows = await db.RunAsync(userId, tx => FamilyStore.ListBabiesAsync(tx), ct);
        return new BabyList([.. rows.Select(ToDto)]);
    }

    public async Task<BabyDto> GetAsync(Guid userId, Guid babyId, CancellationToken ct)
    {
        gate.General(userId);
        return await db.RunAsync(userId, async tx =>
        {
            await Guard.RequireAsync(tx, userId, babyId, ownerOnly: false);
            return ToDto(await FamilyStore.GetBabyAsync(tx, babyId) ?? throw ProblemException.NotFound());
        }, ct);
    }

    public async Task<BabyDto> CreateAsync(Guid userId, Guid? deviceId, BabyCreateRequest request, CancellationToken ct)
    {
        gate.General(userId);
        gate.BabyCreate(userId);
        var v = new FamilyValidation();
        var name = v.DisplayName("display_name", request.DisplayName);
        if (request.BirthDate is null)
        {
            v.Add(new FieldError("birth_date", "REQUIRED"));
        }

        if (request.Id == Guid.Empty)
        {
            v.Add(new FieldError("id", "INVALID_FORMAT"));
        }

        var sex = v.Sex("sex", request.Sex);
        var timezone = v.Timezone("timezone", request.Timezone);
        v.ThrowIfInvalid();

        var babyId = request.Id ?? Guid.NewGuid();
        return await db.RunAsync(userId, async tx =>
        {
            var self = await FamilyStore.GetSelfAsync(tx, userId) ?? throw ProblemException.Unauthorized("INVALID_TOKEN", "Invalid token");
            var zone = timezone ?? self.Timezone ?? "UTC";
            if (!await FamilyStore.IsKnownTimezoneAsync(tx, zone))
            {
                throw ProblemException.Validation(new FieldError("timezone", "INVALID_VALUE"));
            }

            if (request.BirthDate!.Value > await FamilyStore.LocalDateAsync(tx, zone))
            {
                throw ProblemException.Validation(new FieldError("birth_date", "IN_FUTURE"));
            }

            // Reenvio do mesmo bebê (id gerado no cliente) por quem já é Owner dele: devolve o existente (idempotência natural).
            if (request.Id is { } clientId && await FamilyStore.GetBabyAsync(tx, clientId) is { MyRole: Roles.Owner } existing)
            {
                var same = existing.DisplayName == name && existing.BirthDate == request.BirthDate && existing.DueDate == request.DueDate
                           && existing.Timezone == zone && existing.Sex == sex;
                return same ? ToDto(existing) : throw ProblemException.Validation(new FieldError("id", "ENTITY_ID_UNAVAILABLE"));
            }

            // Declaração de responsável legal (art. 14 LGPD): precisa estar consentida antes (contrato: 403 CONSENT_REQUIRED).
            if (options.Value.RequireGuardianConsent && !await FamilyStore.HasGuardianConsentAsync(tx, userId, babyId))
            {
                var current = await FamilyStore.GetPurposeVersionAsync(tx, "child_data_guardian") ?? "1.0.0";
                throw new ProblemException(StatusCodes.Status403Forbidden, "CONSENT_REQUIRED", "Consent required")
                {
                    Extensions = new Dictionary<string, object?>
                    {
                        ["required_consents"] = new[] { new Dictionary<string, object?> { ["purpose_key"] = "CHILD_DATA_GUARDIAN", ["document_version"] = current } },
                    },
                };
            }

            await FamilyStore.SetDeviceAsync(tx, deviceId);
            var family = await FamilyStore.EnsureFamilyAsync(tx, userId);
            await FamilyStore.InsertBabyWithOwnerAsync(tx, userId, babyId, family, name!, request.BirthDate.Value, request.DueDate, sex, zone);
            return ToDto(await FamilyStore.GetBabyAsync(tx, babyId) ?? throw ProblemException.NotFound());
        }, ct);
    }

    /// <summary>Merge-patch (RFC 7396) do perfil. Campos ausentes não mudam; <c>due_date</c> e <c>sex</c> aceitam <c>null</c> para limpar.</summary>
    public async Task<BabyDto> UpdateAsync(Guid userId, Guid? deviceId, Guid babyId, JsonObject patch, long? expectedVersion, CancellationToken ct)
    {
        gate.General(userId);
        var v = new FamilyValidation();
        foreach (var (key, _) in patch)
        {
            if (!PatchFields.Contains(key))
            {
                v.Add(new FieldError(key, "UNKNOWN_FIELD"));
            }
        }

        string? name = null;
        string? sex = null;
        string? timezone = null;
        DateOnly? birth = null;
        DateOnly? due = null;
        if (patch.ContainsKey("display_name"))
        {
            name = patch["display_name"] is JsonValue dn && dn.TryGetValue<string>(out var s) ? v.DisplayName("display_name", s) : NotAString(v, "display_name", patch["display_name"]);
        }

        if (patch.ContainsKey("birth_date"))
        {
            birth = ParseDate(v, "birth_date", patch["birth_date"], nullable: false);
        }

        if (patch.ContainsKey("due_date"))
        {
            due = ParseDate(v, "due_date", patch["due_date"], nullable: true);
        }

        if (patch.ContainsKey("sex"))
        {
            sex = patch["sex"] switch
            {
                null => null,
                JsonValue sv when sv.TryGetValue<string>(out var sx) => v.Sex("sex", sx),
                _ => InvalidType(v, "sex"),
            };
        }

        if (patch.ContainsKey("timezone"))
        {
            timezone = patch["timezone"] is JsonValue tv && tv.TryGetValue<string>(out var tz) ? v.Timezone("timezone", tz) : NotAString(v, "timezone", patch["timezone"]);
        }

        v.ThrowIfInvalid();

        return await db.RunAsync(userId, async tx =>
        {
            await Guard.RequireAsync(tx, userId, babyId, ownerOnly: true);
            var current = await FamilyStore.GetBabyAsync(tx, babyId) ?? throw ProblemException.NotFound();
            if (expectedVersion is { } expected && expected != current.Version)
            {
                throw VersionConflict();
            }

            var changes = new Dictionary<string, (object? Value, NpgsqlDbType Type)>();
            if (patch.ContainsKey("display_name") && name is not null)
            {
                changes["display_name"] = (name, NpgsqlDbType.Text);
            }

            if (patch.ContainsKey("birth_date") && birth is not null)
            {
                changes["birth_date"] = (birth.Value, NpgsqlDbType.Date);
            }

            if (patch.ContainsKey("due_date"))
            {
                changes["due_date"] = (due, NpgsqlDbType.Date);
            }

            if (patch.ContainsKey("sex"))
            {
                changes["sex"] = (sex, NpgsqlDbType.Text);
            }

            if (patch.ContainsKey("timezone") && timezone is not null)
            {
                if (!await FamilyStore.IsKnownTimezoneAsync(tx, timezone))
                {
                    throw ProblemException.Validation(new FieldError("timezone", "INVALID_VALUE"));
                }

                changes["timezone"] = (timezone, NpgsqlDbType.Text);
            }

            if (changes.Count == 0)
            {
                return ToDto(current);
            }

            var effectiveZone = timezone ?? current.Timezone;
            if ((birth ?? current.BirthDate) > await FamilyStore.LocalDateAsync(tx, effectiveZone))
            {
                throw ProblemException.Validation(new FieldError("birth_date", "IN_FUTURE"));
            }

            await FamilyStore.SetDeviceAsync(tx, deviceId);
            if (await FamilyStore.UpdateBabyAsync(tx, babyId, changes, expectedVersion) == 0)
            {
                throw VersionConflict();
            }

            return ToDto(await FamilyStore.GetBabyAsync(tx, babyId) ?? throw ProblemException.NotFound());
        }, ct);
    }

    /// <summary>
    /// Exclusão pelo Owner (NR-07): reautenticação <c>BABY_DELETE</c> + reconhecimento quando há outros cuidadores ativos; o apagamento,
    /// a auditoria (com o jti) e o aviso aos cuidadores são feitos pela função <c>nina.delete_baby</c>.
    /// </summary>
    public async Task DeleteAsync(
        System.Security.Claims.ClaimsPrincipal user, Guid babyId, string? reauthToken, bool acknowledgeOthers, CancellationToken ct)
    {
        var userId = user.RequireUserId();
        gate.General(userId);
        gate.Sensitive(userId);
        await db.RunAsync(userId, async tx =>
        {
            await Guard.RequireAsync(tx, userId, babyId, ownerOnly: true);
            if (!acknowledgeOthers && await FamilyStore.CountOtherActiveMembersAsync(tx, babyId, userId) > 0)
            {
                throw ProblemException.Conflict("OWNER_DECISION_REQUIRED", "Acknowledge the other caregivers before deleting");
            }
        }, ct);

        var proof = await reauth.RequireAsync(user, reauthToken, ReauthScopes.BabyDelete, ct);
        var jtiHash = keys.Hmac("reauth-jti", proof.Jti); // mesma derivação de Identity.ReauthService (livro-razão de reauth)
        await db.RunAsync(userId, tx => FamilyStore.DeleteBabyAsync(tx, babyId, jtiHash, acknowledgeOthers), ct);
    }

    public static ProblemException VersionConflict() =>
        new(StatusCodes.Status412PreconditionFailed, "VERSION_CONFLICT", "Version conflict");

    // ---------------------------------------------------------------- mapeamento

    internal static BabyDto ToDto(BabyRow r)
    {
        var chronological = new AgeValueDto(r.ChronologicalDays, r.ChronologicalDays / 7, WholeMonths(r.BirthDate, r.AsOf));
        AgeValueDto? corrected = r is { CorrectedDays: { } days, DueDate: { } due }
            ? new AgeValueDto(days, days / 7, WholeMonths(due, r.AsOf))
            : null;
        return new BabyDto(
            r.Id, r.DisplayName, r.BirthDate, r.DueDate, r.Sex, r.Timezone, r.PhotoRef, r.MyRole ?? Roles.ReadOnly,
            new AgeDto(r.AsOf, chronological, corrected, r.CorrectionApplied ? "CORRECTED" : "CHRONOLOGICAL"),
            new AgeCalculationDto(r.ChronologicalDays, r.CorrectedDays, r.CorrectionApplied),
            r.Version, r.CreatedAt, r.UpdatedAt);
    }

    /// <summary>Meses completos entre duas datas civis (0 se <paramref name="to"/> for anterior).</summary>
    internal static int WholeMonths(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            return 0;
        }

        var months = ((to.Year - from.Year) * 12) + to.Month - from.Month;
        return to.Day < from.Day ? months - 1 : months;
    }

    private static string? NotAString(FamilyValidation v, string field, JsonNode? node)
    {
        v.Add(new FieldError(field, node is null ? "REQUIRED" : "INVALID_FORMAT"));
        return null;
    }

    private static string? InvalidType(FamilyValidation v, string field)
    {
        v.Add(new FieldError(field, "INVALID_FORMAT"));
        return null;
    }

    private static DateOnly? ParseDate(FamilyValidation v, string field, JsonNode? node, bool nullable)
    {
        if (node is null)
        {
            if (!nullable)
            {
                v.Add(new FieldError(field, "REQUIRED"));
            }

            return null;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text)
            && DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
        {
            return date;
        }

        v.Add(new FieldError(field, "INVALID_FORMAT"));
        return null;
    }
}
