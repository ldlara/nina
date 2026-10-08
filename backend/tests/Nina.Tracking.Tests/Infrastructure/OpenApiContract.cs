using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Nina.Tracking.Tests.Infrastructure;

/// <summary>
/// Testes de contrato (SR-021): valida respostas reais contra os esquemas de <c>contracts/openapi.yaml</c> (somente leitura). Valida o
/// subconjunto de JSON Schema 2020-12 que o OpenAPI 3.1 do projeto usa: <c>$ref</c>, <c>allOf</c>, <c>oneOf</c> (com <c>discriminator</c>),
/// <c>type</c> (inclusive lista com <c>'null'</c>), <c>required</c>, <c>properties</c>, <c>additionalProperties: false</c>, <c>items</c>,
/// <c>enum</c>, <c>const</c>, <c>format</c> (uuid, date-time, date), <c>pattern</c> e limites numéricos/de tamanho.
/// </summary>
public static partial class OpenApiContract
{
    private static readonly Lazy<JsonObject> Document = new(Load);

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex DateTimeFormat();

    [GeneratedRegex(@"^-?\d+$")]
    private static partial Regex IntegerText();

    [GeneratedRegex(@"^-?\d+\.\d+$")]
    private static partial Regex DecimalText();

    public static string Version => Document.Value["info"]!["version"]!.GetValue<string>();

    /// <summary>Exige que <paramref name="instance"/> seja válida para <c>#/components/schemas/{schema}</c>.</summary>
    public static void AssertValid(string schema, JsonNode? instance)
    {
        var errors = new List<string>();
        Validate(instance, Ref(schema), "$", errors);
        Assert.True(errors.Count == 0, $"resposta fora do contrato {schema} (openapi {Version}):\n  " + string.Join("\n  ", errors.Take(12)));
    }

    public static IReadOnlyList<string> Errors(string schema, JsonNode? instance)
    {
        var errors = new List<string>();
        Validate(instance, Ref(schema), "$", errors);
        return errors;
    }

    private static JsonNode Ref(string schema) =>
        Document.Value["components"]!["schemas"]![schema] ?? throw new InvalidOperationException($"schema {schema} ausente no contrato");

