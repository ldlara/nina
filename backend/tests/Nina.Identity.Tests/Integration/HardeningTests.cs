using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nina.Identity.Mail;
using Nina.Identity.Tests.Infrastructure;
using Nina.SharedKernel.Http;
using Npgsql;

namespace Nina.Identity.Tests.Integration;

/// <summary>SEC-041 (bloqueio progressivo, aviso de novo dispositivo), SR-008 (contexto no pool), SR-019/020 (cabeçalhos, erros).</summary>
public sealed class HardeningTests(PostgresFixture postgres) : IntegrationTestBase(postgres)
{
    private Task<ApiResponse> Login(string email, string password, Guid? deviceId = null, string ip = "203.0.113.10") =>
        Api.PostAsync(
            "/v1/auth/login",
            new JsonObject { ["email"] = email, ["password"] = password, ["device"] = ApiClient.Device(deviceId) },
            tweak: ApiClient.FromIp(ip));

    [Fact]
    public async Task Lockouts_grow_progressively_and_the_retry_after_follows()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var retryAfters = new List<int>();
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < 5; i++)
            {
                await Login(s.Email, "wrong-password-123");
            }

            var locked = await Login(s.Email, "wrong-password-123");
            Assert.Equal(HttpStatusCode.TooManyRequests, locked.Status);
            retryAfters.Add((int)locked.Headers.RetryAfter!.Delta!.Value.TotalSeconds);
            Factory.Time.Advance(TimeSpan.FromSeconds(retryAfters[^1] + 1));
        }

        Assert.Equal([900, 1800, 3600], retryAfters);
    }

    [Fact]
    public async Task Retry_after_inside_one_lockout_only_decreases()
    {
        var s = await Api.RegisterAndVerifyAsync();
        for (var i = 0; i < 6; i++)
        {
            await Login(s.Email, "wrong-password-123");
        }

        Factory.Time.Advance(TimeSpan.FromMinutes(5));
        var again = await Login(s.Email, TestConstants.GoodPassword);

        Assert.Equal(HttpStatusCode.TooManyRequests, again.Status);
        Assert.InRange((int)again.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 590, 600);
    }

    [Fact]
    public async Task A_successful_login_clears_the_progressive_penalty()
    {
        var s = await Api.RegisterAndVerifyAsync();
        for (var i = 0; i < 4; i++)
        {
            await Login(s.Email, "wrong-password-123");
        }

        Assert.Equal(HttpStatusCode.OK, (await Login(s.Email, TestConstants.GoodPassword)).Status);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(s.Email, "wrong-password-123")).Status);
        }
    }

    [Fact]
    public async Task Login_from_a_new_device_sends_a_security_notice_but_a_known_device_does_not()
    {
        var s = await Api.RegisterAndVerifyAsync();
        Assert.DoesNotContain(Factory.Mailer.Sent, m => m.Notice == SecurityNotice.NewDeviceLogin);

        await Api.LoginAsync(s.Email, s.Password, s.DeviceId);
        Assert.DoesNotContain(Factory.Mailer.Sent, m => m.Notice == SecurityNotice.NewDeviceLogin);

        await Api.LoginAsync(s.Email, s.Password, Guid.NewGuid());
        Assert.Single(Factory.Mailer.Sent, m => m.To == s.Email && m.Notice == SecurityNotice.NewDeviceLogin);
    }

    [Theory]
    [InlineData("/v1/auth/login")]
    [InlineData("/v1/auth/refresh")]
    [InlineData("/v1/auth/password/forgot")]
    [InlineData("/v1/auth/password/reset")]
    [InlineData("/v1/auth/google")]
    public async Task Credential_objects_reject_unknown_properties(string path)
    {
        var response = await Api.PostAsync(path, new JsonObject { ["email"] = "a@example.org", ["surprise"] = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal("VALIDATION_FAILED", response.Code);
    }

    [Fact]
    public async Task Oversized_bodies_are_refused_before_processing()
    {
        var big = new JsonObject { ["email"] = "a@example.org", ["password"] = new string('x', 300 * 1024), ["device"] = ApiClient.Device() };
        var response = await Api.PostAsync("/v1/auth/login", big);

        Assert.True(response.Status is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var response = await Api.GetAsync("/v1/legal/documents");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-store", response.Headers.GetValues("Cache-Control").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task User_context_is_transaction_scoped_and_never_left_on_pooled_connections()
    {
        var s = await Api.RegisterAndVerifyAsync();
        for (var i = 0; i < 5; i++)
        {
            await Api.GetAsync("/v1/me/sessions", s.AccessToken);
        }

        // Usa a mesma fonte de dados (pool) da API: nenhuma conexão devolvida pode carregar nina.user_id.
        var dataSource = Factory.Services.GetRequiredService<NpgsqlDataSource>();
        for (var i = 0; i < 10; i++)
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand("SELECT coalesce(current_setting('nina.user_id', true), '')", connection);
            Assert.Equal(string.Empty, (string)(await cmd.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public void Application_code_never_issues_a_session_level_SET_of_nina_settings()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "Nina.SharedKernel")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var offenders = Directory.GetFiles(Path.Combine(root.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"\bSET\s+(SESSION\s+)?nina\.|set_config\(\s*'nina\.[a-z_]+'\s*,[^)]*,\s*false\s*\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public async Task Database_errors_are_mapped_by_sqlstate_without_leaking_the_database_message()
    {
        using var host = await new HostBuilder().ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(s => s.AddLogging())
            .Configure(app =>
            {
                app.UseMiddleware<ProblemDetailsMiddleware>();
                app.Run(context =>
                {
                    var state = context.Request.Query["state"].ToString();
                    throw new PostgresException(
                        "ERROR: duplicate key value violates unique constraint \"x\" Key (id)=(11111111-2222-3333-4444-555555555555)",
                        "ERROR", "ERROR", state);
                });
            })).StartAsync();
        using var client = host.GetTestClient();

        foreach (var (state, status, code) in new[]
        {
            ("23505", HttpStatusCode.Conflict, "CONFLICT"),
            ("42501", HttpStatusCode.Forbidden, "FORBIDDEN"),
            ("NN012", HttpStatusCode.Conflict, "BUSINESS_RULE_VIOLATION"),
            ("XX000", HttpStatusCode.InternalServerError, "INTERNAL_ERROR"),
        })
        {
            using var response = await client.GetAsync(new Uri($"/?state={state}", UriKind.Relative));
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(status, response.StatusCode);
            Assert.Contains(code, body, StringComparison.Ordinal);
            Assert.DoesNotContain("11111111-2222", body, StringComparison.Ordinal);
            Assert.DoesNotContain("duplicate key", body, StringComparison.Ordinal);
        }
    }
}
