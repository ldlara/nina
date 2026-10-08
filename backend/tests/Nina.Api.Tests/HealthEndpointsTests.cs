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
    public async Task Ready_lists_all_seven_modules()
    {
        using var client = factory.CreateClient();
        var json = await client.GetStringAsync(new Uri("/ready", UriKind.Relative), CancellationToken.None);
        using var doc = JsonDocument.Parse(json);

        var modules = doc.RootElement.GetProperty("modules").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(7, modules.Count);
        Assert.Contains("SleepIntelligence", modules);
    }
}
