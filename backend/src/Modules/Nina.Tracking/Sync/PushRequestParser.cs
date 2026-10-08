using System.Text.Json;
using Nina.SharedKernel.Http;
using Nina.Tracking.Domain;

namespace Nina.Tracking.Sync;

/// <summary>
/// Valida o envelope do <c>POST /sync/push</c> (a requisição inteira falha com <c>400/413</c>) sem interpretar os dados de cada
/// mutação: <c>data</c> é validado por mutação e uma falha ali vira <c>REJECTED</c> daquele item, nunca do lote.
/// </summary>
internal static class PushRequestParser
{
    public const int MaxMutations = 100;
    public const int MaxPayloadBytes = 256 * 1024;
    private const int MaxReportedErrors = 20;

    public static ParsedPush Parse(ReadOnlyMemory<byte> body, Guid sessionDeviceId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            throw ProblemException.Validation(new FieldError("body", "INVALID_JSON"));
        }

        try
        {
            return Build(document, sessionDeviceId);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static ParsedPush Build(JsonDocument document, Guid sessionDeviceId)
    {
        var root = document.RootElement;
        var errors = new List<FieldError>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw ProblemException.Validation(new FieldError("body", "INVALID_TYPE"));
        }

        Guid deviceId = Guid.Empty;
        if (!root.TryGetProperty("device_id", out var device) || device.ValueKind == JsonValueKind.Null)
        {
            errors.Add(new FieldError("device_id", "REQUIRED"));
        }
        else if (device.ValueKind != JsonValueKind.String || !Guid.TryParse(device.GetString(), out deviceId))
        {
            errors.Add(new FieldError("device_id", "INVALID_VALUE"));
        }
        else if (deviceId != sessionDeviceId)
        {
            errors.Add(new FieldError("device_id", "DEVICE_MISMATCH"));      // deve ser o dispositivo da sessão
        }

        if (!root.TryGetProperty("mutations", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new FieldError("mutations", array.ValueKind == JsonValueKind.Undefined ? "REQUIRED" : "INVALID_TYPE"));
            throw ProblemException.Validation(errors);
        }

        var count = array.GetArrayLength();
        if (count > MaxMutations)
        {
            throw new ProblemException(StatusCodes.Status413PayloadTooLarge, "PAYLOAD_TOO_LARGE", "Payload too large");
        }

        if (count == 0)
        {
            errors.Add(new FieldError("mutations", "REQUIRED"));
        }

        var mutations = new List<PushMutation>(count);
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var mutation = ParseMutation(item, index, errors);
            if (mutation is not null)
            {
                mutations.Add(mutation);
            }

            index++;
            if (errors.Count >= MaxReportedErrors)
            {
                break;
            }
        }

        if (errors.Count > 0)
        {
            throw ProblemException.Validation(errors.Take(MaxReportedErrors));
        }

        return new ParsedPush(document, deviceId, mutations);
    }

    private static PushMutation? ParseMutation(JsonElement item, int index, List<FieldError> errors)
    {
        var path = $"mutations[{index}]";
        if (item.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new FieldError(path, "INVALID_TYPE"));
            return null;
        }

        var before = errors.Count;
        var mutationId = ReadGuid(item, "mutation_id", path, errors);
        var entityId = ReadGuid(item, "entity_id", path, errors);
        var babyId = ReadGuid(item, "baby_id", path, errors);
        var op = ReadEnum(item, "op", path, ["CREATE", "UPDATE", "DELETE"], errors);
        var entityType = ReadEnum(item, "entity_type", path, [.. EntityCatalog.All.Select(s => s.Type)], errors);

        long baseVersion = 0;
        if (!item.TryGetProperty("base_version", out var bv) || bv.ValueKind == JsonValueKind.Null)
        {
            errors.Add(new FieldError(path + ".base_version", "REQUIRED"));
        }
        else if (bv.ValueKind != JsonValueKind.Number || !bv.TryGetInt64(out baseVersion) || baseVersion < 0)
        {
            errors.Add(new FieldError(path + ".base_version", "INVALID_VALUE"));
        }

        DateTimeOffset clientCreatedAt = default;
        if (!item.TryGetProperty("client_created_at", out var cc) || cc.ValueKind == JsonValueKind.Null)
        {
            errors.Add(new FieldError(path + ".client_created_at", "REQUIRED"));
        }
        else if (DataValidator.TryReadInstant(cc, out var instant, out var code))
        {
            clientCreatedAt = (DateTimeOffset)instant!;
        }
        else
        {
            errors.Add(new FieldError(path + ".client_created_at", code!));
        }

        JsonElement? data = item.TryGetProperty("data", out var d) ? d : null;
        if (errors.Count > before)
        {
            return null;
        }

        return new PushMutation(index, mutationId, op!, entityType!, entityId, babyId, baseVersion, clientCreatedAt, data);
    }

    private static Guid ReadGuid(JsonElement item, string name, string path, List<FieldError> errors)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            errors.Add(new FieldError($"{path}.{name}", "REQUIRED"));
            return Guid.Empty;
        }

        if (value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) && id != Guid.Empty)
        {
            return id;
        }

        errors.Add(new FieldError($"{path}.{name}", "INVALID_VALUE"));
        return Guid.Empty;
    }

    private static string? ReadEnum(JsonElement item, string name, string path, IReadOnlyList<string> allowed, List<FieldError> errors)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            errors.Add(new FieldError($"{path}.{name}", "REQUIRED"));
            return null;
        }

        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text && allowed.Contains(text, StringComparer.Ordinal))
        {
            return text;
        }

        errors.Add(new FieldError($"{path}.{name}", "INVALID_VALUE"));
        return null;
    }
}