    private static JsonObject Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "contracts", "openapi.yaml")))
        {
            dir = dir.Parent;
        }

        var path = Path.Combine(dir?.FullName ?? throw new FileNotFoundException("contracts/openapi.yaml"), "contracts", "openapi.yaml");
        var yaml = new YamlStream();
        using var reader = new StreamReader(path);
        yaml.Load(reader);
        return (JsonObject)Convert(yaml.Documents[0].RootNode)!;
    }

    private static JsonNode? Convert(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode map:
                var obj = new JsonObject();
                foreach (var (key, value) in map.Children)
                {
                    obj[((YamlScalarNode)key).Value ?? string.Empty] = Convert(value);
                }

                return obj;
            case YamlSequenceNode seq:
                var array = new JsonArray();
                foreach (var item in seq.Children)
                {
                    array.Add(Convert(item));
                }

                return array;
            case YamlScalarNode scalar:
                var text = scalar.Value ?? string.Empty;
                if (scalar.Style != YamlDotNet.Core.ScalarStyle.Plain)
                {
                    return JsonValue.Create(text);
                }

                return text switch
                {
                    "null" or "~" or "" => null,
                    "true" => JsonValue.Create(true),
                    "false" => JsonValue.Create(false),
                    _ when IntegerText().IsMatch(text) => JsonValue.Create(long.Parse(text, CultureInfo.InvariantCulture)),
                    _ when DecimalText().IsMatch(text) => JsonValue.Create(double.Parse(text, CultureInfo.InvariantCulture)),
                    _ => JsonValue.Create(text),
                };
            default:
                return null;
        }
    }

    // ------------------------------------------------------------------ validador

    private static JsonNode Resolve(JsonNode schema)
    {
        while (schema is JsonObject o && o["$ref"] is JsonValue r)
        {
            var parts = r.GetValue<string>().TrimStart('#', '/').Split('/');
            JsonNode? current = Document.Value;
            foreach (var part in parts)
            {
                current = current?[part];
            }

            schema = current ?? throw new InvalidOperationException("$ref não resolvido: " + r.GetValue<string>());
        }

        return schema;
    }

    private static void Validate(JsonNode? instance, JsonNode schemaNode, string path, List<string> errors)
    {
        var schema = Resolve(schemaNode);
        if (schema is not JsonObject s)
        {
            return;
        }

        if (s["allOf"] is JsonArray all)
        {
            foreach (var part in all)
            {
                Validate(instance, part!, path, errors);
            }
        }

        if (s["oneOf"] is JsonArray one)
        {
            ValidateOneOf(instance, s, one, path, errors);
        }

        if (s["type"] is { } type && !TypeMatches(instance, type))
        {
            errors.Add($"{path}: tipo {Kind(instance)} não é {type.ToJsonString()}");
            return;
        }

        if (s.ContainsKey("const") && !Same(instance, s["const"]))
        {
            errors.Add($"{path}: esperado const {s["const"]?.ToJsonString()}, veio {instance?.ToJsonString()}");
        }

        if (s["enum"] is JsonArray values && !values.Any(v => Same(instance, v)))
        {
            errors.Add($"{path}: {instance?.ToJsonString()} fora do enum {values.ToJsonString()}");
        }

        switch (instance)
        {
            case JsonValue value:
                ValidateScalar(value, s, path, errors);
                break;
            case JsonObject obj:
                ValidateObject(obj, s, path, errors);
                break;
            case JsonArray array:
                ValidateArray(array, s, path, errors);
                break;
            default:
                break;
        }
    }

    private static void ValidateOneOf(JsonNode? instance, JsonObject s, JsonArray options, string path, List<string> errors)
    {
        if (s["discriminator"] is JsonObject disc && instance is JsonObject obj)
        {
            var property = disc["propertyName"]!.GetValue<string>();
            var tag = obj[property]?.GetValue<string>();
            var target = tag is null ? null : disc["mapping"]?[tag]?.GetValue<string>();
            if (target is null)
            {
                errors.Add($"{path}: discriminador {property}={tag ?? "(ausente)"} sem mapeamento");
                return;
            }

            Validate(instance, new JsonObject { ["$ref"] = target }, path, errors);
            return;
        }

        var valid = 0;
        var detail = new List<string>();
        foreach (var option in options)
        {
            var local = new List<string>();
            Validate(instance, option!, path, local);
            if (local.Count == 0)
            {
                valid++;
            }
            else
            {
                detail.AddRange(local.Take(2));
            }
        }

        if (valid != 1)
        {
            errors.Add($"{path}: oneOf válido em {valid} alternativas (esperado 1). {string.Join(" | ", detail.Take(4))}");
        }
    }

    private static void ValidateObject(JsonObject obj, JsonObject s, string path, List<string> errors)
    {
        if (s["required"] is JsonArray required)
        {
            foreach (var name in required)
            {
                if (!obj.ContainsKey(name!.GetValue<string>()))
                {
                    errors.Add($"{path}: campo obrigatório ausente '{name.GetValue<string>()}'");
                }
            }
        }

        var known = new HashSet<string>(StringComparer.Ordinal);
        Collect(s, known);
        if (s["properties"] is JsonObject properties)
        {
            foreach (var (name, sub) in properties)
            {
                if (obj.TryGetPropertyValue(name, out var value))
                {
                    Validate(value, sub!, $"{path}.{name}", errors);
                }
            }
        }

        if (s["additionalProperties"] is JsonValue ap && ap.TryGetValue<bool>(out var allowed) && !allowed)
        {
            foreach (var (name, _) in obj)
            {
                if (!known.Contains(name))
                {
                    errors.Add($"{path}: campo não previsto '{name}'");
                }
            }
        }

        if (s["minProperties"] is JsonValue min && obj.Count < min.GetValue<long>())
        {
            errors.Add($"{path}: menos de {min.GetValue<long>()} propriedades");
        }
    }

    private static void Collect(JsonNode schema, HashSet<string> names)
    {
        if (Resolve(schema) is not JsonObject s)
        {
            return;
        }

        if (s["properties"] is JsonObject properties)
        {
            foreach (var (name, _) in properties)
            {
                names.Add(name);
            }
        }

        if (s["allOf"] is JsonArray all)
        {
            foreach (var part in all)
            {
                Collect(part!, names);
            }
        }
    }

    private static void ValidateArray(JsonArray array, JsonObject s, string path, List<string> errors)
    {
        if (s["minItems"] is JsonValue min && array.Count < min.GetValue<long>())
        {
            errors.Add($"{path}: menos de {min.GetValue<long>()} itens");
        }

        if (s["maxItems"] is JsonValue max && array.Count > max.GetValue<long>())
        {
            errors.Add($"{path}: mais de {max.GetValue<long>()} itens");
        }

        if (s["items"] is { } items)
        {
            for (var i = 0; i < array.Count; i++)
            {
                Validate(array[i], items, $"{path}[{i}]", errors);
            }
        }
    }

    private static void ValidateScalar(JsonValue value, JsonObject s, string path, List<string> errors)
    {
        if (value.TryGetValue<string>(out var text))
        {
            if (s["minLength"] is JsonValue min && text.Length < min.GetValue<long>())
            {
                errors.Add($"{path}: texto menor que {min.GetValue<long>()}");
            }

            if (s["maxLength"] is JsonValue max && text.Length > max.GetValue<long>())
            {
                errors.Add($"{path}: texto maior que {max.GetValue<long>()}");
            }

            if (s["pattern"] is JsonValue pattern && !Regex.IsMatch(text, pattern.GetValue<string>()))
            {
                errors.Add($"{path}: '{text}' não casa com {pattern.GetValue<string>()}");
            }

            switch (s["format"]?.GetValue<string>())
            {
                case "uuid" when !Guid.TryParseExact(text, "D", out _):
                    errors.Add($"{path}: '{text}' não é uuid");
                    break;
                case "date-time" when !DateTimeFormat().IsMatch(text):
                    errors.Add($"{path}: '{text}' não é date-time RFC 3339");
                    break;
                case "date" when !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _):
                    errors.Add($"{path}: '{text}' não é date");
                    break;
                default:
                    break;
            }

            return;
        }

        double number;
        if (value.TryGetValue<long>(out var asLong))
        {
            number = asLong;
        }
        else if (!value.TryGetValue<double>(out number))
        {
            return;
        }

        if (s["minimum"] is JsonValue minimum && number < AsDouble(minimum))
        {
            errors.Add($"{path}: {number} abaixo do mínimo {AsDouble(minimum)}");
        }

        if (s["maximum"] is JsonValue maximum && number > AsDouble(maximum))
        {
            errors.Add($"{path}: {number} acima do máximo {AsDouble(maximum)}");
        }
    }

    private static double AsDouble(JsonValue value) => value.TryGetValue<long>(out var l) ? l : value.GetValue<double>();

    private static bool TypeMatches(JsonNode? instance, JsonNode type) => type switch
    {
        JsonArray list => list.Any(t => TypeMatches(instance, t!)),
        JsonValue v => OneType(instance, v.GetValue<string>()),
        _ => true,
    };

    private static bool OneType(JsonNode? instance, string type) => type switch
    {
        "null" => instance is null,
        "string" => instance is JsonValue v && v.TryGetValue<string>(out _),
        "boolean" => instance is JsonValue b && b.TryGetValue<bool>(out _),
        "integer" => instance is JsonValue i && (i.TryGetValue<long>(out _) || (i.TryGetValue<double>(out var d) && d == Math.Floor(d))),
        "number" => instance is JsonValue n && (n.TryGetValue<double>(out _) || n.TryGetValue<long>(out _)),
        "object" => instance is JsonObject,
        "array" => instance is JsonArray,
        _ => true,
    };

    private static string Kind(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v when v.TryGetValue<string>(out _) => "string",
        JsonValue v when v.TryGetValue<bool>(out _) => "boolean",
        _ => "number",
    };

    private static bool Same(JsonNode? instance, JsonNode? expected)
    {
        if (instance is null || expected is null)
        {
            return instance is null && expected is null;
        }

        return string.Equals(Lexical(instance), Lexical(expected), StringComparison.Ordinal);
    }

    private static string Lexical(JsonNode node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node.ToJsonString();
}
