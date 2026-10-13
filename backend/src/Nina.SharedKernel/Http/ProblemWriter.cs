using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Nina.SharedKernel.Http;

/// <summary>Escreve <c>application/problem+json</c> (RFC 7807) com <c>code</c> estável e <c>request_id</c>.</summary>
public static class ProblemWriter
{
    public const string ContentType = "application/problem+json";

    public static string TypeFor(string code) =>
        "https://api.nina.app/problems/" + code.ToLowerInvariant().Replace('_', '-');

    public static async Task WriteAsync(HttpContext context, ProblemException problem)
    {
        var body = new Dictionary<string, object?>
        {
            ["type"] = TypeFor(problem.Code),
            ["title"] = problem.Title,
            ["status"] = problem.Status,
            ["code"] = problem.Code,
            ["request_id"] = context.Items[HttpRequestContext.RequestIdItemKey] as string,
        };

        if (problem.Errors.Count > 0)
        {
            body["errors"] = problem.Errors.Select(e =>
            {
                var item = new Dictionary<string, object?> { ["field"] = e.Field, ["code"] = e.Code };
                if (e.Meta is not null)
                {
                    item["meta"] = e.Meta;
                }

                return item;
            }).ToList();
        }

        if (problem.RetryAfterSeconds is { } retry)
        {
            body["retry_after_seconds"] = retry;
            context.Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        foreach (var (key, value) in problem.Extensions)
        {
            body[key] = value;
        }

        context.Response.StatusCode = problem.Status;
        context.Response.ContentType = ContentType;
        var options = context.RequestServices.GetService<IOptions<JsonOptions>>()?.Value.SerializerOptions
                      ?? NinaJson.Options;
        await JsonSerializer.SerializeAsync(context.Response.Body, body, options, context.RequestAborted);
    }
}

/// <summary>Opções JSON do contrato (snake_case).</summary>
public static class NinaJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new UtcDateTimeOffsetConverter() },
    };
}
