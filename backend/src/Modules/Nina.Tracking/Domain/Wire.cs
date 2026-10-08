using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nina.Tracking.Domain;

/// <summary>Formatação e leitura de valores do contrato (UTC com <c>Z</c>, snake_case, UUID canônico).</summary>
internal static class Wire
{
    public static string Instant(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'", CultureInfo.InvariantCulture);

    public static JsonNode? InstantOrNull(DateTimeOffset? value) => value is { } v ? JsonValue.Create(Instant(v)) : null;

    public static JsonNode? Id(Guid? value) => value is { } v ? JsonValue.Create(v.ToString("D")) : null;

    /// <summary>Lê um instante de uma coluna <c>timestamptz</c> serializada por <c>to_jsonb</c> (aceita qualquer deslocamento).</summary>
    public static DateTimeOffset? ReadInstant(JsonNode? node)
    {
        var text = node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        return text is null
            ? null
            : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
    }

    public static DateTimeOffset RequireInstant(JsonNode? node) => ReadInstant(node) ?? throw new InvalidOperationException("instante ausente");

    public static Guid? ReadGuid(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && Guid.TryParse(s, out var g) ? g : null;

    public static string? ReadString(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static long? ReadLong(JsonNode? node)
    {
        if (node is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue<long>(out var l))
        {
            return l;
        }

        return v.TryGetValue<decimal>(out var d) ? (long)Math.Round(d, MidpointRounding.AwayFromZero) : null;
    }

    /// <summary>Hora local <c>HH:MM:SS</c> do banco para <c>HH:MM</c> do contrato.</summary>
    public static string? ReadTimeOfDay(JsonNode? node) => ReadString(node) is { Length: >= 5 } s ? s[..5] : null;

    public static JsonNode? Text(string? value) => value is null ? null : JsonValue.Create(value);

    public static JsonNode? Number(long? value) => value is { } v ? JsonValue.Create(v) : null;

    public static JsonNode? DeepClone(JsonNode? node) => node?.DeepClone();

    /// <summary>Elemento JSON de um corpo já lido como <see cref="JsonElement"/> para uma árvore editável.</summary>
    public static JsonNode? ToNode(JsonElement element) => JsonNode.Parse(element.GetRawText());
}
