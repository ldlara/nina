using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Nina.Bff.Tests;

public sealed class IdentityProxyTests
{
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

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    [Fact]
    public async Task Login_is_forwarded_with_body_contract_headers_and_the_resolved_client_ip()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """{"access_token":"a"}"""));
        using var factory = Create(handler);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/auth/login")
        {
            Content = new StringContent("""{"email":"a@example.org"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Authorization", "Bearer abc");
        request.Headers.Add("X-Reauth-Token", "reauth");
        request.Headers.Add("Idempotency-Key", "k-1");
        request.Headers.Add("Cookie", "session=secret");
        request.Headers.Add("X-Forwarded-For", "6.6.6.6");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"access_token":"a"}""", await response.Content.ReadAsStringAsync(CancellationToken.None));
        var sent = handler.Last!;
        Assert.Equal("/v1/auth/login", sent.RequestUri!.PathAndQuery);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("""{"email":"a@example.org"}""", handler.LastBody);
        Assert.Equal("Bearer abc", sent.Headers.GetValues("Authorization").Single());
        Assert.Equal("reauth", sent.Headers.GetValues("X-Reauth-Token").Single());
        Assert.Equal("k-1", sent.Headers.GetValues("Idempotency-Key").Single());
        Assert.False(sent.Headers.Contains("Cookie"));
        Assert.False(sent.Headers.TryGetValues("X-Forwarded-For", out var spoofed) && spoofed.Contains("6.6.6.6"));
    }

    private sealed class FixedIpFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.5");
                    return nextMiddleware(context);
                });
                next(app);
            };
    }

    [Fact]
    public async Task The_resolved_client_ip_is_sent_as_x_forwarded_for_replacing_what_the_client_claimed()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"));
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddHttpClient<ApiForwarder>().ConfigurePrimaryHttpMessageHandler(() => handler);
            s.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, FixedIpFilter>();
        }));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/me");
        request.Headers.Add("X-Forwarded-For", "6.6.6.6");

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["203.0.113.5"], handler.Last!.Headers.GetValues("X-Forwarded-For").ToArray());
    }

    [Fact]
    public async Task Upstream_status_problem_body_and_rate_limit_headers_are_preserved()
    {
        var handler = new RecordingHandler(_ =>
        {
            var r = Json(HttpStatusCode.TooManyRequests, """{"code":"RATE_LIMITED"}""", "application/problem+json");
            r.Headers.Add("Retry-After", "42");
            r.Headers.Add("X-Request-Id", "req-9");
            return r;
        });
        using var factory = Create(handler);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(new Uri("/v1/auth/login", UriKind.Relative), new StringContent("{}", Encoding.UTF8, "application/json"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(42, response.Headers.RetryAfter!.Delta!.Value.TotalSeconds);
        Assert.Equal("req-9", response.Headers.GetValues("X-Request-Id").Single());
        Assert.Contains("RATE_LIMITED", await response.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET", "/v1/me")]
    [InlineData("PATCH", "/v1/me")]
    [InlineData("GET", "/v1/me/sessions")]
    [InlineData("DELETE", "/v1/me/sessions/6f1d1c5e-4a3b-4f0e-9a52-0b5b3c8a7e11")]
    [InlineData("POST", "/v1/me/session-revocations")]
    [InlineData("PUT", "/v1/me/push-tokens/6f1d1c5e-4a3b-4f0e-9a52-0b5b3c8a7e11")]
    [InlineData("DELETE", "/v1/me/push-tokens/6f1d1c5e-4a3b-4f0e-9a52-0b5b3c8a7e11")]
    [InlineData("GET", "/v1/me/consents?include_history=true")]
    [InlineData("POST", "/v1/auth/google")]
    [InlineData("POST", "/v1/auth/logout")]
    [InlineData("GET", "/v1/legal/documents")]
    public async Task Every_identity_route_of_the_contract_reaches_the_api_with_the_same_method_and_path(string method, string path)
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

    [Theory]
    [InlineData("GET", "/v1/admin/users")]
    [InlineData("GET", "/internal/anything")]
    [InlineData("GET", "/v1/auth/login")]
    [InlineData("DELETE", "/v1/me/identities/GOOGLE")]
    public async Task Routes_outside_the_contract_surface_do_not_exist_on_the_bff(string method, string path)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var factory = Create(handler);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.Null(handler.Last);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Oversized_bodies_are_rejected_before_reaching_the_api()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var factory = Create(handler);
        using var client = factory.CreateClient();
        using var content = new StringContent(new string('x', 300 * 1024), Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(new Uri("/v1/auth/login", UriKind.Relative), content, CancellationToken.None);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Null(handler.Last);
    }

    [Fact]
    public async Task Unreachable_api_becomes_a_503_problem_without_internal_details()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("connection refused to 10.0.0.5:8080"));
        using var factory = Create(handler);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/v1/legal/documents", UriKind.Relative), CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("UPSTREAM_UNAVAILABLE", body, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.5", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Responses_carry_security_headers_and_no_server_banner()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"));
        using var factory = Create(handler);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/v1/legal/documents", UriKind.Relative), CancellationToken.None);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-store", response.Headers.GetValues("Cache-Control").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_and_ready_endpoints_still_work()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var factory = Create(handler);
        using var client = factory.CreateClient();

        using var health = await client.GetAsync(new Uri("/health", UriKind.Relative), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}
