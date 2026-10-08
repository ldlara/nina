using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nina.SharedKernel.Http;
using Nina.SharedKernel.Security;

namespace Nina.Bff.Tests;

/// <summary>
/// NR-02 ponta a ponta: cliente -> BFF real (Kestrel, socket real, sem injetar IP) -> API com o middleware e o limitador reais
/// do SharedKernel. O peer do BFF é 127.0.0.1 e faz o papel do roteador; o IP do cliente só pode vir do X-Forwarded-For.
/// </summary>
public sealed class ClientIpEndToEndTests : IAsyncDisposable
{
    private const string Secret = "e2e-internal-shared-secret-0123456789-abcdef";
    private const int LoginLimit = 3;

    private readonly WebApplication _api = BuildApi();

    private static WebApplication BuildApi()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Internal:SharedSecret"] = Secret;
        builder.Services.AddOptions<Nina.SharedKernel.Http.InternalOptions>().Bind(builder.Configuration.GetSection("Internal"));
        builder.Services.AddSingleton<IRateLimiter>(new InMemoryRateLimiter(TimeProvider.System));
        var app = builder.Build();
        app.UseMiddleware<InternalClientIpMiddleware>();

        // Réplica mínima do limite de AuthService: login:ip:{ip}, 3 tentativas por janela (o real é 30 falhas/15 min).
        app.MapPost("/v1/auth/login", (HttpContext context, IRateLimiter limiter) =>
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var decision = limiter.Consume($"login:ip:{ip}", LoginLimit, TimeSpan.FromMinutes(15));
            return Results.Text(ip, "text/plain", Encoding.UTF8, decision.Allowed ? StatusCodes.Status200OK : StatusCodes.Status429TooManyRequests);
        });
        app.Start();
        return app;
    }

    private WebApplicationFactory<Program> CreateBff(Action<Dictionary<string, string?>> configure, string environment = "Development")
    {
        var settings = new Dictionary<string, string?>
        {
            ["Internal:SharedSecret"] = Secret,
            ["ForwardedHeaders:KnownProxies:0"] = "127.0.0.1", // o "roteador": o socket real do teste
        };
        configure(settings);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            b.ConfigureServices(s => s.AddHttpClient<ApiForwarder>()
                .ConfigurePrimaryHttpMessageHandler(() => _api.GetTestServer().CreateHandler()));
        });
        factory.UseKestrel(0);
        return factory;
    }

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string? forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/login")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<string> SeenIpAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(CancellationToken.None);

    [Fact]
    public async Task Rate_limits_are_per_client_not_global_when_the_router_is_trusted()
    {
        using var bff = CreateBff(_ => { });
        using var client = bff.CreateClient();

        // O cliente A (200.0.0.1) esgota o limite de login...
        for (var i = 0; i < LoginLimit; i++)
        {
            using var ok = await LoginAsync(client, "200.0.0.1");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal("200.0.0.1", await SeenIpAsync(ok));
        }

        using var blocked = await LoginAsync(client, "200.0.0.1");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

        // ...e o cliente B, que chega pelo mesmo roteador, NÃO é afetado (antes do NR-02 todos viravam 127.0.0.1).
        using var other = await LoginAsync(client, "200.0.0.2");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal("200.0.0.2", await SeenIpAsync(other));
    }

    [Fact]
    public async Task Entries_injected_by_the_client_before_the_router_are_discarded()
    {
        using var bff = CreateBff(_ => { });
        using var client = bff.CreateClient();

        // O roteador anexa o IP real (203.0.113.5) à direita do que o cliente forjou (ForwardLimit = 1).
        using var response = await LoginAsync(client, "6.6.6.6, 203.0.113.5");

        Assert.Equal("203.0.113.5", await SeenIpAsync(response));
    }

    [Fact]
    public async Task Forwarded_for_from_an_untrusted_peer_is_dropped_and_cannot_evade_the_limit()
    {
        using var bff = CreateBff(s => s["ForwardedHeaders:KnownProxies:0"] = "192.0.2.1"); // o peer real (127.0.0.1) NÃO é confiável
        using var client = bff.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < LoginLimit + 1; i++)
        {
            using var r = await LoginAsync(client, $"198.51.100.{i + 1}"); // cada tentativa jura ser outro cliente
            statuses.Add(r.StatusCode);
            Assert.Equal("127.0.0.1", await SeenIpAsync(r));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    [Fact]
    public async Task Known_networks_in_cidr_notation_are_honoured()
    {
        using var bff = CreateBff(s =>
        {
            s.Remove("ForwardedHeaders:KnownProxies:0");
            s["ForwardedHeaders:KnownNetworks:0"] = "127.0.0.0/8";
        });
        using var client = bff.CreateClient();

        using var response = await LoginAsync(client, "203.0.113.77");

        Assert.Equal("203.0.113.77", await SeenIpAsync(response));
    }

    [Fact]
    public async Task Header_symmetry_is_enforced_when_required()
    {
        using var bff = CreateBff(s => s["ForwardedHeaders:RequireHeaderSymmetry"] = "true");
        using var client = bff.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/login") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Forwarded-For", "203.0.113.5");
        request.Headers.Add("X-Forwarded-Proto", "https, http"); // assimétrico

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal("127.0.0.1", await SeenIpAsync(response)); // cabeçalhos ignorados
    }

    [Fact]
    public async Task A_secret_mismatch_makes_the_api_ignore_the_forwarded_ip()
    {
        using var bff = CreateBff(s => s["Internal:SharedSecret"] = "another-secret-0123456789-abcdefghijklmnop");
        using var client = bff.CreateClient();

        using var response = await LoginAsync(client, "203.0.113.5");

        Assert.Equal("unknown", await SeenIpAsync(response)); // nem o IP do BFF nem o do cliente é aceito
    }

    [Fact]
    public async Task Without_a_secret_in_development_the_bff_sends_no_ip_headers_at_all()
    {
        string? leaked = null;
        var handler = new CapturingHandler(r => leaked = string.Join(",", r.Headers.Select(h => h.Key)));
        using var bff = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureServices(s => s.AddHttpClient<ApiForwarder>().ConfigurePrimaryHttpMessageHandler(() => handler));
        });
        using var client = bff.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/legal/documents");
        request.Headers.Add("X-Forwarded-For", "6.6.6.6");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("X-Forwarded-For", leaked, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Nina", leaked, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Outside_development_the_bff_does_not_start_without_the_shared_secret()
    {
        using var bff = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Production"));

        var ex = Assert.ThrowsAny<Exception>(() => bff.CreateClient());

        Assert.Contains("Internal:SharedSecret", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Internal_header_names_match_the_shared_kernel_contract()
    {
        Assert.Equal(InternalHeaders.ClientIp, ApiForwarder.InternalClientIpHeader);
        Assert.Equal(InternalHeaders.Secret, ApiForwarder.InternalSecretHeader);
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private sealed class CapturingHandler(Action<HttpRequestMessage> capture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
