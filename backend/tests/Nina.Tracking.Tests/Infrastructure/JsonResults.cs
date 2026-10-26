using System.Text.Json.Nodes;

namespace Nina.Tracking.Tests.Infrastructure;

/// <summary>Atalhos de leitura dos resultados por mutação (<c>MutationResult</c>) e das mudanças do pull.</summary>
public static class JsonResults
{
    public static string Status(this JsonNode? result) => result!["status"]!.GetValue<string>();

    public static string? ProblemCode(this JsonNode? result) => result!["problem"]?["code"]?.GetValue<string>();

    public static string? Resolution(this JsonNode? result) => result!["resolution"]?.GetValue<string>();

    public static long Version(this JsonNode? result) => result!["version"]!.GetValue<long>();

    public static IReadOnlyList<string> WarningCodes(this JsonNode? result) =>
        result!["warnings"]?.AsArray().Select(w => w!["code"]!.GetValue<string>()).ToList() ?? [];

    public static IReadOnlyList<string> ConflictFields(this JsonNode? result) =>
        result!["conflicts"]?.AsArray().Select(c => $"{c!["field"]!.GetValue<string>()}:{c["kept"]!.GetValue<string>()}").ToList() ?? [];

    public static JsonNode? Entity(this JsonNode? result) => result!["entity"];

    public static string EntityType(this JsonNode? change) => change!["entity_type"]!.GetValue<string>();

    public static string Op(this JsonNode? change) => change!["op"]!.GetValue<string>();

    public static Guid EntityId(this JsonNode? change) => Guid.Parse(change!["entity_id"]!.GetValue<string>());
}
