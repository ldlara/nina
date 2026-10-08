using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nina.SharedKernel.Http;
using Nina.SharedKernel.Security;

namespace Nina.Api.Tests;

/// <summary>NR-02: a API só confia no IP do cliente enviado pelo BFF com o segredo compartilhado; NR-17: validador de sessão obrigatório.</summary>
public sealed class InternalClientIpTests
{
    private const string Secret = "unit-test-internal-secret-0123456789-abcdef";

    private static async Task<IPAddress?> ResolveAsync(string? configuredSecret, Action<HttpRequest> arrange)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.7"); // o BFF
        arrange(context.Request);
        IPAddress? seen = null;
        var middleware = new InternalClientIpMiddleware(
            ctx =>
            {
                seen = ctx.Connection.RemoteIpAddress;
                return Task.CompletedTask;
            },
            Options.Create(new InternalOptions { SharedSecret = configuredSecret }));
        await middleware.InvokeAsync(context);
        Assert.False(context.Request.Headers.ContainsKey("X-Forwarded-For"));
        Assert.False(context.Request.Headers.ContainsKey(InternalHeaders.Secret));
        Assert.False(context.Request.Headers.ContainsKey(InternalHeaders.ClientIp));
        return seen;
    }

    [Fact]
    public async Task Correct_secret_replaces_the_peer_address_with_the_forwarded_client_ip()
    {
        var ip = await ResolveAsync(Secret, r =>
        {
            r.Headers[InternalHeaders.Secret] = Secret;
            r.Headers[InternalHeaders.ClientIp] = "203.0.113.9";
        });

        Assert.Equal(IPAddress.Parse("203.0.113.9"), ip);
    }

    [Fact]
    public async Task Ipv4_mapped_addresses_are_normalised()
    {
        var ip = await ResolveAsync(Secret, r =>
        {
            r.Headers[InternalHeaders.Secret] = Secret;
            r.Headers[InternalHeaders.ClientIp] = "::ffff:203.0.113.9";
        });

        Assert.Equal(IPAddress.Parse("203.0.113.9"), ip);
    }

    [Theory]
    [InlineData("wrong-secret")]
    [InlineData("")]
    [InlineData(Secret + "x")]
    public async Task Wrong_secret_keeps_the_peer_address(string provided)
    {
        var ip = await ResolveAsync(Secret, r =>
        {
            r.Headers[InternalHeaders.Secret] = provided;
            r.Headers[InternalHeaders.ClientIp] = "203.0.113.9";
            r.Headers["X-Forwarded-For"] = "6.6.6.6";
        });

        Assert.Equal(IPAddress.Parse("10.0.0.7"), ip);
    }

    [Fact]
    public async Task Missing_secret_and_plain_forwarded_for_are_ignored()
    {
        var ip = await ResolveAsync(Secret, r =>
        {
            r.Headers["X-Forwarded-For"] = "6.6.6.6";
            r.Headers[InternalHeaders.ClientIp] = "6.6.6.6";
        });

        Assert.Equal(IPAddress.Parse("10.0.0.7"), ip);
    }

    [Fact]
    public async Task Without_a_configured_secret_nothing_is_trusted_even_if_the_header_is_empty_too()
    {
        var ip = await ResolveAsync(null, r =>
        {
            r.Headers[InternalHeaders.Secret] = string.Empty;
            r.Headers[InternalHeaders.ClientIp] = "6.6.6.6";
        });

        Assert.Equal(IPAddress.Parse("10.0.0.7"), ip);
    }

    [Fact]
    public async Task Duplicate_or_invalid_client_ip_values_are_not_used()
    {
        var duplicated = await ResolveAsync(Secret, r =>
        {
            r.Headers[InternalHeaders.Secret] = Secret;
            r.Headers[InternalHeaders.ClientIp] = new Microsoft.Extensions.Primitives.StringValues(["1.1.1.1", "2.2.2.2"]);
        });
        var invalid = await ResolveAsync(Secret, r =>
        {
            r.Headers[InternalHeaders.Secret] = Secret;
            r.Headers[InternalHeaders.ClientIp] = "not-an-ip";
        });

        Assert.Equal(IPAddress.Parse("10.0.0.7"), duplicated);
        Assert.Equal(IPAddress.Parse("10.0.0.7"), invalid);
    }

    private static WebApplicationFactory<Program> Factory(string environment, string? secret, Action<IServiceCollection>? services = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SigningKeyPem"] = TestKeys.SigningKeyPem(),
                ["Jwt:KeyId"] = "k1",
                ["Security:MasterKey"] = "dGVzdC1tYXN0ZXIta2V5LWZvci1uaW5hLWlkZW50aXR5LXRlc3RzLTEyMzQ1Ng==",
                ["ConnectionStrings:Default"] = "Host=127.0.0.1;Database=x;Username=x;Password=x",
                ["Internal:SharedSecret"] = secret,
            }));
            if (services is not null)
            {
                b.ConfigureServices(services);
            }
        });

    [Fact]
    public void Outside_development_the_api_does_not_start_without_the_shared_secret()
    {
        using var factory = Factory("Production", secret: null);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Internal:SharedSecret", Flatten(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void Outside_development_a_short_secret_is_rejected()
    {
        using var factory = Factory("Production", secret: "too-short");

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Internal:SharedSecret", Flatten(ex), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outside_development_with_a_proper_secret_the_api_starts()
    {
        using var factory = Factory("Production", secret: Secret);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Authentication_without_a_session_validator_does_not_start_nr17()
    {
        using var factory = Factory("Development", secret: null, s => s.RemoveAll<ISessionValidator>());

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("ISessionValidator", Flatten(ex), StringComparison.Ordinal);
    }

    private static string Flatten(Exception ex)
    {
        var text = new System.Text.StringBuilder();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            text.AppendLine(e.Message);
        }

        return text.ToString();
    }
}

internal static class TestKeys
{
    public static string SigningKeyPem()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        return key.ExportPkcs8PrivateKeyPem();
    }
}
