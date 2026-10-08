using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Nina.Family.Mail;
using Nina.Identity.Mail;
using Nina.SharedKernel.Http;

namespace Nina.Family.Tests.Infrastructure;

public static class TestConstants
{
    public const string MasterKey = "dGVzdC1tYXN0ZXIta2V5LWZvci1uaW5hLWlkZW50aXR5LXRlc3RzLTEyMzQ1Ng=="; // base64, > 32 bytes
    public const string InternalSecret = "test-internal-shared-secret-0123456789-abcdef";
    public const string GoodPassword = "correct-horse-battery-staple";

    public static string NewSigningKeyPem()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        return key.ExportPkcs8PrivateKeyPem();
    }
}

/// <summary>Host in-process da API contra um PostgreSQL real, com relógio e e-mails fakes (nenhum envio ou rede real).</summary>
public sealed class ApiFactory(string appConnectionString, Action<Dictionary<string, string?>>? configure = null)
    : WebApplicationFactory<Program>
{
    public FakeTimeProvider Time { get; } = CreateTime();

    public InMemoryIdentityMailer IdentityMailer => Services.GetRequiredService<InMemoryIdentityMailer>();

    public InMemoryFamilyMailer FamilyMailer => Services.GetRequiredService<InMemoryFamilyMailer>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = appConnectionString,
            ["Jwt:SigningKeyPem"] = TestConstants.NewSigningKeyPem(),
            ["Jwt:KeyId"] = "test-key-1",
            ["Security:MasterKey"] = TestConstants.MasterKey,
            ["Internal:SharedSecret"] = TestConstants.InternalSecret,
            ["Identity:Argon2MemoryKiB"] = "4096", // acelera os testes; o padrão de produção é 19456
            ["Identity:Argon2Iterations"] = "2",
        };
        configure?.Invoke(settings);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            // E-mails e demais trabalhos em segundo plano executam no ato, para as asserções verem o envio de imediato.
            services.RemoveAll<IBackgroundWork>();
            services.AddSingleton<IBackgroundWork, InlineBackgroundWork>();
        });
    }

    private static FakeTimeProvider CreateTime()
    {
        var time = new FakeTimeProvider();
        time.SetUtcNow(DateTimeOffset.UtcNow);
        return time;
    }
}

public sealed class InlineBackgroundWork : IBackgroundWork
{
    public void Enqueue(Func<CancellationToken, Task> work) => work(CancellationToken.None).GetAwaiter().GetResult();
}

/// <summary>Cliente HTTP de teste (JSON snake_case, <c>JsonNode</c> nas respostas).</summary>
public sealed class ApiClient(HttpClient http, ApiFactory factory)
{
    public ApiFactory Factory => factory;

    public static string NewEmail() => $"user-{Guid.NewGuid():N}@example.org";

    public Task<ApiResponse> GetAsync(string path, string? bearer = null) => SendAsync(HttpMethod.Get, path, null, bearer);

    public Task<ApiResponse> PostAsync(string path, JsonNode? body, string? bearer = null, Action<HttpRequestMessage>? tweak = null) =>
        SendAsync(HttpMethod.Post, path, body, bearer, tweak);

    public Task<ApiResponse> DeleteAsync(string path, string? bearer = null, Action<HttpRequestMessage>? tweak = null) =>
        SendAsync(HttpMethod.Delete, path, null, bearer, tweak);

    public Task<ApiResponse> PatchAsync(string path, JsonNode? body, string? bearer = null, Action<HttpRequestMessage>? tweak = null) =>
        SendAsync(HttpMethod.Patch, path, body, bearer, tweak, "application/merge-patch+json");

    public async Task<ApiResponse> SendAsync(
        HttpMethod method, string path, JsonNode? body, string? bearer = null, Action<HttpRequestMessage>? tweak = null, string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, contentType);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        tweak?.Invoke(request);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonNode? json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        return new ApiResponse(response.StatusCode, json, response.Headers, response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Envia um corpo bruto (para testar tipos de conteúdo e JSON inválido).</summary>
    public async Task<ApiResponse> SendRawAsync(HttpMethod method, string path, string body, string contentType, string? bearer)
    {
        using var request = new HttpRequestMessage(method, path) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        if (bearer is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        }

        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonNode? json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        return new ApiResponse(response.StatusCode, json, response.Headers, response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Cadastra pela API real, confirma o e-mail (código lido do e-mail fake) e devolve a sessão.</summary>
    public async Task<Session> RegisterAndVerifyAsync(string? email = null, string password = TestConstants.GoodPassword, string name = "Teste")
    {
        email ??= NewEmail();
        var register = await PostAsync("/v1/auth/register", new JsonObject
        {
            ["email"] = email,
            ["password"] = password,
            ["display_name"] = name,
            ["locale"] = "pt-BR",
            ["timezone"] = "America/Sao_Paulo",
            ["consents"] = new JsonArray(
                new JsonObject { ["purpose_key"] = "TERMS_OF_USE", ["document_version"] = "1.0.0" },
                new JsonObject { ["purpose_key"] = "PRIVACY_POLICY", ["document_version"] = "1.0.0" }),
        });
        Assert.Equal(System.Net.HttpStatusCode.Accepted, register.Status);
        var code = factory.IdentityMailer.Sent.Last(m => m.To == email && m.Kind == MailKind.VerificationCode).Secret!;
        var deviceId = Guid.NewGuid();
        var verify = await PostAsync("/v1/auth/email/verify", new JsonObject
        {
            ["email"] = email,
            ["code"] = code,
            ["device"] = new JsonObject
            {
                ["device_id"] = deviceId.ToString(),
                ["platform"] = "IOS",
                ["device_label"] = "Aparelho de teste",
                ["app_version"] = "1.0.0",
            },
        });
        Assert.Equal(System.Net.HttpStatusCode.OK, verify.Status);
        var json = verify.Json!;
        return new Session(
            json["access_token"]!.GetValue<string>(), Guid.Parse(json["session_id"]!.GetValue<string>()),
            Guid.Parse(json["user"]!["id"]!.GetValue<string>()), deviceId, email, password);
    }

    /// <summary>X-Reauth-Token (uso único) por senha, com o escopo pedido.</summary>
    public async Task<string> ReauthAsync(Session s, string scope)
    {
        var response = await PostAsync("/v1/auth/reauthenticate", new JsonObject
        {
            ["password"] = s.Password,
            ["scope"] = new JsonArray(JsonValue.Create(scope)),
        }, s.AccessToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.Status);
        return response.Json!["reauth_token"]!.GetValue<string>();
    }

    public static Action<HttpRequestMessage> Header(string name, string value) => r => r.Headers.TryAddWithoutValidation(name, value);

    public static Action<HttpRequestMessage> Headers(params (string Name, string Value)[] headers) =>
        r =>
        {
            foreach (var (name, value) in headers)
            {
                r.Headers.TryAddWithoutValidation(name, value);
            }
        };
}

public sealed record Session(string AccessToken, Guid SessionId, Guid UserId, Guid DeviceId, string Email, string Password);

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

    public string? Header(string name) => Headers.TryGetValues(name, out var values) ? values.First() : null;
}
