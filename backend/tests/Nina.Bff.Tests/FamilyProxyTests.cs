using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Nina.Bff.Tests;

/// <summary>Superfície pública do módulo Family no BFF: só as rotas do contrato, encaminhadas sem regra de negócio.</summary>
public sealed class FamilyProxyTests
{
    private const string Baby = "7d0a5c9e-1c0b-4d3a-b5f4-2f9d0e6a8b21";
    private const string Member = "6f1d1c5e-4a3b-4f0e-9a52-0b5b3c8a7e11";

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Last = request;
            return respond(request);
        }
    }

    private static WebApplicationFactory<Program> Create(RecordingHandler handler) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddHttpClient<ApiForwarder>().ConfigurePrimaryHttpMessageHandler(() => handler)));

    [Theory]
    [InlineData("GET", "/v1/babies")]
    [InlineData("POST", "/v1/babies")]
    [InlineData("GET", "/v1/babies/" + Baby)]
    [InlineData("PATCH", "/v1/babies/" + Baby)]
    [InlineData("DELETE", "/v1/babies/" + Baby + "?acknowledge_other_caregivers=true")]
    [InlineData("GET", "/v1/babies/" + Baby + "/caregivers")]
    [InlineData("PATCH", "/v1/babies/" + Baby + "/caregivers/" + Member)]
    [InlineData("DELETE", "/v1/babies/" + Baby + "/caregivers/" + Member)]
    [InlineData("POST", "/v1/babies/" + Baby + "/invitations")]
    [InlineData("POST", "/v1/babies/" + Baby + "/invitations/" + Member + "/resend")]
    [InlineData("POST", "/v1/babies/" + Baby + "/ownership-transfer")]
    [InlineData("POST", "/v1/invitations/inspect")]
    [InlineData("POST", "/v1/invitations/accept")]
    [InlineData("POST", "/v1/invitations/decline")]
    public async Task Every_family_route_of_the_contract_reaches_the_api_with_the_same_method_and_path(string method, string path)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var factory = Create(handler);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(method, handler.Last!.Method.Method);
        Assert.Equal(path, handler.Last.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task Concurrency_idempotency_and_reauth_headers_and_the_merge_patch_body_are_forwarded_and_the_etag_comes_back()
    {
        var handler = new RecordingHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"x"}""", Encoding.UTF8, "application/json") };
            r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"2\"");
            r.Headers.Add("Idempotent-Replayed", "true");
            return r;
        });
        using var factory = Create(handler);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/v1/babies/" + Baby)
        {
            Content = new StringContent("""{"due_date":null}""", Encoding.UTF8, "application/merge-patch+json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "11111111-1111-1111-1111-111111111111");
        request.Headers.TryAddWithoutValidation("X-Reauth-Token", "reauth-token-value-0123456789");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer abc");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal("\"2\"", response.Headers.ETag!.Tag);
        Assert.Equal("true", response.Headers.GetValues("Idempotent-Replayed").Single());
        var sent = handler.Last!;
        Assert.Equal("\"1\"", sent.Headers.GetValues("If-Match").Single());
        Assert.Equal("11111111-1111-1111-1111-111111111111", sent.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal("reauth-token-value-0123456789", sent.Headers.GetValues("X-Reauth-Token").Single());
        Assert.Equal("Bearer abc", sent.Headers.GetValues("Authorization").Single());
        Assert.Equal("application/merge-patch+json", sent.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("""{"due_date":null}""", handler.LastBody);
    }

    [Fact]
    public async Task Upstream_problem_statuses_of_the_family_module_are_preserved()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"code":"ACCESS_REVOKED"}""", Encoding.UTF8, "application/problem+json"),
        });
        using var factory = Create(handler);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/v1/babies/" + Baby, UriKind.Relative), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("ACCESS_REVOKED", await response.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET", "/v1/babies/not-a-guid")]
    [InlineData("PUT", "/v1/babies/" + Baby)]
    [InlineData("POST", "/v1/babies/" + Baby + "/events")]
    [InlineData("DELETE", "/v1/babies/" + Baby + "/invitations")]
    [InlineData("GET", "/v1/babies/" + Baby + "/invitations/" + Member + "/resend")]
    [InlineData("GET", "/v1/invitations/accept")]
    [InlineData("GET", "/v1/babies/" + Baby + "/members")]
    public async Task Family_routes_outside_the_contract_do_not_exist_on_the_bff(string method, string path)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var factory = Create(handler);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.Null(handler.Last);
    }
}
