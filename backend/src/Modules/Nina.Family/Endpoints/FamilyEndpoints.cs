using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Nina.Family.Contracts;
using Nina.Family.Services;
using Nina.SharedKernel.Http;
using Nina.SharedKernel.Security;

namespace Nina.Family.Endpoints;

/// <summary>
/// Endpoints do módulo Family conforme <c>contracts/openapi.yaml</c> v1.0.1 (prefixo <c>/v1</c>; a API interna só é alcançada pelo BFF):
/// bebês, cuidadores e convites. Autorização por bebê/papel no servidor (404 sem vínculo, 403 sem papel, 403 ACCESS_REVOKED).
/// </summary>
public static class FamilyEndpoints
{
    private const string ReauthHeader = "X-Reauth-Token";

    public static IEndpointRouteBuilder MapFamilyEndpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/v1").RequireAuthorization();
        MapBabies(v1.MapGroup("/babies").WithTags("Babies"));
        MapCaregivers(v1.MapGroup("/babies/{babyId:guid}").WithTags("Caregivers"));
        MapInvitations(v1.MapGroup("/invitations").WithTags("Caregivers"));
        return app;
    }

    private static void MapBabies(RouteGroupBuilder babies)
    {
        babies.MapGet(string.Empty, async (ClaimsPrincipal user, BabyService s, CancellationToken ct) =>
            TypedResults.Json(await s.ListAsync(user.RequireUserId(), ct)));

        babies.MapPost(string.Empty, async (
            [FromBody] BabyCreateRequest body, HttpContext http, ClaimsPrincipal user, BabyService s, IdempotencyStore idempotency, CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            return await idempotency.ExecuteAsync(
                http, userId, "baby.create", IdempotencyStore.Fingerprint("baby.create", body),
                async () => (StatusCodes.Status201Created, await s.CreateAsync(userId, user.GetDeviceId(), body, ct)));
        });

        babies.MapGet("/{babyId:guid}", async (Guid babyId, HttpContext http, ClaimsPrincipal user, BabyService s, CancellationToken ct) =>
        {
            var baby = await s.GetAsync(user.RequireUserId(), babyId, ct);
            http.Response.Headers.ETag = ETag(baby.Version);
            return TypedResults.Json(baby);
        });

        babies.MapPatch("/{babyId:guid}", async (Guid babyId, HttpContext http, ClaimsPrincipal user, BabyService s, CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            var patch = await ReadMergePatchAsync(http.Request, ct);
            var baby = await s.UpdateAsync(userId, user.GetDeviceId(), babyId, patch, ParseIfMatch(http.Request), ct);
            http.Response.Headers.ETag = ETag(baby.Version);
            return TypedResults.Json(baby);
        });

        babies.MapDelete("/{babyId:guid}", async (
            Guid babyId,
            [FromHeader(Name = ReauthHeader)] string? reauth,
            [FromQuery(Name = "acknowledge_other_caregivers")] bool? acknowledge,
            ClaimsPrincipal user,
            BabyService s,
            CancellationToken ct) =>
        {
            await s.DeleteAsync(user, babyId, reauth, acknowledge ?? false, ct);
            return Results.NoContent();
        });
    }

    private static void MapCaregivers(RouteGroupBuilder baby)
    {
        baby.MapGet("/caregivers", async (Guid babyId, ClaimsPrincipal user, CaregiverService s, CancellationToken ct) =>
            TypedResults.Json(await s.ListAsync(user.RequireUserId(), babyId, ct)));

        baby.MapPatch("/caregivers/{membershipId:guid}", async (
            Guid babyId, Guid membershipId, [FromBody] RoleChangeRequest body, ClaimsPrincipal user, CaregiverService s, CancellationToken ct) =>
            TypedResults.Json(await s.ChangeRoleAsync(user.RequireUserId(), babyId, membershipId, body, ct)));

        baby.MapDelete("/caregivers/{membershipId:guid}", async (
            Guid babyId, Guid membershipId, ClaimsPrincipal user, CaregiverService s, CancellationToken ct) =>
        {
            await s.RemoveAsync(user.RequireUserId(), babyId, membershipId, ct);
            return Results.NoContent();
        });

        baby.MapPost("/invitations", async (
            Guid babyId,
            [FromBody] InvitationCreateRequest body,
            HttpContext http,
            ClaimsPrincipal user,
            InvitationService s,
            IdempotencyStore idempotency,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            return await idempotency.ExecuteAsync(
                http, userId, "invitation.create", IdempotencyStore.Fingerprint("invitation.create", babyId, body),
                async () => (StatusCodes.Status201Created, await s.CreateAsync(userId, babyId, body, ct)));
        });

        baby.MapPost("/invitations/{membershipId:guid}/resend", async (
            Guid babyId, Guid membershipId, ClaimsPrincipal user, InvitationService s, CancellationToken ct) =>
            TypedResults.Json(await s.ResendAsync(user.RequireUserId(), babyId, membershipId, ct)));

        baby.MapPost("/ownership-transfer", async (
            Guid babyId,
            [FromBody] OwnershipTransferRequest body,
            [FromHeader(Name = ReauthHeader)] string? reauth,
            ClaimsPrincipal user,
            CaregiverService s,
            CancellationToken ct) =>
            TypedResults.Json(await s.TransferOwnershipAsync(user, babyId, body, reauth, ct)));
    }

    private static void MapInvitations(RouteGroupBuilder invitations)
    {
        invitations.MapPost("/inspect", async ([FromBody] InvitationTokenRequest body, ClaimsPrincipal user, InvitationService s, CancellationToken ct) =>
            TypedResults.Json(await s.InspectAsync(user.RequireUserId(), body, ct)));

        invitations.MapPost("/accept", async ([FromBody] InvitationTokenRequest body, ClaimsPrincipal user, InvitationService s, CancellationToken ct) =>
            TypedResults.Json(await s.AcceptAsync(user, body, ct)));

        invitations.MapPost("/decline", async ([FromBody] InvitationTokenRequest body, ClaimsPrincipal user, InvitationService s, CancellationToken ct) =>
        {
            await s.DeclineAsync(user.RequireUserId(), body, ct);
            return Results.NoContent();
        });
    }

    private static string ETag(long version) => $"\"{version}\"";

    /// <summary><c>If-Match</c> com a <c>version</c> (<c>"3"</c>); ausente ou <c>*</c> = sem pré-condição (PA-12: opcional no MVP).</summary>
    private static long? ParseIfMatch(HttpRequest request)
    {
        var raw = request.Headers.IfMatch.ToString().Trim();
        if (raw.Length == 0 || raw == "*")
        {
            return null;
        }

        if (raw.StartsWith("W/", StringComparison.Ordinal))
        {
            raw = raw[2..];
        }

        return long.TryParse(raw.Trim('"'), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var version)
            ? version
            : throw ProblemException.Validation(new FieldError("If-Match", "INVALID_FORMAT"));
    }

    /// <summary>Corpo do merge-patch: <c>application/merge-patch+json</c> (ou <c>application/json</c>), sempre um objeto.</summary>
    private static async Task<JsonObject> ReadMergePatchAsync(HttpRequest request, CancellationToken ct)
    {
        var mediaType = request.ContentType?.Split(';', 2)[0].Trim();
        if (!string.Equals(mediaType, "application/merge-patch+json", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProblemException(StatusCodes.Status415UnsupportedMediaType, "UNSUPPORTED_MEDIA_TYPE", "Unsupported media type");
        }

        try
        {
            return await JsonNode.ParseAsync(request.Body, cancellationToken: ct) as JsonObject
                   ?? throw ProblemException.Validation(new FieldError("body", "INVALID_BODY"));
        }
        catch (JsonException)
        {
            throw ProblemException.Validation(new FieldError("body", "INVALID_BODY"));
        }
    }
}
