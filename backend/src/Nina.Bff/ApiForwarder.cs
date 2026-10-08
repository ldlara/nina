using System.Text.Json;

namespace Nina.Bff;

/// <summary>
/// Encaminha uma chamada do contrato público (BFF, único exposto) para a API interna sem regra de negócio:
/// repassa método, caminho, corpo e cabeçalhos do contrato; devolve status, corpo e cabeçalhos da resposta.
/// O IP do cliente (já resolvido pelo middleware de cabeçalhos encaminhados) vai em <c>X-Forwarded-For</c>.
/// </summary>
public sealed class ApiForwarder(HttpClient http)
{
    private const long MaxBodyBytes = 256 * 1024;

    private static readonly string[] RequestHeaders =
        ["Authorization", "Accept", "Accept-Language", "Idempotency-Key", "If-Match", "X-Reauth-Token", "User-Agent", "X-Request-Id"];

    private static readonly string[] ResponseHeaders =
        ["Retry-After", "WWW-Authenticate", "Idempotent-Replayed", "ETag", "X-Request-Id", "Deprecation", "Sunset"];

    public async Task ForwardAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.ContentLength > MaxBodyBytes)
        {
            await WriteProblemAsync(context, StatusCodes.Status413PayloadTooLarge, "PAYLOAD_TOO_LARGE", "Payload too large");
            return;
        }

        if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxBodyBytes;
        }

        using var upstream = new HttpRequestMessage(new HttpMethod(request.Method), new Uri(request.Path + request.QueryString, UriKind.Relative));
        foreach (var name in RequestHeaders)
        {
            if (request.Headers.TryGetValue(name, out var values))
            {
                upstream.Headers.TryAddWithoutValidation(name, values.ToArray());
            }
        }

        if (context.Connection.RemoteIpAddress is { } ip)
        {
            // O cabeçalho do cliente é descartado de propósito: a API confia no valor do BFF.
            upstream.Headers.TryAddWithoutValidation("X-Forwarded-For", ip.ToString());
        }

        if (request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
        {
            upstream.Content = new StreamContent(request.Body);
            if (request.ContentType is { } contentType)
            {
                upstream.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
        }

        try
        {
            using var response = await http.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var name in ResponseHeaders)
            {
                if (response.Headers.TryGetValues(name, out var values))
                {
                    context.Response.Headers[name] = values.ToArray();
                }
            }

            if (response.Content.Headers.ContentType is { } responseType)
            {
                context.Response.ContentType = responseType.ToString();
            }

            if (response.StatusCode is not (System.Net.HttpStatusCode.NoContent or System.Net.HttpStatusCode.NotModified))
            {
                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
        }
        catch (HttpRequestException) when (!context.Response.HasStarted)
        {
            await WriteProblemAsync(context, StatusCodes.Status503ServiceUnavailable, "UPSTREAM_UNAVAILABLE", "Service unavailable");
        }
        catch (TaskCanceledException) when (!context.RequestAborted.IsCancellationRequested && !context.Response.HasStarted)
        {
            await WriteProblemAsync(context, StatusCodes.Status504GatewayTimeout, "UPSTREAM_TIMEOUT", "Gateway timeout");
        }
    }

    public static async Task WriteProblemAsync(HttpContext context, int status, string code, string title)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        var body = new Dictionary<string, object?>
        {
            ["type"] = "https://api.nina.app/problems/" + code.ToLowerInvariant().Replace('_', '-'),
            ["title"] = title,
            ["status"] = status,
            ["code"] = code,
            ["request_id"] = context.Request.Headers["X-Request-Id"].ToString() is { Length: > 0 and <= 64 } id ? id : context.TraceIdentifier,
        };
        await JsonSerializer.SerializeAsync(context.Response.Body, body, cancellationToken: context.RequestAborted);
    }
}
