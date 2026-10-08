using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Nina.Api.Tests;

public sealed class HealthEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_returns_200_healthy()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_does_not_disclose_modules_or_dependencies()
    {
        using var client = factory.CreateClient();
        var json = await client.GetStringAsync(new Uri("/ready", UriKind.Relative), CancellationToken.None);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("ready", doc.RootElement.GetProperty("status").GetString());
        Assert.False(doc.RootElement.TryGetProperty("modules", out _));
        Assert.DoesNotContain("Identity", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Module_registration_still_lists_all_seven_modules_internally()
    {
        Assert.Equal(7, ModuleRegistration.ModuleNames.Count);
        Assert.Contains("SleepIntelligence", ModuleRegistration.ModuleNames);
    }

    [Fact]
    public async Task Responses_carry_security_headers_and_a_request_id()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), CancellationToken.None);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-store", response.Headers.GetValues("Cache-Control").Single());
        Assert.True(response.Headers.Contains("X-Request-Id"));
    }
}
