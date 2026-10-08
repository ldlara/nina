using Nina.Bff;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<ApiOptions>()
    .Bind(builder.Configuration.GetSection(ApiOptions.SectionName));

builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
    (builder.Configuration.GetSection(ForwardedHeadersSettings.SectionName).Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings()).Apply(o));

// NR-02: segredo compartilhado com a API (autentica o IP do cliente repassado). Obrigatório fora de Development.
builder.Services
    .AddOptions<InternalOptions>()
    .Bind(builder.Configuration.GetSection(InternalOptions.SectionName))
    .Validate(
        o => builder.Environment.IsDevelopment() && string.IsNullOrEmpty(o.SharedSecret) || o.SharedSecret is { Length: >= InternalOptions.MinSecretLength },
        $"Internal:SharedSecret é obrigatório (mínimo {InternalOptions.MinSecretLength} caracteres) fora de Development.")
    .ValidateOnStart();

builder.Services.AddHttpClient<INinaApiClient, NinaApiClient>((sp, client) =>
{
    var options = builder.Configuration.GetSection(ApiOptions.SectionName).Get<ApiOptions>() ?? new ApiOptions();
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
});

// Encaminhador dos endpoints públicos de Identity para a API interna.
builder.Services.AddHttpClient<ApiForwarder>((sp, client) =>
{
    var options = builder.Configuration.GetSection(ApiOptions.SectionName).Get<ApiOptions>() ?? new ApiOptions();
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
});

// Corpo máximo de 256 KiB (SEC-042) e sem cabeçalho Server.
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = 256 * 1024;
    o.AddServerHeader = false;
});

var app = builder.Build();

app.UseForwardedHeaders();

// Cabeçalhos de segurança (SEC-065); HSTS só quando a requisição chegou por HTTPS (o roteador envia X-Forwarded-Proto).
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Cache-Control"] = "no-store";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Frame-Options"] = "DENY";
        headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        if (context.Request.IsHttps)
        {
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        }

        return Task.CompletedTask;
    });
    await next();
});

// Rotas fora do contrato: 404/405 em RFC 7807 (o corpo das respostas encaminhadas é sempre preservado).
app.Use(async (context, next) =>
{
    await next();
    if (!context.Response.HasStarted && context.Response.StatusCode is 404 or 405 && context.Response.ContentLength is null && context.Response.ContentType is null)
    {
        await ApiForwarder.WriteProblemAsync(
            context, context.Response.StatusCode, context.Response.StatusCode == 404 ? "NOT_FOUND" : "METHOD_NOT_ALLOWED", "Not found");
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

// Readiness do BFF depende da API de domínio.
app.MapGet("/ready", async (INinaApiClient api, CancellationToken ct) =>
    await api.IsReadyAsync(ct)
        ? Results.Ok(new { status = "ready" })
        : Results.Json(new { status = "api-unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapIdentityProxy();
app.MapFamilyProxy();
app.MapTrackingProxy();

await app.RunAsync();

// Necessário para WebApplicationFactory nos testes.
public partial class Program;
