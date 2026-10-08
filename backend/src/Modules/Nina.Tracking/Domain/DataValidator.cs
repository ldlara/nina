using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nina.SharedKernel.Http;

namespace Nina.Tracking.Domain;

/// <summary>Limites dependentes de parâmetro (<c>app_parameter</c>) usados na validação dos dados de uma mutação.</summary>
internal sealed record DataLimits(int BottleVolumeMlMax = 5000, int PumpingVolumeMlMax = 5000);

internal sealed class ParsedData
{
    /// <summary>Campos presentes no JSON (valor nulo = limpar). Tipos: <see cref="DateTimeOffset"/>, <see cref="string"/>, <see cref="int"/>, <see cref="Guid"/>.</summary>
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

    public List<FieldError> Errors { get; } = [];
}

/// <summary>
/// Validação estrita de <c>data</c> (criação) e do patch (edição) contra o contrato: campos desconhecidos, tipos, faixas, enums e
/// regras de forma entre campos (INV-01, INV-07). Erros por campo (<c>data.&lt;campo&gt;</c>) viram <c>REJECTED VALIDATION_FAILED</c>.
/// </summary>
internal static partial class DataValidator
{
    private const int MinYear = 2000;
    private const int MaxYear = 2100;

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,9})?(Z|z|[+-]\d{2}:\d{2})$")]
    private static partial Regex Rfc3339();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_+-]*(/[A-Za-z0-9_+-]+){0,2}$")]
    private static partial Regex IanaZone();

    public static ParsedData Parse(EntitySpec spec, JsonElement? data, bool create, DataLimits limits)
    {
        var result = new ParsedData();
        if (data is not { ValueKind: JsonValueKind.Object } obj)
        {
            result.Errors.Add(new FieldError("data", data is null || data.Value.ValueKind == JsonValueKind.Null ? "REQUIRED" : "INVALID_TYPE"));
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in obj.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                result.Errors.Add(new FieldError("data." + property.Name, "DUPLICATE_FIELD"));
                continue;
            }

            var field = spec.Field(property.Name);
            if (field is null)
            {
                result.Errors.Add(new FieldError("data." + property.Name, "UNKNOWN_FIELD"));
                continue;
            }

            if (!create && !spec.Patchable.Contains(property.Name))
            {
                result.Errors.Add(new FieldError("data." + property.Name, "NOT_PATCHABLE"));
                continue;
            }

            if (TryReadValue(spec, field, property.Value, limits, out var value, out var error))
            {
                result.Values[property.Name] = value;
            }
            else
            {
                result.Errors.Add(new FieldError("data." + property.Name, error!));
            }
        }

        if (create)
        {
            foreach (var required in spec.CreateRequired)
            {
                if ((!result.Values.TryGetValue(required, out var present) || present is null)
                    && !result.Errors.Any(e => e.Field == "data." + required))
                {
                    result.Errors.Add(new FieldError("data." + required, "REQUIRED"));
                }
            }
        }
        else if (result.Values.Count == 0 && result.Errors.Count == 0)
        {
            result.Errors.Add(new FieldError("data", "REQUIRED"));
        }

        return result;
    }

    /// <summary>Regras entre campos sobre o estado completo (criação, ou o estado atual com o patch aplicado).</summary>
    public static void ValidateShape(EntitySpec spec, IReadOnlyDictionary<string, object?> v, List<FieldError> errors)
    {
        if (spec.StartField is { } startField && spec.EndField is { } endField)
        {
            var start = v.GetValueOrDefault(startField) as DateTimeOffset?;
            var end = v.GetValueOrDefault(endField) as DateTimeOffset?;
            if (spec.EndRequired && end is null && !errors.Any(e => e.Field == "data." + endField))
            {
                errors.Add(new FieldError("data." + endField, "REQUIRED"));
            }

            if (start is { } s && end is { } e && e < s && !errors.Any(x => x.Field == "data." + endField))
            {
                errors.Add(new FieldError("data." + endField, "END_BEFORE_START"));
            }
        }

        if (spec.Type == EntityCatalog.FeedingSession)
        {
            ValidateFeedingShape(v, errors);
        }
    }

    private static void ValidateFeedingShape(IReadOnlyDictionary<string, object?> v, List<FieldError> errors)
    {
        var type = v.GetValueOrDefault("feeding_type") as string;
        var side = v.GetValueOrDefault("side");
        var volume = v.GetValueOrDefault("volume_ml");
        var milk = v.GetValueOrDefault("milk_type");
        var end = v.GetValueOrDefault("end_at");

        void NotApplicable(string field, object? value)
        {
            if (value is not null)
            {
                errors.Add(new FieldError("data." + field, "NOT_APPLICABLE"));
            }
        }

        void Required(string field, object? value)
        {
            if (value is null)
            {
                errors.Add(new FieldError("data." + field, "REQUIRED"));
            }
        }

        switch (type)
        {
            case "BREASTFEEDING":
                Required("side", side);
                Required("end_at", end);                 // PA-05 / ADR-0010: end_at obrigatório na mamada
                NotApplicable("volume_ml", volume);
                NotApplicable("milk_type", milk);
                break;
            case "BOTTLE":
                Required("volume_ml", volume);           // contrato: BottleFeedingData exige volume_ml
                NotApplicable("side", side);
                break;
            case "SOLID" or "OTHER":
                NotApplicable("side", side);
                NotApplicable("volume_ml", volume);
                NotApplicable("milk_type", milk);
                break;
            default:
                break;
        }
    }

    private static bool TryReadValue(EntitySpec spec, FieldSpec field, JsonElement element, DataLimits limits, out object? value, out string? error)
    {
        value = null;
        error = null;
        if (element.ValueKind == JsonValueKind.Null)
        {
            if (field.Nullable)
            {
                return true;
            }

            error = "NULL_NOT_ALLOWED";
            return false;
        }

        switch (field.Kind)
        {
            case FieldKind.Instant:
                return TryReadInstant(element, out value, out error);
            case FieldKind.Text:
                if (element.ValueKind != JsonValueKind.String)
                {
                    error = "INVALID_TYPE";
                    return false;
                }

                var text = element.GetString()!;
                if (text.EnumerateRunes().Count() > field.MaxLength)
                {
                    error = "TOO_LONG";
                    return false;
                }

                if (text.Contains('\0', StringComparison.Ordinal))
                {
                    error = "INVALID_VALUE";
                    return false;
                }

                value = text;
                return true;
            case FieldKind.TimeZone:
                if (element.ValueKind != JsonValueKind.String)
                {
                    error = "INVALID_TYPE";
                    return false;
                }

                var zone = element.GetString()!;
                if (zone.Length > 64 || !IanaZone().IsMatch(zone))
                {
                    error = "INVALID_VALUE";
                    return false;
                }

                value = zone;
                return true;
            case FieldKind.Enum:
                if (element.ValueKind != JsonValueKind.String)
                {
                    error = "INVALID_TYPE";
                    return false;
                }

                var option = element.GetString()!;
                if (field.Values is null || !field.Values.Contains(option, StringComparer.Ordinal))
                {
                    error = "INVALID_VALUE";
                    return false;
                }

                value = option;
                return true;
            case FieldKind.Int:
                if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var number))
                {
                    error = "INVALID_TYPE";
                    return false;
                }

                var max = field.Name == "volume_ml"
                    ? Math.Min(field.Max, spec.Type == EntityCatalog.PumpingSession ? limits.PumpingVolumeMlMax : limits.BottleVolumeMlMax)
                    : field.Max;
                if (number < field.Min || number > max)
                {
                    error = "OUT_OF_RANGE";
                    return false;
                }

                value = number;
                return true;
            case FieldKind.Guid:
                if (element.ValueKind != JsonValueKind.String || !Guid.TryParse(element.GetString(), out var id))
                {
                    error = "INVALID_TYPE";
                    return false;
                }

                value = id;
                return true;
            default:
                error = "INVALID_TYPE";
                return false;
        }
    }

    public static bool TryReadInstant(JsonElement element, out object? value, out string? error)
    {
        value = null;
        error = null;
        if (element.ValueKind != JsonValueKind.String)
        {
            error = "INVALID_TYPE";
            return false;
        }

        var text = element.GetString()!;
        if (!Rfc3339().IsMatch(text)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            error = "INVALID_VALUE";
            return false;
        }

        parsed = parsed.ToUniversalTime();
        if (parsed.Year is < MinYear or > MaxYear)
        {
            error = "OUT_OF_RANGE";
            return false;
        }

        value = parsed;
        return true;
    }
}
