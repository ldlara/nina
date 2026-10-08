using Microsoft.Extensions.Options;
using Nina.Identity.Contracts;
using Nina.Identity.Crypto;
using Nina.Identity.External;
using Nina.Identity.Mail;
using Nina.Identity.Persistence;
using Nina.SharedKernel.Audit;
using Nina.SharedKernel.Data;
using Nina.SharedKernel.Http;
using Nina.SharedKernel.Security;
using Npgsql;

namespace Nina.Identity.Services;

/// <summary>Perfil, senha, identidades vinculadas, sessões/dispositivos e tokens de push do usuário autenticado.</summary>
public sealed class AccountService(
    NinaDb db,
    IOptions<IdentityOptions> options,
    PasswordHasher hasher,
    PasswordPolicy policy,
    AuthService auth,
    IIdentityMailer mailer,
    AuditLog audit,
    TimeProvider time)
{
    private IdentityOptions Opt => options.Value;

    public async Task<UserDto> GetMeAsync(Guid userId, CancellationToken ct) =>
        await db.InTransactionAsync(userId, async tx =>
        {
            var user = await IdentityStore.FindUserByIdAsync(tx, userId) ?? throw ProblemException.Unauthorized("INVALID_TOKEN", "Invalid token");
            return await UserMapper.ToDtoAsync(tx, user, Opt.DefaultLocale);
        }, ct);

    public async Task<UserDto> UpdateMeAsync(Guid userId, UserUpdateRequest req, CancellationToken ct)
    {
        var v = new Validation();
        if (req.DisplayName is { Length: > 80 })
        {
            v.Add(new FieldError("display_name", "TOO_LONG"));
        }

        var locale = v.Locale("locale", req.Locale);
        var timezone = v.Timezone("timezone", req.Timezone);
        v.ThrowIfInvalid();

        return await db.InTransactionAsync(userId, async tx =>
        {
            // display_name não é persistido: o schema 0001 não tem a coluna (ver relatório BE-001).
            await IdentityStore.UpdateProfileAsync(tx, userId, locale, timezone);
            var user = await IdentityStore.FindUserByIdAsync(tx, userId) ?? throw ProblemException.Unauthorized("INVALID_TOKEN", "Invalid token");
            return await UserMapper.ToDtoAsync(tx, user, Opt.DefaultLocale);
        }, ct);
    }

    public async Task ChangePasswordAsync(Guid userId, Guid sessionId, ChangePasswordRequest req, CancellationToken ct)
    {
        var v = new Validation();
        var password = v.Required("new_password", req.NewPassword, Opt.PasswordMaxLength);
        v.ThrowIfInvalid();

        var email = await db.InTransactionAsync(userId, async tx => (await IdentityStore.FindUserByIdAsync(tx, userId))?.Email, ct);
        v.Add(await policy.ValidateAsync("new_password", password!, email, ct));
        v.ThrowIfInvalid();

        var hash = await hasher.HashAsync(password!);
        var now = time.GetUtcNow();
        var notify = await db.InTransactionAsync(userId, async tx =>
        {
            var user = await IdentityStore.FindUserByIdAsync(tx, userId) ?? throw ProblemException.Unauthorized("INVALID_TOKEN", "Invalid token");
            await IdentityStore.UpsertCredentialAsync(tx, userId, hash, now);
            var devices = await IdentityStore.RevokeAllSessionsAsync(tx, userId, sessionId, "PASSWORD_CHANGED", now);
            await IdentityStore.DeletePushTokensAsync(tx, userId, devices);
            await audit.AppendAsync(tx, new AuditEntry("auth.password_changed", userId, "user", userId, IsCritical: true));
            return (user.Email, Locale: user.Locale ?? Opt.DefaultLocale);
        }, ct);
        await mailer.SendSecurityNoticeAsync(notify.Email, SecurityNotice.PasswordChanged, notify.Locale, ct);
    }

    public async Task<UserDto> LinkIdentityAsync(Guid userId, LinkIdentityRequest req, CancellationToken ct)
    {
        var v = new Validation();
        if (!Providers.IsKnown(req.Provider))
        {
            v.Add(new FieldError("provider", req.Provider is null ? "REQUIRED" : "UNSUPPORTED_VALUE"));
        }

        var idToken = v.Required("id_token", req.IdToken, 4096, 16);
        var nonce = v.Required("nonce", req.Nonce, 256, 8);
        v.ThrowIfInvalid();

        var identity = await auth.VerifyExternalAsync(req.Provider!, idToken!, nonce!, null, ct);
        var now = time.GetUtcNow();
        var (dto, email, locale) = await db.InTransactionAsync(userId, async tx =>
        {
            var existing = await IdentityStore.ListIdentitiesAsync(tx, userId);
            if (existing.Any(i => i.Provider == identity.Provider))
            {
                throw ProblemException.Conflict("IDENTITY_ALREADY_LINKED", "A identity of this provider is already linked");
            }

            if (await IdentityStore.FindIdentityAsync(tx, identity.Provider, identity.Subject) is not null)
            {
                throw ProblemException.Conflict("IDENTITY_IN_USE", "This identity belongs to another account");
            }

            await IdentityStore.InsertIdentityAsync(tx, userId, identity.Provider, identity.Subject, identity.Email, now);
            await audit.AppendAsync(tx, new AuditEntry(
                "auth.identity_linked", userId, "user", userId, IsCritical: true,
                Metadata: new Dictionary<string, object?> { ["provider"] = identity.Provider }));
            var user = await IdentityStore.FindUserByIdAsync(tx, userId) ?? throw ProblemException.Unauthorized("INVALID_TOKEN", "Invalid token");
            return (await UserMapper.ToDtoAsync(tx, user, Opt.DefaultLocale), user.Email, user.Locale ?? Opt.DefaultLocale);
        }, ct);
        await mailer.SendSecurityNoticeAsync(email, SecurityNotice.IdentityLinked, locale, ct);
        return dto;
    }

    // ------------------------------------------------------------ sessões

    public async Task<SessionList> ListSessionsAsync(Guid userId, Guid currentSessionId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var rows = await db.InTransactionAsync(userId, tx => IdentityStore.ListActiveSessionsAsync(tx, userId, now), ct);
        return new SessionList([.. rows.Select(s => new SessionDto(
            s.Id, s.DeviceId, s.DeviceLabel, s.Platform, s.AppVersion, s.CreatedAt, Coarsen(s.LastSeenAt), null, s.Id == currentSessionId))]);
    }

    public async Task RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await db.InTransactionAsync(userId, async tx =>
        {
            var session = await IdentityStore.GetSessionAsync(tx, sessionId, forUpdate: true) ?? throw ProblemException.NotFound();
            if (session.RevokedAt is not null)
            {
                return;
            }

            await IdentityStore.RevokeSessionAsync(tx, sessionId, "USER_REVOKED", now);
            await IdentityStore.DeletePushTokensAsync(tx, userId, [session.DeviceId]);
            await audit.AppendAsync(tx, new AuditEntry("auth.session_revoked", userId, "session", sessionId, session.DeviceId));
        }, ct);
    }

    public async Task<RevokedCountDto> RevokeOtherSessionsAsync(Guid userId, Guid currentSessionId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return await db.InTransactionAsync(userId, async tx =>
        {
            var devices = await IdentityStore.RevokeAllSessionsAsync(tx, userId, currentSessionId, "USER_REVOKED", now);
            var current = await IdentityStore.GetSessionAsync(tx, currentSessionId);
            var toClean = devices.Where(d => d != current?.DeviceId).Distinct().ToList();
            if (toClean.Count > 0)
            {
                await IdentityStore.DeletePushTokensAsync(tx, userId, toClean);
            }

            await audit.AppendAsync(tx, new AuditEntry(
                "auth.sessions_revoked", userId, "session", currentSessionId,
                Metadata: new Dictionary<string, object?> { ["count"] = devices.Count }));
            return new RevokedCountDto(devices.Count);
        }, ct);
    }

    // ------------------------------------------------------------ push tokens

    public async Task<PushTokenDto> RegisterPushTokenAsync(Guid userId, Guid deviceId, PushTokenRequest req, CancellationToken ct)
    {
        var v = new Validation();
        if (req.Platform is not ("APNS" or "FCM"))
        {
            v.Add(new FieldError("platform", req.Platform is null ? "REQUIRED" : "UNSUPPORTED_VALUE"));
        }

        var token = v.Required("token", req.Token, 4096);
        if (req.Environment is not (null or "PRODUCTION" or "SANDBOX"))
        {
            v.Add(new FieldError("environment", "UNSUPPORTED_VALUE"));
        }

        v.Locale("locale", req.Locale);
        if (req.AppVersion is { Length: > 32 })
        {
            v.Add(new FieldError("app_version", "TOO_LONG"));
        }

        v.ThrowIfInvalid();

        var now = time.GetUtcNow();
        try
        {
            return await db.InTransactionAsync(userId, async tx =>
            {
                var session = (await IdentityStore.ListActiveSessionsAsync(tx, userId, now)).FirstOrDefault(s => s.DeviceId == deviceId)
                              ?? throw ProblemException.NotFound();
                if ((session.Platform == "IOS") != (req.Platform == "APNS"))
                {
                    throw ProblemException.Validation(new FieldError("platform", "PLATFORM_MISMATCH"));
                }

                var row = await IdentityStore.UpsertPushTokenAsync(tx, userId, deviceId, req.Platform!, token!, now)
                          ?? throw ProblemException.NotFound();
                await audit.AppendAsync(tx, new AuditEntry("push_token.registered", userId, "device", deviceId, deviceId));
                return new PushTokenDto(deviceId, req.Platform!, row.Created, row.Updated);
            }, ct);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.InsufficientPrivilege)
        {
            // O mesmo token já pertence a outro usuário/dispositivo (invisível por RLS); não vaza quem.
            throw ProblemException.Conflict("PUSH_TOKEN_CONFLICT", "Push token already registered");
        }
    }

    public async Task UnregisterPushTokenAsync(Guid userId, Guid deviceId, CancellationToken ct) =>
        await db.InTransactionAsync(userId, async tx =>
        {
            await IdentityStore.DeletePushTokensAsync(tx, userId, [deviceId]);
            await audit.AppendAsync(tx, new AuditEntry("push_token.removed", userId, "device", deviceId, deviceId));
        }, ct);

    // Precisão reduzida (RF-054-A3): arredonda para 5 minutos.
    private static DateTimeOffset Coarsen(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % TimeSpan.FromMinutes(5).Ticks), TimeSpan.Zero);
}
