using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Nina.SharedKernel.Http;
using Nina.SharedKernel.Security;
using Nina.Tracking.Domain;
using Nina.Tracking.Reads;
using Nina.Tracking.Sync;

namespace Nina.Tracking.Endpoints;

/// <summary>
/// Endpoints do módulo Tracking conforme <c>contracts/openapi.yaml</c> 1.0.1 (prefixo <c>/v1</c>; a API interna só é alcançada pelo BFF):
/// <c>POST /sync/push</c>, <c>GET /sync/pull</c>, <c>/babies/{id}/timeline</c>, <c>/events/{id}</c>, <c>/events/{id}/history</c> e <c>/aggregates</c>.
/// Autenticação por bearer; o papel é conferido no banco a cada requisição (nunca no token).
/// </summary>
public static class TrackingEndpoints
{
    private const int DefaultPullLimit = 200;
    private const int MaxPullLimit = 500;
    private const int DefaultPageLimit = 50;
    private const int MaxPageLimit = 100;

    public static IEndpointRouteBuilder MapTrackingEndpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/v1").WithTags("Tracking").RequireAuthorization();

        v1.MapPost("/sync/push", async (
            HttpContext http, ClaimsPrincipal user, PushService push, IRateLimiter limiter, IOptions<TrackingOptions> options, CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            var device = user.GetDeviceId() ?? throw ProblemException.Unauthorized("INVALID_TOKEN", "Invalid token");
            Throttle(limiter, $"tracking:push:user:{userId:N}", options.Value.PushPerUserPerMinute);
            Throttle(limiter, $"tracking:push:device:{device:N}", options.Value.PushPerDevicePerMinute);
            var body = await ReadBodyAsync(http.Request, PushRequestParser.MaxPayloadBytes, ct);
            using var parsed = PushRequestParser.Parse(body, device);
            return Results.Json(await push.PushAsync(userId, parsed, ct));
        }).Accepts<object>("application/json").WithName("pushMutations");

        v1.MapGet("/sync/pull", async (
            HttpContext http, ClaimsPrincipal user, PullService pull, IRateLimiter limiter, IOptions<TrackingOptions> options, CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            Throttle(limiter, $"tracking:pull:user:{userId:N}", options.Value.PullPerUserPerMinute);
            var query = http.Request.Query;
            var errors = new List<FieldError>();
            var babyId = RequiredGuid(query, "baby_id", errors);
            var cursor = query.TryGetValue("cursor", out var c) && c.Count > 0 ? c[0] : null;
            if (cursor is { Length: 0 or > CursorCodec.MaxLength })
            {
                errors.Add(new FieldError("cursor", "INVALID_VALUE"));
            }

            var limit = OptionalInt(query, "limit", DefaultPullLimit, 1, MaxPullLimit, errors);
            if (errors.Count > 0)
            {
                throw ProblemException.Validation(errors);
            }

            return Results.Json(await pull.PullAsync(userId, babyId, cursor, limit, ct));
        }).WithName("pullChanges");

        v1.MapGet("/babies/{babyId:guid}/timeline", async (
            Guid babyId, HttpContext http, ClaimsPrincipal user, ReadService reads, IRateLimiter limiter, IOptions<TrackingOptions> options, CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            Throttle(limiter, $"tracking:read:user:{userId:N}", options.Value.ReadPerUserPerMinute);
            var query = http.Request.Query;
            var errors = new List<FieldError>();
            var limit = OptionalInt(query, "limit", DefaultPageLimit, 1, MaxPageLimit, errors);
            var types = OptionalList(query, "types", ReadService.AllEventTypes, errors);
            var from = OptionalInstant(query, "from", errors);
            var to = OptionalInstant(query, "to", errors);
            var includeOpen = OptionalBool(query, "include_open", true, errors);
            var pageToken = query.TryGetValue("page_token", out var pt) && pt.Count > 0 ? pt[0] : null;
            if (errors.Count > 0)
            {
                throw ProblemException.Validation(errors);
            }

            return Results.Json(await reads.TimelineAsync(userId, babyId, new TimelineQuery(limit, pageToken, types, from, to, includeOpen), ct));
        }).WithName("getTimeline");

        v1.MapGet("/babies/{babyId:guid}/events/{eventId:guid}", async (
            Guid babyId, Guid eventId, ClaimsPrincipal user, ReadService reads, IRateLimiter limiter, IOptions<TrackingOptions> options, CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            Throttle(limiter, $"tracking:read:user:{userId:N}", options.Value.ReadPerUserPerMinute);
            return Results.Json(await reads.GetEventAsync(userId, babyId, eventId, ct));
        }).WithName("getEvent");

        v1.MapGet("/babies/{babyId:guid}/events/{eventId:guid}/history", async (
            Guid babyId, Guid eventId, HttpContext http, ClaimsPrincipal user, ReadService reads, IRateLimiter limiter, IOptions<TrackingOptions> options, CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            Throttle(limiter, $"tracking:read:user:{userId:N}", options.Value.ReadPerUserPerMinute);
            var errors = new List<FieldError>();
            var limit = OptionalInt(http.Request.Query, "limit", DefaultPageLimit, 1, MaxPageLimit, errors);
            if (errors.Count > 0)
            {
                throw ProblemException.Validation(errors);
            }

            var pageToken = http.Request.Query.TryGetValue("page_token", out var pt) && pt.Count > 0 ? pt[0] : null;
            return Results.Json(await reads.HistoryAsync(userId, babyId, eventId, limit, pageToken, ct));
        }).WithName("getEventHistory");

