using Nina.Bff;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<ApiOptions>()
    .Bind(builder.Configuration.GetSection(ApiOptions.SectionName));

builder.Services.AddHttpClient<INinaApiClient, NinaApiClient>((sp, client) =>
{
    var options = builder.Configuration.GetSection(ApiOptions.SectionName).Get<ApiOptions>() ?? new ApiOptions();
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
});

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

// Readiness do BFF depende da API de domínio.
app.MapGet("/ready", async (INinaApiClient api, CancellationToken ct) =>
    await api.IsReadyAsync(ct)
        ? Results.Ok(new { status = "ready" })
        : Results.Json(new { status = "api-unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable));

await app.RunAsync();

// Necessário para WebApplicationFactory nos testes.
public partial class Program;
