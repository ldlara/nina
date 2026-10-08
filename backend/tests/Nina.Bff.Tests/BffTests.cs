using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Nina.Bff.Tests;

public sealed class BffTests
{
    private sealed class StubApi(bool ready) : INinaApiClient
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(ready);
    }

    private static WebApplicationFactory<Program> Create(bool apiReady) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<INinaApiClient>(new StubApi(apiReady))));

    [Fact]
    public async Task Health_returns_200()
    {
        using var factory = Create(apiReady: false);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.ServiceUnavailable)]
    public async Task Ready_reflects_api_availability(bool apiReady, HttpStatusCode expected)
    {
        using var factory = Create(apiReady);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/ready", UriKind.Relative), CancellationToken.None);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ApiClient_returns_false_when_api_unreachable()
    {
        using var http = new HttpClient(new FailingHandler()) { BaseAddress = new Uri("http://api.invalid") };
        var client = new NinaApiClient(http);

        Assert.False(await client.IsReadyAsync(CancellationToken.None));
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("down");
    }
}
