using Microsoft.Extensions.Options;
using Nina.Identity.Contracts;
using Nina.Identity.Crypto;
using Nina.Identity.Persistence;
using Nina.SharedKernel.Audit;
using Nina.SharedKernel.Data;

namespace Nina.Identity.Services;

public static class UserMapper
{
    public static async Task<UserDto> ToDtoAsync(DbTx tx, UserRow user, string defaultLocale)
    {
        var identities = await IdentityStore.ListIdentitiesAsync(tx, user.Id);
        return new UserDto(
            user.Id,
            user.Email,
            user.EmailVerifiedAt is not null,
            null, // display_name: o schema 0001 não tem coluna em app_user (lacuna registrada no relatório BE-001).
            user.Locale ?? defaultLocale,
            user.Timezone,
            user.Status,
            user.PasswordHash is not null,
            [.. identities.Select(i => new UserIdentityDto(i.Provider, i.LinkedAt))],
            user.CreatedAt);
    }
}

/// <summary>Cria sessão (uma ativa por dispositivo), refresh token inicial e access token (RF-054, SEC-011).</summary>
public sealed class SessionIssuer(IOptions<IdentityOptions> options, TokenService tokens, TimeProvider time, AuditLog audit)
{
    public async Task<TokenResponse> IssueAsync(DbTx tx, UserRow user, Contracts.DeviceInfo device, string auditAction, string method)
    {
        var o = options.Value;
        await tx.SetUserAsync(user.Id);
        var now = time.GetUtcNow();
        var deviceId = device.DeviceId!.Value;
        var sessionId = Guid.NewGuid();
        var absolute = now.AddDays(o.SessionAbsoluteDays);

        await IdentityStore.RevokeDeviceSessionsAsync(tx, user.Id, deviceId, now);
        await IdentityStore.InsertSessionAsync(tx, sessionId, user.Id, deviceId, device.DeviceLabel, device.Platform!, device.AppVersion, now, absolute);

        var refresh = OpaqueTokens.NewRefreshToken(user.Id, sessionId);
        var refreshExpires = Min(now.AddDays(o.RefreshTokenDays), absolute);
        await IdentityStore.InsertRefreshTokenAsync(tx, sessionId, OpaqueTokens.HashRefreshToken(refresh), now, refreshExpires);

        var response = await BuildAsync(tx, user, sessionId, deviceId, refresh, refreshExpires, o.DefaultLocale);
        await audit.AppendAsync(tx, new AuditEntry(
            auditAction, user.Id, "session", sessionId, deviceId,
            Metadata: new Dictionary<string, object?> { ["method"] = method, ["platform"] = device.Platform }));
        return response;
    }

    public async Task<TokenResponse> BuildAsync(
        DbTx tx, UserRow user, Guid sessionId, Guid deviceId, string refresh, DateTimeOffset refreshExpires, string defaultLocale)
    {
        var (access, expiresIn) = tokens.IssueAccessToken(user.Id, sessionId, deviceId);
        var dto = await UserMapper.ToDtoAsync(tx, user, defaultLocale);
        var pending = await ConsentService.PendingAsync(tx, user.Id);
        return new TokenResponse("Bearer", access, expiresIn, refresh, refreshExpires, sessionId, dto, pending);
    }

    public static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
