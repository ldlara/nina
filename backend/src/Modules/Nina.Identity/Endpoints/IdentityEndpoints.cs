using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Nina.Identity.Contracts;
using Nina.Identity.External;
using Nina.Identity.Services;
using Nina.SharedKernel.Security;

namespace Nina.Identity.Endpoints;

/// <summary>Endpoints do módulo Identity conforme <c>contracts/openapi.yaml</c> v1.0.0 (prefixo <c>/v1</c>; a API interna é alcançada só pelo BFF).</summary>
public static class IdentityEndpoints
{
    private const string ReauthHeader = "X-Reauth-Token";

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/v1");
        MapAuth(v1.MapGroup("/auth").WithTags("Auth"));
        MapAccount(v1.MapGroup("/me").WithTags("Account").RequireAuthorization());
        MapPrivacy(v1);
        return app;
    }

    private static void MapAuth(RouteGroupBuilder auth)
    {
        auth.MapPost("/register", async ([FromBody] RegisterRequest body, AuthService s, CancellationToken ct) =>
            TypedResults.Json(await s.RegisterAsync(body, ct), statusCode: StatusCodes.Status202Accepted));

        auth.MapPost("/email/verify", async ([FromBody] EmailVerifyRequest body, AuthService s, CancellationToken ct) =>
            TypedResults.Json(await s.VerifyEmailAsync(body, ct)));

        auth.MapPost("/login", async ([FromBody] LoginRequest body, AuthService s, CancellationToken ct) =>
            TypedResults.Json(await s.LoginAsync(body, ct)));

        auth.MapPost("/google", async ([FromBody] SocialLoginRequest body, AuthService s, CancellationToken ct) =>
            TypedResults.Json(await s.LoginSocialAsync(Providers.Google, body, ct)));

        auth.MapPost("/apple", async ([FromBody] SocialLoginRequest body, AuthService s, CancellationToken ct) =>
            TypedResults.Json(await s.LoginSocialAsync(Providers.Apple, body, ct)));

        auth.MapPost("/refresh", async ([FromBody] RefreshRequest body, AuthService s, CancellationToken ct) =>
            TypedResults.Json(await s.RefreshAsync(body, ct)));

        auth.MapPost("/logout", async (ClaimsPrincipal user, AuthService s, CancellationToken ct) =>
            {
                await s.LogoutAsync(user.RequireUserId(), user.RequireSessionId(), ct);
                return Results.NoContent();
            })
            .RequireAuthorization()
            .WithMetadata(new AllowRevokedSessionAttribute());

        auth.MapPost("/password/forgot", async ([FromBody] ForgotPasswordRequest body, AuthService s, CancellationToken ct) =>
        {
            await s.ForgotPasswordAsync(body, ct);
            return Results.StatusCode(StatusCodes.Status202Accepted);
        });

        auth.MapPost("/password/reset", async ([FromBody] ResetPasswordRequest body, AuthService s, CancellationToken ct) =>
        {
            await s.ResetPasswordAsync(body, ct);
            return Results.NoContent();
        });

        auth.MapPost("/reauthenticate", async ([FromBody] ReauthRequest body, ClaimsPrincipal user, AuthService s, CancellationToken ct) =>
                TypedResults.Json(await s.ReauthenticateAsync(user.RequireUserId(), user.RequireSessionId(), body, ct)))
            .RequireAuthorization();
    }

    private static void MapAccount(RouteGroupBuilder me)
    {
        me.MapGet(string.Empty, async (ClaimsPrincipal user, AccountService s, CancellationToken ct) =>
            TypedResults.Json(await s.GetMeAsync(user.RequireUserId(), ct)));

        me.MapPatch(string.Empty, async ([FromBody] UserUpdateRequest body, ClaimsPrincipal user, AccountService s, CancellationToken ct) =>
            TypedResults.Json(await s.UpdateMeAsync(user.RequireUserId(), body, ct)));

        me.MapPut("/password", async (
            [FromBody] ChangePasswordRequest body,
            [FromHeader(Name = ReauthHeader)] string? reauth,
            ClaimsPrincipal user,
            AccountService s,
            CancellationToken ct) =>
        {
            var (userId, sessionId) = (user.RequireUserId(), user.RequireSessionId());
            await s.RequireReauthAsync(reauth, userId, sessionId);
            await s.ChangePasswordAsync(userId, sessionId, body, ct);
            return Results.NoContent();
        });

        me.MapPost("/identities", async (
            [FromBody] LinkIdentityRequest body,
            [FromHeader(Name = ReauthHeader)] string? reauth,
            ClaimsPrincipal user,
            AccountService s,
            CancellationToken ct) =>
        {
            var (userId, sessionId) = (user.RequireUserId(), user.RequireSessionId());
            await s.RequireReauthAsync(reauth, userId, sessionId);
            return TypedResults.Json(await s.LinkIdentityAsync(userId, body, ct));
        });

        me.MapGet("/sessions", async (ClaimsPrincipal user, AccountService s, CancellationToken ct) =>
            TypedResults.Json(await s.ListSessionsAsync(user.RequireUserId(), user.RequireSessionId(), ct)));

        me.MapDelete("/sessions/{sessionId:guid}", async (Guid sessionId, ClaimsPrincipal user, AccountService s, CancellationToken ct) =>
        {
            await s.RevokeSessionAsync(user.RequireUserId(), sessionId, ct);
            return Results.NoContent();
        });

        me.MapPost("/session-revocations", async (ClaimsPrincipal user, AccountService s, CancellationToken ct) =>
            TypedResults.Json(await s.RevokeOtherSessionsAsync(user.RequireUserId(), user.RequireSessionId(), ct)));

        me.MapPut("/push-tokens/{deviceId:guid}", async (
            Guid deviceId, [FromBody] PushTokenRequest body, ClaimsPrincipal user, AccountService s, CancellationToken ct) =>
            TypedResults.Json(await s.RegisterPushTokenAsync(user.RequireUserId(), deviceId, body, ct)));

        me.MapDelete("/push-tokens/{deviceId:guid}", async (Guid deviceId, ClaimsPrincipal user, AccountService s, CancellationToken ct) =>
        {
            await s.UnregisterPushTokenAsync(user.RequireUserId(), deviceId, ct);
            return Results.NoContent();
        });

        me.MapGet("/consents", async (
            [FromQuery(Name = "include_history")] bool? includeHistory, ClaimsPrincipal user, ConsentService s, CancellationToken ct) =>
            TypedResults.Json(await s.ListAsync(user.RequireUserId(), includeHistory ?? false, ct)));

        me.MapPost("/consents", async ([FromBody] ConsentInputRequest body, ClaimsPrincipal user, ConsentService s, CancellationToken ct) =>
            TypedResults.Json(await s.RecordAsync(user.RequireUserId(), body, ct), statusCode: StatusCodes.Status201Created));
    }

    private static void MapPrivacy(RouteGroupBuilder v1)
    {
        v1.MapGet("/legal/documents", async (ConsentService s, CancellationToken ct) =>
            TypedResults.Json(await s.ListLegalDocumentsAsync(ct))).WithTags("Privacy");
    }
}
