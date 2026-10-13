using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Nina.Bff.Tests;

public sealed class TrackingProxyTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Last = request;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }

    private static WebApplicationFactory<Program> Create(RecordingHandler handler) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddHttpClient<ApiForwarder>().ConfigurePrimaryHttpMessageHandler(() => handler)));

    [Theory]
    [InlineData("POST", "/v1/sync/push")]
    [InlineData("GET", "/v1/sync/pull?baby_id=7d0a5c9e-1c0b-4d3a-b5f4-2f9d0e6a8b21&cursor=abc&limit=50")]
    [InlineData("GET", "/v1/babies/7d0a5c9e-1c0b-4d3a-b5f4-2f9d0e6a8b21/timeline?limit=10&types=SLEEP,DIAPER")]
    [InlineData("GET", "/v1/babies/7d0a5c9e-1c0b-4d3a-b5f4-2f9d0e6a8b21/events/0b8f1a52-6f3e-4a0c-8f86-6c3d7a3c1a10")]
    [InlineData("GET", "/v1/babies/7d0a5c9e-1c0b-4d3a-b5f4-2f9d0e6a8b21/events/0b8f1a52-6f3e-4a0c-8f86-6c3d7a3c1a10/history")]
    [InlineData("GET", "/v1/babies/7d0a5c9e-1c0b-4d3a-b5f4-2f9d0e6a8b21/aggregates?period=WEEK")]
    public async Task Tracking_routes_are_forwarded_with_path_query_body_and_authorization(string method, string pathAndQuery)
    {
        var handler = new RecordingHandler();
        using var factory = Create(handler);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), pathAndQuery);
        request.Headers.Add("Authorization", "Bearer abc");
        if (method == "POST")
        {
            request.Content = new StringContent("""{"device_id":"x","mutations":[]}""", Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(pathAndQuery, handler.Last!.RequestUri!.PathAndQuery);
        Assert.Equal(new HttpMethod(method), handler.Last.Method);
        Assert.Equal("Bearer abc", handler.Last.Headers.GetValues("Authorization").Single());
        Assert.Equal(method == "POST" ? """{"device_id":"x","mutations":[]}""" : null, handler.LastBody);
    }

    [Theory]
    [InlineData("PUT", "/v1/sync/push")]
    [InlineData("POST", "/v1/sync/pull")]
    [InlineData("POST", "/v1/babies/7d0a5c9e-1c0b-4d3a-b5f4-2f9d0e6a8b21/events")]
    [InlineData("DELETE", "/v1/babies/7d0a5c9e-1c0b-4d3a-b5f4-2f9d0e6a8b21/events/0b8f1a52-6f3e-4a0c-8f86-6c3d7a3c1a10")]
    [InlineData("GET", "/v1/babies/nao-e-uuid/timeline")]
    public async Task Routes_outside_the_contract_do_not_exist_in_the_bff(string method, string path)
    {
        var handler = new RecordingHandler();
        using var factory = Create(handler);
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), CancellationToken.None);
        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.Null(handler.Last);
    }

    [Fact]
    public async Task A_push_body_over_256_KiB_is_413_at_the_bff_without_reaching_the_api()
    {
        var handler = new RecordingHandler();
        using var factory = Create(handler);
        using var client = factory.CreateClient();
        using var content = new StringContent(new string('x', 300 * 1024), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri("/v1/sync/push", UriKind.Relative), content, CancellationToken.None);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Null(handler.Last);
    }
}
