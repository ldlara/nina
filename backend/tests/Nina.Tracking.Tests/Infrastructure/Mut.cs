using System.Globalization;
using System.Text.Json.Nodes;

namespace Nina.Tracking.Tests.Infrastructure;

/// <summary>Construtores das mutações do contrato (<c>PushRequest</c>), com os mesmos nomes e enums em maiúsculas do OpenAPI.</summary>
public static class Mut
{
    public const string Sleep = "SLEEP_SESSION";
    public const string Feeding = "FEEDING_SESSION";
    public const string Pumping = "PUMPING_SESSION";
    public const string Diaper = "DIAPER_EVENT";
    public const string Wake = "WAKE_EVENT";
    public const string Tz = "America/Sao_Paulo";

    public static string At(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static JsonObject Push(Guid device, params JsonObject[] mutations) => new()
    {
        ["device_id"] = device.ToString(),
        ["mutations"] = new JsonArray(mutations.Select(m => (JsonNode)m).ToArray()),
    };

    public static string PullPath(Guid baby, string? cursor, int? limit)
    {
        var path = $"/v1/sync/pull?baby_id={baby}";
        if (cursor is not null)
        {
            path += "&cursor=" + Uri.EscapeDataString(cursor);
        }

        return limit is { } l ? path + $"&limit={l}" : path;
    }

    public static JsonObject Create(string type, Guid baby, Guid entity, JsonObject data, Guid? mutation = null, DateTimeOffset? clientAt = null) => new()
    {
        ["mutation_id"] = (mutation ?? Guid.NewGuid()).ToString(),
        ["op"] = "CREATE",
        ["entity_type"] = type,
        ["entity_id"] = entity.ToString(),
        ["baby_id"] = baby.ToString(),
        ["base_version"] = 0,
        ["client_created_at"] = At(clientAt ?? DateTimeOffset.UtcNow),
        ["data"] = data,
    };

    public static JsonObject Update(string type, Guid baby, Guid entity, long baseVersion, JsonObject data, Guid? mutation = null, DateTimeOffset? clientAt = null) => new()
    {
        ["mutation_id"] = (mutation ?? Guid.NewGuid()).ToString(),
        ["op"] = "UPDATE",
        ["entity_type"] = type,
        ["entity_id"] = entity.ToString(),
        ["baby_id"] = baby.ToString(),
        ["base_version"] = baseVersion,
        ["client_created_at"] = At(clientAt ?? DateTimeOffset.UtcNow),
        ["data"] = data,
    };

    public static JsonObject Delete(string type, Guid baby, Guid entity, long baseVersion, Guid? mutation = null, DateTimeOffset? clientAt = null) => new()
    {
        ["mutation_id"] = (mutation ?? Guid.NewGuid()).ToString(),
        ["op"] = "DELETE",
        ["entity_type"] = type,
        ["entity_id"] = entity.ToString(),
        ["baby_id"] = baby.ToString(),
        ["base_version"] = baseVersion,
        ["client_created_at"] = At(clientAt ?? DateTimeOffset.UtcNow),
    };

    // ---- dados de criação

    public static JsonObject SleepData(DateTimeOffset start, DateTimeOffset? end, string type = "NAP", string source = "MANUAL", string? notes = null) => new()
    {
        ["sleep_type"] = type,
        ["start_at"] = At(start),
        ["end_at"] = end is { } e ? At(e) : null,
        ["tz"] = Tz,
        ["notes"] = notes,
        ["source"] = source,
    };

    public static JsonObject BreastData(DateTimeOffset start, DateTimeOffset end, string side = "LEFT") => new()
    {
        ["feeding_type"] = "BREASTFEEDING",
        ["side"] = side,
        ["start_at"] = At(start),
        ["end_at"] = At(end),
        ["tz"] = Tz,
    };

    public static JsonObject BottleData(DateTimeOffset start, int volumeMl = 120, string? milk = "FORMULA") => new()
    {
        ["feeding_type"] = "BOTTLE",
        ["start_at"] = At(start),
        ["volume_ml"] = volumeMl,
        ["milk_type"] = milk,
        ["tz"] = Tz,
    };

    public static JsonObject PumpingData(DateTimeOffset start, DateTimeOffset end, int? volumeMl = 90) => new()
    {
        ["start_at"] = At(start),
        ["end_at"] = At(end),
        ["volume_ml"] = volumeMl,
        ["side"] = "BOTH",
        ["tz"] = Tz,
    };

    public static JsonObject DiaperData(DateTimeOffset at, string type = "WET", string? notes = null) => new()
    {
        ["occurred_at"] = At(at),
        ["diaper_type"] = type,
        ["tz"] = Tz,
        ["notes"] = notes,
    };

    public static JsonObject WakeData(Guid session, DateTimeOffset start, DateTimeOffset end, string? source = null)
    {
        var data = new JsonObject { ["sleep_session_id"] = session.ToString(), ["started_at"] = At(start), ["ended_at"] = At(end) };
        if (source is not null)
        {
            data["source"] = source;
        }

        return data;
    }
}