        v1.MapGet("/babies/{babyId:guid}/aggregates", async (
            Guid babyId, HttpContext http, ClaimsPrincipal user, AggregatesService aggregates, TimeProvider time, IRateLimiter limiter,
            IOptions<TrackingOptions> options, CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            Throttle(limiter, $"tracking:read:user:{userId:N}", options.Value.ReadPerUserPerMinute);
            var query = http.Request.Query;
            var errors = new List<FieldError>();
            var period = query.TryGetValue("period", out var p) && p.Count > 0 ? p[0] : null;
            if (period is not ("DAY" or "WEEK" or "MONTH"))
            {
                errors.Add(new FieldError("period", period is null ? "REQUIRED" : "INVALID_VALUE"));
            }

            DateOnly? anchor = null;
            if (query.TryGetValue("anchor_date", out var a) && a.Count > 0)
            {
                if (DateOnly.TryParseExact(a[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && parsed.Year is >= 2000 and <= 2100)
                {
                    anchor = parsed;
                }
                else
                {
                    errors.Add(new FieldError("anchor_date", "INVALID_VALUE"));
                }
            }

            var include = OptionalList(query, "include", ["SLEEP", "FEEDING", "PUMPING", "DIAPERS"], errors);
            if (errors.Count > 0)
            {
                throw ProblemException.Validation(errors);
            }

            var set = (include ?? ["SLEEP", "FEEDING", "PUMPING", "DIAPERS"]).ToHashSet(StringComparer.Ordinal);
            return Results.Json(await aggregates.GetAsync(userId, babyId, new AggregatesQuery(period!, anchor, set), time.GetUtcNow(), ct));
        }).WithName("getAggregates");

        return app;
    }

    private static void Throttle(IRateLimiter limiter, string key, int limit)
    {
        var decision = limiter.Consume(key, limit, TimeSpan.FromMinutes(1));
        if (!decision.Allowed)
        {
            throw ProblemException.RateLimited(Math.Max(1, decision.RetryAfterSeconds));
        }
    }

    /// <summary>Lê o corpo com teto de bytes: acima dele <c>413 PAYLOAD_TOO_LARGE</c> (o Kestrel também limita em 256 KiB).</summary>
    private static async Task<ReadOnlyMemory<byte>> ReadBodyAsync(HttpRequest request, int maxBytes, CancellationToken ct)
    {
        if (request.ContentLength > maxBytes)
        {
            throw TooLarge();
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw TooLarge();
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw TooLarge();
        }

        return buffer.ToArray();
    }

    private static ProblemException TooLarge() => new(StatusCodes.Status413PayloadTooLarge, "PAYLOAD_TOO_LARGE", "Payload too large");

    private static Guid RequiredGuid(IQueryCollection query, string name, List<FieldError> errors)
    {
        if (!query.TryGetValue(name, out var values) || values.Count == 0 || values[0] is not { Length: > 0 } text)
        {
            errors.Add(new FieldError(name, "REQUIRED"));
            return Guid.Empty;
        }

        if (Guid.TryParse(text, out var id) && id != Guid.Empty)
        {
            return id;
        }

        errors.Add(new FieldError(name, "INVALID_VALUE"));
        return Guid.Empty;
    }

    private static int OptionalInt(IQueryCollection query, string name, int fallback, int min, int max, List<FieldError> errors)
    {
        if (!query.TryGetValue(name, out var values) || values.Count == 0)
        {
            return fallback;
        }

        if (int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max)
        {
            return value;
        }

        errors.Add(new FieldError(name, "OUT_OF_RANGE"));
        return fallback;
    }

    private static bool OptionalBool(IQueryCollection query, string name, bool fallback, List<FieldError> errors)
    {
        if (!query.TryGetValue(name, out var values) || values.Count == 0)
        {
            return fallback;
        }

        if (bool.TryParse(values[0], out var value))
        {
            return value;
        }

        errors.Add(new FieldError(name, "INVALID_VALUE"));
        return fallback;
    }

    private static List<string>? OptionalList(IQueryCollection query, string name, IReadOnlyList<string> allowed, List<FieldError> errors)
    {
        if (!query.TryGetValue(name, out var values) || values.Count == 0)
        {
            return null;
        }

        var items = values.SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal).ToList();
        if (items.Count == 0 || items.Any(i => !allowed.Contains(i, StringComparer.Ordinal)))
        {
            errors.Add(new FieldError(name, "INVALID_VALUE"));
            return null;
        }

        return items;
    }

    private static DateTimeOffset? OptionalInstant(IQueryCollection query, string name, List<FieldError> errors)
    {
        if (!query.TryGetValue(name, out var values) || values.Count == 0)
        {
            return null;
        }

        if (DataValidator.TryReadInstant(System.Text.Json.JsonSerializer.SerializeToElement(values[0]), out var instant, out _))
        {
            return (DateTimeOffset)instant!;
        }

        errors.Add(new FieldError(name, "INVALID_VALUE"));
        return null;
    }
}
