using Microsoft.Extensions.Logging;
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

/// <summary>Cadastro, verificação, login (senha/Google/Apple), refresh rotativo, logout, recuperação e reautenticação.</summary>
public sealed partial class AuthService(
    NinaDb db,
    IOptions<IdentityOptions> options,
    PasswordHasher hasher,
    PasswordPolicy policy,
    TokenService tokens,
    SessionIssuer issuer,
    ConsentService consents,
    IIdentityTokenVerifier verifier,
    IIdentityMailer mailer,
    IRateLimiter limiter,
    SecretKeys keys,
    AuditLog audit,
    IRequestContext request,
    TimeProvider time,
    ILogger<AuthService> logger)
{
    private IdentityOptions Opt => options.Value;

    private RateLimitSettings Limits => options.Value.RateLimits;

    private TimeSpan FailureWindow => TimeSpan.FromMinutes(Limits.FailureWindowMinutes);

    private string Ip => request.ClientIp?.ToString() ?? "unknown";

    // ------------------------------------------------------------------ cadastro

    public async Task<VerificationPending> RegisterAsync(RegisterRequest req, CancellationToken ct)
    {
        Enforce(limiter.Consume($"register:ip:{Ip}", Limits.RegisterPerIpPerHour, TimeSpan.FromHours(1)));

        var v = new Validation();
        var email = v.Email("email", req.Email);
        var password = v.Required("password", req.Password, Opt.PasswordMaxLength);
        if (req.DisplayName is { Length: > 80 })
        {
            v.Add(new FieldError("display_name", "TOO_LONG"));
        }

        var locale = v.Locale("locale", req.Locale);
        if (req.Locale is null)
        {
            v.Add(new FieldError("locale", "REQUIRED"));
        }

        var timezone = v.Timezone("timezone", req.Timezone);
        v.ThrowIfInvalid();
        v.Add(await policy.ValidateAsync("password", password!, email, ct));
        v.ThrowIfInvalid();

        var emailAllowed = limiter.Consume($"register:email:{email}", Limits.RegisterPerEmailPerHour, TimeSpan.FromHours(1)).Allowed;

        // Custo de hash idêntico para e-mail novo e existente (SEC-050).
        var hash = await hasher.HashAsync(password!);
        var code = OpaqueTokens.NewVerificationCode(Opt.VerificationCodeDigits);
        var now = time.GetUtcNow();

        RegisterOutcome outcome;
        try
        {
            outcome = await db.InTransactionAsync(null, tx => RegisterCoreAsync(tx, req, email!, locale!, timezone, hash, code, emailAllowed, now), ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            outcome = RegisterOutcome.None; // corrida entre cadastros do mesmo e-mail: a resposta continua uniforme
        }

        switch (outcome)
        {
            case RegisterOutcome.SendCode:
                await TrySendAsync(() => mailer.SendVerificationCodeAsync(email!, code, locale!, ct));
                break;
            case RegisterOutcome.NotifyExisting:
                await TrySendAsync(() => mailer.SendAlreadyRegisteredAsync(email!, locale!, ct));
                break;
            default:
                break;
        }

        return new VerificationPending("VERIFICATION_PENDING", Opt.ResendAfterSeconds);
    }

    private async Task<RegisterOutcome> RegisterCoreAsync(
        DbTx tx, RegisterRequest req, string email, string locale, string? timezone, string hash, string code, bool emailAllowed, DateTimeOffset now)
    {
        var (accepted, errors, missing) = await ConsentService.ResolveOnboardingAsync(tx, req.Consents, "consents");
        if (missing.Count > 0 && errors.Count == 0)
        {
            errors.Add(new FieldError("consents", "REQUIRED"));
        }

        if (errors.Count > 0)
        {
            throw ProblemException.Validation(errors);
        }

        var existing = await IdentityStore.FindUserByEmailAsync(tx, email);
        if (existing is { } user && (user.EmailVerifiedAt is not null || user.Status != "ACTIVE"))
        {
            return emailAllowed ? RegisterOutcome.NotifyExisting : RegisterOutcome.None;
        }

        if (!emailAllowed)
        {
            return RegisterOutcome.None;
        }

        Guid userId;
        if (existing is null)
        {
            userId = Guid.NewGuid();
            await IdentityStore.InsertUserAsync(tx, userId, email, locale, timezone, null, now);
        }
        else
        {
            // Cadastro ainda não confirmado: respeita o intervalo de reenvio e substitui a senha (anti pré-sequestro).
            userId = existing.Id;
            var last = await IdentityStore.LastRecoveryCreatedAsync(tx, userId);
            if (last is { } l && now - l < TimeSpan.FromSeconds(Opt.ResendAfterSeconds))
            {
                return RegisterOutcome.None;
            }
        }

        await tx.SetUserAsync(userId);
        await IdentityStore.UpsertCredentialAsync(tx, userId, hash, now);
        await IdentityStore.InvalidateOpenRecoveryAsync(tx, userId, now);
        await IdentityStore.InsertRecoveryAsync(tx, userId, VerificationHash(userId, code), now, now.AddMinutes(Opt.VerificationCodeMinutes));
        await consents.RecordOnboardingAsync(tx, userId, accepted, locale, null);
        await audit.AppendAsync(tx, new AuditEntry("auth.register", userId, "user", userId));
        return RegisterOutcome.SendCode;
    }

    public async Task<TokenResponse> VerifyEmailAsync(EmailVerifyRequest req, CancellationToken ct)
    {
        var v = new Validation();
        var email = v.Email("email", req.Email);
        var code = v.VerificationCode("code", req.Code);
        var device = v.Device("device", req.Device);
        v.ThrowIfInvalid();

        // Consome antes de verificar: tentativas paralelas não furam o limite; zera no sucesso (AD-34: 5 tentativas por código).
        var attempt = limiter.Consume($"verify:email:{email}", Opt.VerificationCodeAttempts, FailureWindow);
        Enforce(attempt);
        EnforcePeek($"verify:ip:{Ip}", Limits.VerifyFailuresPerIp);

        var now = time.GetUtcNow();
        var result = await db.InTransactionAsync(null, async tx =>
        {
            var user = await IdentityStore.FindUserByEmailAsync(tx, email!);
            if (user is null || user.Status != "ACTIVE")
            {
                return null;
            }

            var owner = await IdentityStore.ConsumeRecoveryAsync(tx, VerificationHash(user.Id, code!), now);
            if (owner != user.Id)
            {
                if (attempt.Count >= Opt.VerificationCodeAttempts)
                {
                    // Tentativas esgotadas: o código vigente é invalidado e será preciso pedir outro (AD-34).
                    await IdentityStore.InvalidateOpenRecoveryAsync(tx, user.Id, now);
                }

                return null;
            }

            await IdentityStore.MarkEmailVerifiedAsync(tx, user.Id, now);
            var verified = user with { EmailVerifiedAt = now };
            return await issuer.IssueAsync(tx, verified, device!, "auth.email_verified", "EMAIL_CODE");
        }, ct);

        if (result is null)
        {
            limiter.Record($"verify:ip:{Ip}", FailureWindow);
            await AuditFailureAsync("auth.email_verify_failed", null, device!.DeviceId, ct);
            throw ProblemException.Unauthorized("INVALID_VERIFICATION_CODE", "Invalid or expired code");
        }

        limiter.Reset($"verify:email:{email}");
        return result;
    }

    // --------------------------------------------------------------------- login

    public async Task<TokenResponse> LoginAsync(LoginRequest req, CancellationToken ct)
    {
        var v = new Validation();
        var email = v.Email("email", req.Email);
        var password = v.Required("password", req.Password, Opt.PasswordMaxLength);
        var device = v.Device("device", req.Device);
        v.ThrowIfInvalid();

        var accountIp = $"login:acct-ip:{email}:{Ip}";
        var account = $"login:acct:{email}";
        var ipKey = $"login:ip:{Ip}";
        var deviceKey = $"login:device:{device!.DeviceId:N}";
        // Bloqueio progressivo (SEC-041): cada novo estouro dobra o tempo, até o teto; o Retry-After cresce entre bloqueios.
        var blockKey = $"login:block:{email}:{Ip}";
        Enforce(limiter.CheckBlocked(blockKey));
        // Consome antes de verificar (limite por conta+IP vale para tentativas paralelas); zera no sucesso.
        if (!limiter.Consume(accountIp, Limits.LoginFailuresPerAccountAndIp, FailureWindow).Allowed)
        {
            var strikes = limiter.Consume($"login:strikes:{email}:{Ip}", int.MaxValue, TimeSpan.FromHours(Limits.LoginMaxLockoutHours)).Count;
            var lockout = TimeSpan.FromTicks(Math.Min(
                FailureWindow.Ticks * (1L << Math.Min(strikes - 1, 10)),
                TimeSpan.FromHours(Limits.LoginMaxLockoutHours).Ticks));
            limiter.Block(blockKey, lockout);
            throw ProblemException.RateLimited((int)lockout.TotalSeconds);
        }

        EnforcePeek(account, Limits.LoginFailuresPerAccount);
        EnforcePeek(ipKey, Limits.LoginFailuresPerIp);
        EnforcePeek(deviceKey, Limits.LoginFailuresPerDevice);

        var user = await db.InTransactionAsync(null, tx => IdentityStore.FindUserByEmailAsync(tx, email!), ct);
        var check = new PasswordCheck(false, false);
        if (user?.PasswordHash is { } stored)
        {
            check = await hasher.VerifyAsync(password!, stored);
        }
        else
        {
            await hasher.BurnAsync(password!);
        }

        if (!check.Valid || user is null || user.EmailVerifiedAt is null)
        {
            limiter.Record(account, FailureWindow);
            limiter.Record(ipKey, FailureWindow);
            limiter.Record(deviceKey, FailureWindow);
            await AuditFailureAsync("auth.login_failed", user?.Id, device.DeviceId, ct);
            throw ProblemException.Unauthorized("INVALID_CREDENTIALS", "Invalid credentials");
        }

        limiter.Reset(accountIp);
        limiter.Reset($"login:strikes:{email}:{Ip}");
        limiter.Reset(blockKey);
        var newHash = check.NeedsRehash ? await hasher.HashAsync(password!) : null;
        var now = time.GetUtcNow();
        var (response, newDevice) = await db.InTransactionAsync(user.Id, async tx =>
        {
            if (newHash is not null)
            {
                await IdentityStore.UpsertCredentialAsync(tx, user.Id, newHash, now);
            }

            await tx.SetUserAsync(user.Id);
            var known = await IdentityStore.DeviceHasAnySessionAsync(tx, user.Id, device.DeviceId!.Value);
            return (await issuer.IssueAsync(tx, user, device, "auth.login", "PASSWORD"), !known);
        }, ct);

        if (newDevice)
        {
            // Aviso de login em dispositivo novo (SEC-041); falha de envio não derruba o login.
            await TrySendAsync(() => mailer.SendSecurityNoticeAsync(user.Email, SecurityNotice.NewDeviceLogin, user.Locale ?? Opt.DefaultLocale, ct));
        }

        return response;
    }

    public async Task<TokenResponse> LoginSocialAsync(string provider, SocialLoginRequest req, CancellationToken ct)
    {
        Enforce(limiter.Consume($"social:ip:{Ip}", Limits.SocialPerIpPerMinute, TimeSpan.FromMinutes(1)));

        var v = new Validation();
        var idToken = v.Required("id_token", req.IdToken, 4096, 16);
        var nonce = v.Required("nonce", req.Nonce, 256, 8);
        var device = v.Device("device", req.Device);
        var locale = v.Locale("locale", req.Locale);
        var timezone = v.Timezone("timezone", req.Timezone);
        v.ThrowIfInvalid();

        var identity = await VerifyExternalAsync(provider, idToken!, nonce!, device!.DeviceId, ct);
        var now = time.GetUtcNow();
        return await db.InTransactionAsync(null, async tx =>
        {
            var link = await IdentityStore.FindIdentityAsync(tx, provider, identity.Subject);
            if (link is not null)
            {
                var linked = await IdentityStore.FindUserByIdAsync(tx, link.UserId)
                             ?? throw ProblemException.Unauthorized("INVALID_ID_TOKEN", "Invalid identity token");
                return await issuer.IssueAsync(tx, linked, device, "auth.login", provider);
            }

            if (string.IsNullOrWhiteSpace(identity.Email) || !identity.EmailVerified)
            {
                throw ProblemException.Unauthorized("INVALID_ID_TOKEN", "Invalid identity token");
            }

            var email = Validation.NormalizeEmail(identity.Email);
            if (await IdentityStore.FindUserByEmailAsync(tx, email) is not null)
            {
                // ADR-0007: nunca funde contas pelo e-mail; o usuário entra pelo método original e vincula em /me/identities.
                throw ProblemException.Conflict("IDENTITY_LINK_REQUIRED", "Sign in with your original method and link this identity");
            }

            var (accepted, errors, missing) = await ConsentService.ResolveOnboardingAsync(tx, req.Consents, "consents");
            if (errors.Count > 0)
            {
                throw ProblemException.Validation(errors);
            }

            if (missing.Count > 0)
            {
                throw new ProblemException(StatusCodes.Status403Forbidden, "CONSENT_REQUIRED", "Consent required")
                {
                    Extensions = new Dictionary<string, object?> { ["required_consents"] = missing },
                };
            }

            var userId = Guid.NewGuid();
            var effectiveLocale = locale ?? Opt.DefaultLocale;
            await IdentityStore.InsertUserAsync(tx, userId, email, effectiveLocale, timezone, now, now);
            await tx.SetUserAsync(userId);
            await IdentityStore.InsertIdentityAsync(tx, userId, provider, identity.Subject, email, now);
            await consents.RecordOnboardingAsync(tx, userId, accepted, effectiveLocale, device);
            await audit.AppendAsync(tx, new AuditEntry("auth.register", userId, "user", userId, device.DeviceId, Metadata: new Dictionary<string, object?> { ["method"] = provider }));
            var created = await IdentityStore.FindUserByIdAsync(tx, userId);
            return await issuer.IssueAsync(tx, created!, device, "auth.login", provider);
        }, ct);
    }

    public async Task<VerifiedIdentity> VerifyExternalAsync(string provider, string idToken, string nonce, Guid? deviceId, CancellationToken ct)
    {
        try
        {
            return await verifier.VerifyAsync(provider, idToken, nonce, ct);
        }
        catch (IdentityTokenException)
        {
            await AuditFailureAsync("auth.login_failed", null, deviceId, ct);
            throw ProblemException.Unauthorized("INVALID_ID_TOKEN", "Invalid identity token");
        }
    }

    // ------------------------------------------------------------ refresh / logout

    public async Task<TokenResponse> RefreshAsync(RefreshRequest req, CancellationToken ct)
    {
        Enforce(limiter.Consume($"refresh:ip:{Ip}", Limits.RefreshPerIpPerMinute, TimeSpan.FromMinutes(1)));
        var v = new Validation();
        var token = v.Required("refresh_token", req.RefreshToken, 512, 16);
        if (req.DeviceId is null || req.DeviceId == Guid.Empty)
        {
            v.Add(new FieldError("device_id", "REQUIRED"));
        }

        v.ThrowIfInvalid();
        if (!OpaqueTokens.TryParseRefreshToken(token, out var userId, out var sessionId))
        {
            throw ProblemException.Unauthorized("INVALID_REFRESH_TOKEN", "Invalid refresh token");
        }

        var now = time.GetUtcNow();
        var hash = OpaqueTokens.HashRefreshToken(token!);
        var outcome = await db.InTransactionAsync(userId, async tx =>
        {
            var stored = await IdentityStore.GetRefreshTokenForUpdateAsync(tx, hash);
            if (stored is null || stored.SessionId != sessionId)
            {
                return RefreshResult.Fail("INVALID_REFRESH_TOKEN");
            }

            // RLS: se o userId do token não for o dono da sessão, a linha não aparece.
            var session = await IdentityStore.GetSessionAsync(tx, sessionId, forUpdate: true);
            if (session is null || session.DeviceId != req.DeviceId)
            {
                return RefreshResult.Fail("INVALID_REFRESH_TOKEN");
            }

            if (session.RevokedAt is not null)
            {
                return RefreshResult.Fail("SESSION_REVOKED");
            }

            if (stored.UsedAt is not null)
            {
                // Reuso de token já rotacionado: derruba a família inteira (SEC-011).
                await IdentityStore.RevokeSessionAsync(tx, sessionId, "REUSE_DETECTED", now);
                await IdentityStore.DeletePushTokensAsync(tx, userId, [session.DeviceId]);
                await audit.AppendAsync(tx, new AuditEntry(
                    "auth.refresh_reuse_detected", userId, "session", sessionId, session.DeviceId, "DENIED", IsCritical: true));
                return RefreshResult.Fail("REFRESH_TOKEN_REUSED");
            }

            if (now >= stored.ExpiresAt || now >= session.AbsoluteExpiresAt)
            {
                return RefreshResult.Fail("SESSION_EXPIRED");
            }

            var user = await IdentityStore.FindUserByIdAsync(tx, userId);
            if (user is null)
            {
                return RefreshResult.Fail("INVALID_REFRESH_TOKEN");
            }

            await IdentityStore.MarkRefreshTokenUsedAsync(tx, stored.Id, now);
            var next = OpaqueTokens.NewRefreshToken(userId, sessionId);
            var expires = SessionIssuer.Min(now.AddDays(Opt.RefreshTokenDays), session.AbsoluteExpiresAt);
            await IdentityStore.InsertRefreshTokenAsync(tx, sessionId, OpaqueTokens.HashRefreshToken(next), now, expires);
            await IdentityStore.TouchSessionAsync(tx, sessionId, now);
            var response = await issuer.BuildAsync(tx, user, sessionId, session.DeviceId, next, expires, Opt.DefaultLocale);
            return RefreshResult.Ok(response);
        }, ct);

        if (outcome.Response is null)
        {
            throw ProblemException.Unauthorized(outcome.ErrorCode!, outcome.ErrorCode switch
            {
                "SESSION_REVOKED" => "Session revoked",
                "REFRESH_TOKEN_REUSED" => "Refresh token reused",
                "SESSION_EXPIRED" => "Session expired",
                _ => "Invalid refresh token",
            });
        }

        return outcome.Response;
    }

    public async Task LogoutAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await db.InTransactionAsync(userId, async tx =>
        {
            var session = await IdentityStore.GetSessionAsync(tx, sessionId, forUpdate: true);
            if (session is null)
            {
                return;
            }

            if (session.RevokedAt is null)
            {
                await IdentityStore.RevokeSessionAsync(tx, sessionId, "LOGOUT", now);
                await audit.AppendAsync(tx, new AuditEntry("auth.logout", userId, "session", sessionId, session.DeviceId));
            }

            await IdentityStore.DeletePushTokensAsync(tx, userId, [session.DeviceId]);
        }, ct);
    }

    // ------------------------------------------------------- recuperação de senha

    public async Task ForgotPasswordAsync(ForgotPasswordRequest req, CancellationToken ct)
    {
        Enforce(limiter.Consume($"forgot:ip:{Ip}", Limits.ForgotPerIpPerHour, TimeSpan.FromHours(1)));
        var v = new Validation();
        var email = v.Email("email", req.Email);
        v.ThrowIfInvalid();
        var allowed = limiter.Consume($"forgot:email:{email}", Limits.ForgotPerEmailPerHour, TimeSpan.FromHours(1)).Allowed;

        var token = OpaqueTokens.NewUrlToken();
        var now = time.GetUtcNow();
        var locale = await db.InTransactionAsync(null, async tx =>
        {
            var user = await IdentityStore.FindUserByEmailAsync(tx, email!);
            if (user is null || user.Status != "ACTIVE" || !allowed)
            {
                return null;
            }

            await IdentityStore.InsertRecoveryAsync(tx, user.Id, ResetHash(token), now, now.AddMinutes(Opt.PasswordResetMinutes));
            await audit.AppendAsync(tx, new AuditEntry("auth.password_reset_requested", user.Id, "user", user.Id));
            return user.Locale ?? Opt.DefaultLocale;
        }, ct);

        if (locale is not null)
        {
            await TrySendAsync(() => mailer.SendPasswordResetAsync(email!, token, locale, ct));
        }
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest req, CancellationToken ct)
    {
        EnforcePeek($"reset:ip:{Ip}", Limits.ResetFailuresPerIp);
        var v = new Validation();
        var token = v.Required("token", req.Token, 512, 16);
        var password = v.Required("new_password", req.NewPassword, Opt.PasswordMaxLength);
        v.ThrowIfInvalid();
        v.Add(await policy.ValidateAsync("new_password", password!, null, ct));
        v.ThrowIfInvalid();

        var hash = await hasher.HashAsync(password!);
        var now = time.GetUtcNow();
        var notify = await db.InTransactionAsync<(string Email, string Locale)?>(null, async tx =>
        {
            var userId = await IdentityStore.ConsumeRecoveryAsync(tx, ResetHash(token!), now);
            if (userId is null)
            {
                return null;
            }

            await tx.SetUserAsync(userId.Value);
            var user = await IdentityStore.FindUserByIdAsync(tx, userId.Value);
            if (user is null || user.Status != "ACTIVE")
            {
                return null;
            }

            await IdentityStore.UpsertCredentialAsync(tx, userId.Value, hash, now);
            await IdentityStore.MarkEmailVerifiedAsync(tx, userId.Value, now);
            await IdentityStore.InvalidateOpenRecoveryAsync(tx, userId.Value, now);
            var devices = await IdentityStore.RevokeAllSessionsAsync(tx, userId.Value, null, "PASSWORD_CHANGED", now);
            await IdentityStore.DeletePushTokensAsync(tx, userId.Value, devices);
            await audit.AppendAsync(tx, new AuditEntry("auth.password_reset", userId, "user", userId, IsCritical: true));
            return (user.Email, user.Locale ?? Opt.DefaultLocale);
        }, ct);

        if (notify is null)
        {
            limiter.Record($"reset:ip:{Ip}", FailureWindow);
            throw ProblemException.Unauthorized("INVALID_RESET_TOKEN", "Invalid or expired token");
        }

        await TrySendAsync(() => mailer.SendSecurityNoticeAsync(notify.Value.Email, SecurityNotice.PasswordReset, notify.Value.Locale, ct));
    }

    // ------------------------------------------------------------ reautenticação

    public async Task<ReauthResponse> ReauthenticateAsync(Guid userId, Guid sessionId, ReauthRequest req, CancellationToken ct)
    {
        var key = $"reauth:user:{userId:N}";
        EnforcePeek($"reauth:ip:{Ip}", Limits.ReauthFailuresPerUser * 4);

        // Exatamente um entre `password` e (`provider`, `id_token`, `nonce`) (oneOf do contrato).
        var v = new Validation();
        var usingProvider = req.Provider is not null || req.IdToken is not null || req.Nonce is not null;
        if (req.Password is null && !usingProvider)
        {
            v.Add(new FieldError("password", "REQUIRED"));
        }

        if (req.Password is not null && usingProvider)
        {
            v.Add(new FieldError("password", "CONFLICTING_CREDENTIALS"));
        }

        if (req.Password is not null)
        {
            v.Required("password", req.Password, Opt.PasswordMaxLength);
        }

        if (usingProvider)
        {
            if (!Providers.IsKnown(req.Provider))
            {
                v.Add(new FieldError("provider", req.Provider is null ? "REQUIRED" : "UNSUPPORTED_VALUE"));
            }

            v.Required("id_token", req.IdToken, 4096, 16);
            v.Required("nonce", req.Nonce, 256, 8);
        }

        var scopes = ValidateScopes(v, req.Scope);
        v.ThrowIfInvalid();

        // Requisições inválidas não gastam tentativas; as válidas consomem antes da checagem (paralelismo não fura o limite).
        Enforce(limiter.Consume(key, Limits.ReauthFailuresPerUser, FailureWindow));

        var user = await db.InTransactionAsync(userId, tx => IdentityStore.FindUserByIdAsync(tx, userId), ct);
        var ok = false;
        if (user is not null)
        {
            if (usingProvider)
            {
                var identity = await VerifyExternalAsync(req.Provider!, req.IdToken!, req.Nonce!, null, ct);
                var link = await db.InTransactionAsync(null, tx => IdentityStore.FindIdentityAsync(tx, identity.Provider, identity.Subject), ct);
                ok = link?.UserId == userId;
            }
            else if (user.PasswordHash is { } stored)
            {
                ok = (await hasher.VerifyAsync(req.Password!, stored)).Valid;
            }
            else
            {
                await hasher.BurnAsync(req.Password!);
            }
        }

        if (!ok)
        {
            limiter.Record($"reauth:ip:{Ip}", FailureWindow);
            await AuditFailureAsync("auth.reauth_failed", userId, null, ct);
            throw ProblemException.Unauthorized("INVALID_CREDENTIALS", "Invalid credentials");
        }

        limiter.Reset(key);
        var (token, expiresIn, jti) = tokens.IssueReauthToken(userId, sessionId, scopes);
        await db.InTransactionAsync(userId, tx => audit.AppendAsync(tx, new AuditEntry(
            "auth.reauthenticated", userId, "session", sessionId,
            Metadata: new Dictionary<string, object?> { ["jti"] = jti, ["scopes"] = scopes ?? [] })), ct);
        return new ReauthResponse(token, expiresIn, scopes?.ToList());
    }

    private static List<string>? ValidateScopes(Validation v, List<string>? scope)
    {
        if (scope is null)
        {
            return null; // transição da v1.x: token de uso único para uma operação sensível qualquer
        }

        if (scope.Count is < 1 or > 3)
        {
            v.Add(new FieldError("scope", scope.Count == 0 ? "REQUIRED" : "TOO_MANY"));
        }

        for (var i = 0; i < scope.Count; i++)
        {
            if (!ReauthScopes.All.Contains(scope[i]))
            {
                v.Add(new FieldError($"scope[{i}]", "UNSUPPORTED_VALUE"));
            }
        }

        if (scope.Distinct(StringComparer.Ordinal).Count() != scope.Count)
        {
            v.Add(new FieldError("scope", "DUPLICATE"));
        }

        return scope;
    }

    // ------------------------------------------------------------------- helpers

    private byte[] VerificationHash(Guid userId, string code) => keys.Hmac("email-verify", $"{userId:N}:{code}");

    private byte[] ResetHash(string token) => keys.Hmac("password-reset", token);

    private static void Enforce(RateLimitDecision decision)
    {
        if (!decision.Allowed)
        {
            throw ProblemException.RateLimited(decision.RetryAfterSeconds);
        }
    }

    private void EnforcePeek(string key, int limit) => Enforce(limiter.Peek(key, limit, FailureWindow));

    private async Task AuditFailureAsync(string action, Guid? userId, Guid? deviceId, CancellationToken ct)
    {
        await db.InTransactionAsync(null, tx => audit.AppendAsync(tx, new AuditEntry(
            action, userId, userId is null ? null : "user", userId, deviceId, "FAILURE")), ct);
    }

    // Falha de envio não pode diferenciar e-mail novo de existente (anti-enumeração); registra só o tipo do erro.
    private async Task TrySendAsync(Func<Task> send)
    {
#pragma warning disable CA1031 // qualquer falha do provedor de e-mail é tratada igualmente
        try
        {
            await send();
        }
        catch (Exception ex)
        {
            LogMailFailure(logger, ex.GetType().Name);
        }
#pragma warning restore CA1031
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha ao enviar e-mail transacional ({ExceptionType})")]
    private static partial void LogMailFailure(ILogger logger, string exceptionType);

    private enum RegisterOutcome
    {
        None,
        SendCode,
        NotifyExisting,
    }

    private sealed record RefreshResult(TokenResponse? Response, string? ErrorCode)
    {
        public static RefreshResult Ok(TokenResponse response) => new(response, null);

        public static RefreshResult Fail(string code) => new(null, code);
    }
}
