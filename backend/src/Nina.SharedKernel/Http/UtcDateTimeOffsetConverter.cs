using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nina.SharedKernel.Http;

/// <summary>Instantes sempre em UTC com sufixo <c>Z</c> (RFC 3339), conforme o contrato (AD-13).</summary>
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture));
}
