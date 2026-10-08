using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Nina.Identity.External;
using Nina.Identity.Mail;
using Nina.SharedKernel.Http;

namespace Nina.Identity.Tests.Infrastructure;

public static class TestConstants
{
    public const string SigningKey = "dGVzdC1zaWduaW5nLWtleS1mb3ItbmluYS1pZGVudGl0eS10ZXN0cy0xMjM0NTY="; // base64, > 32 bytes
    public const string GoodPassword = "correct-horse-battery-staple";
}

/// <summary>Host in-process da API contra um PostgreSQL real, com relógio, e-mail e provedores externos fakes.</summary>
public sealed class ApiFactory(string appConnectionString, Action<Dictionary<string, string?>>? configure = null)
    : WebApplicationFactory<Program>
{
    public FakeTimeProvider Time { get; } = CreateTime();

    public InMemoryIdentityMailer Mailer => Services.GetRequiredService<InMemoryIdentityMailer>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = appConnectionString,
            ["Jwt:SigningKey"] = TestConstants.SigningKey,
            ["Identity:Argon2MemoryKiB"] = "4096", // acelera os testes; o padrão de produção é 19456
            ["Identity:Argon2Iterations"] = "2",
            ["Identity:GoogleClientIds:0"] = "test-google-client",
            ["Identity:AppleClientIds:0"] = "test-apple-client",
        };
        configure?.Invoke(settings);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            services.RemoveAll<IIdentityTokenVerifier>();
            services.AddSingleton<IIdentityTokenVerifier, FakeIdentityTokenVerifier>();
        });
    }

    private static FakeTimeProvider CreateTime()
    {
        var time = new FakeTimeProvider();
        time.SetUtcNow(DateTimeOffset.UtcNow);
        return time;
    }
}

/// <summary>Cliente HTTP de teste com atalhos para o contrato (JSON snake_case, <c>JsonNode</c> nas respostas).</summary>
public sealed class ApiClient(HttpClient http, ApiFactory factory)
{
    public ApiFactory Factory => factory;

    public static JsonObject Device(Guid? id = null, string platform = "IOS") => new()
    {
        ["device_id"] = (id ?? Guid.NewGuid()).ToString(),
        ["platform"] = platform,
        ["device_label"] = "Aparelho de teste",
        ["app_version"] = "1.0.0",
    };

    public static JsonArray Consents(string version = "1.0.0") =>
    [
        new JsonObject { ["purpose_key"] = "TERMS_OF_USE", ["document_version"] = version },
        new JsonObject { ["purpose_key"] = "PRIVACY_POLICY", ["document_version"] = version },
    ];

    public static string NewEmail() => $"user-{Guid.NewGuid():N}@example.org";

    public Task<ApiResponse> PostAsync(string path, JsonNode? body, string? bearer = null, Action<HttpRequestMessage>? tweak = null) =>
        SendAsync(HttpMethod.Post, path, body, bearer, tweak);

    public Task<ApiResponse> GetAsync(string path, string? bearer = null) => SendAsync(HttpMethod.Get, path, null, bearer);

    public async Task<ApiResponse> SendAsync(HttpMethod method, string path, JsonNode? body, string? bearer = null, Action<HttpRequestMessage>? tweak = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        tweak?.Invoke(request);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonNode? json = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            json = JsonNode.Parse(text);
        }

        return new ApiResponse(response.StatusCode, json, response.Headers, response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Cadastra e confirma o e-mail (código lido do e-mail fake); devolve a sessão.</summary>
    public async Task<Session> RegisterAndVerifyAsync(string? email = null, string password = TestConstants.GoodPassword, Guid? deviceId = null, string platform = "IOS")
    {
        email ??= NewEmail();
        var register = await PostAsync("/v1/auth/register", new JsonObject
        {
            ["email"] = email,
            ["password"] = password,
            ["display_name"] = "Teste",
            ["locale"] = "pt-BR",
            ["timezone"] = "America/Sao_Paulo",
            ["consents"] = Consents(),
        });
        Assert.Equal(System.Net.HttpStatusCode.Accepted, register.Status);
        var session = await VerifyAsync(email, LastCode(email), deviceId, platform);
        return session with { Email = email, Password = password };
    }

    public async Task<Session> VerifyAsync(string email, string code, Guid? deviceId = null, string platform = "IOS")
    {
        var device = Device(deviceId, platform);
        var verify = await PostAsync("/v1/auth/email/verify", new JsonObject { ["email"] = email, ["code"] = code, ["device"] = device });
        Assert.Equal(System.Net.HttpStatusCode.OK, verify.Status);
        return Session.From(verify.Json!, Guid.Parse(device["device_id"]!.GetValue<string>()), email);
    }

    public async Task<Session> LoginAsync(string email, string password = TestConstants.GoodPassword, Guid? deviceId = null, string platform = "IOS")
    {
        var device = Device(deviceId, platform);
        var login = await PostAsync("/v1/auth/login", new JsonObject { ["email"] = email, ["password"] = password, ["device"] = device });
        Assert.Equal(System.Net.HttpStatusCode.OK, login.Status);
        return Session.From(login.Json!, Guid.Parse(device["device_id"]!.GetValue<string>()), email) with { Password = password };
    }

    public string LastCode(string email) =>
        factory.Mailer.Sent.Last(m => m.To == email && m.Kind == MailKind.VerificationCode).Secret!;
}

public sealed record Session(string AccessToken, string RefreshToken, Guid SessionId, Guid UserId, Guid DeviceId, string Email, string Password = "")
{
    public static Session From(JsonNode json, Guid deviceId, string email) => new(
        json["access_token"]!.GetValue<string>(),
        json["refresh_token"]!.GetValue<string>(),
        Guid.Parse(json["session_id"]!.GetValue<string>()),
        Guid.Parse(json["user"]!["id"]!.GetValue<string>()),
        deviceId,
        email);
}

public sealed record ApiResponse(
    System.Net.HttpStatusCode Status,
    JsonNode? Json,
    System.Net.Http.Headers.HttpResponseHeaders Headers,
    string? ContentType)
{
    public string? Code => Json?["code"]?.GetValue<string>();

    public bool IsProblem => ContentType == ProblemWriter.ContentType;

    public string? FieldErrorCode(string field) =>
        Json?["errors"]?.AsArray().FirstOrDefault(e => e?["field"]?.GetValue<string>() == field)?["code"]?.GetValue<string>();
}

internal static class JsonExtensions
{
    public static JsonSerializerOptions Options { get; } = NinaJson.Options;
}
