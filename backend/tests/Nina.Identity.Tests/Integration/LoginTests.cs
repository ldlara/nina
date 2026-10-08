using System.Net;
using System.Text.Json.Nodes;
using Nina.Identity.Tests.Infrastructure;

namespace Nina.Identity.Tests.Integration;

/// <summary>RF-001-A3, SEC-040/041/050, AZ-16: login por senha, erro genérico, lockout e uma sessão por dispositivo.</summary>
public sealed class LoginTests(PostgresFixture postgres) : IntegrationTestBase(postgres)
{
    private Task<ApiResponse> Login(string email, string password, Guid? deviceId = null, string ip = "203.0.113.10") =>
        Api.PostAsync(
            "/v1/auth/login",
            new JsonObject { ["email"] = email, ["password"] = password, ["device"] = ApiClient.Device(deviceId) },
            tweak: r => r.Headers.Add("X-Forwarded-For", ip));

    [Fact]
    public async Task Login_with_valid_credentials_returns_a_session_bound_to_the_device()
    {
        var registered = await Api.RegisterAndVerifyAsync();
        var deviceId = Guid.NewGuid();
        var response = await Login(registered.Email, TestConstants.GoodPassword, deviceId);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        var sessions = await Api.GetAsync("/v1/me/sessions", response.Json!["access_token"]!.GetValue<string>());
        var current = sessions.Json!["items"]!.AsArray().Single(s => s!["is_current"]!.GetValue<bool>());
        Assert.Equal(deviceId.ToString(), current!["device_id"]!.GetValue<string>());
        Assert.Equal(response.Json["session_id"]!.GetValue<string>(), current["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Login_failures_for_unknown_email_and_wrong_password_are_indistinguishable()
    {
        var registered = await Api.RegisterAndVerifyAsync();
        var wrongPassword = await Login(registered.Email, "wrong-password-123");
        var unknown = await Login(ApiClient.NewEmail(), "wrong-password-123");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.Status);
        Assert.Equal(wrongPassword.Status, unknown.Status);
        Assert.Equal("INVALID_CREDENTIALS", wrongPassword.Code);
        Assert.Equal(wrongPassword.Code, unknown.Code);
        Assert.Equal(wrongPassword.Json!["title"]!.ToString(), unknown.Json!["title"]!.ToString());
        Assert.Equal(wrongPassword.Json["type"]!.ToString(), unknown.Json["type"]!.ToString());
        Assert.Equal(wrongPassword.Json.AsObject().Count, unknown.Json.AsObject().Count);
    }

    [Fact]
    public async Task Login_locks_out_after_repeated_failures_and_recovers_after_the_window()
    {
        var registered = await Api.RegisterAndVerifyAsync();
        for (var i = 0; i < 5; i++)
        {
            var failed = await Login(registered.Email, "wrong-password-123");
            Assert.Equal(HttpStatusCode.Unauthorized, failed.Status);
        }

        // Mesmo com a senha certa, a conta+IP está bloqueada (credential stuffing).
        var locked = await Login(registered.Email, TestConstants.GoodPassword);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.Status);
        Assert.Equal("RATE_LIMITED", locked.Code);
        Assert.InRange(locked.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 15 * 60);

        // Outro IP, mesma conta: o bloqueio é por conta+IP, então ele ainda pode tentar (e acerta).
        var otherIp = await Login(registered.Email, TestConstants.GoodPassword, ip: "203.0.113.99");
        Assert.Equal(HttpStatusCode.OK, otherIp.Status);

        Factory.Time.Advance(TimeSpan.FromMinutes(16));
        var recovered = await Login(registered.Email, TestConstants.GoodPassword);
        Assert.Equal(HttpStatusCode.OK, recovered.Status);
    }

    [Fact]
    public async Task Login_distributed_attack_on_one_account_hits_the_account_limit()
    {
        var registered = await Api.RegisterAndVerifyAsync();
        for (var i = 0; i < 20; i++)
        {
            await Login(registered.Email, "wrong-password-123", ip: $"198.51.100.{i + 1}");
        }

        var response = await Login(registered.Email, TestConstants.GoodPassword, ip: "198.51.100.200");
        Assert.Equal(HttpStatusCode.TooManyRequests, response.Status);
    }

    [Fact]
    public async Task Login_validates_the_request()
    {
        var response = await Api.PostAsync("/v1/auth/login", new JsonObject
        {
            ["email"] = "x@example.org", ["password"] = "irrelevant-password",
            ["device"] = new JsonObject { ["device_id"] = Guid.NewGuid().ToString(), ["platform"] = "SYMBIAN" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal("UNSUPPORTED_VALUE", response.FieldErrorCode("device.platform"));
    }

    [Fact]
    public async Task Login_on_the_same_device_replaces_the_previous_session_immediately()
    {
        var first = await Api.RegisterAndVerifyAsync();
        var second = await Api.LoginAsync(first.Email, first.Password, first.DeviceId);

        var old = await Api.GetAsync("/v1/me", first.AccessToken);
        var current = await Api.GetAsync("/v1/me", second.AccessToken);

        Assert.Equal(HttpStatusCode.Unauthorized, old.Status);
        Assert.Equal("SESSION_REVOKED", old.Code);
        Assert.Equal(HttpStatusCode.OK, current.Status);
    }

    [Fact]
    public async Task Login_rehashes_passwords_stored_with_weaker_argon2id_parameters()
    {
        var registered = await Api.RegisterAndVerifyAsync();
        var weak = new Crypto.PasswordHasher(Microsoft.Extensions.Options.Options.Create(
            new IdentityOptions { Argon2MemoryKiB = 1024, Argon2Iterations = 1, Argon2Parallelism = 1 }));
        var weakHash = await weak.HashAsync(TestConstants.GoodPassword);
        weak.Dispose();
        await AdminExecAsync($"UPDATE nina.user_credential SET password_hash = '{weakHash}'");

        var login = await Login(registered.Email, TestConstants.GoodPassword);

        Assert.Equal(HttpStatusCode.OK, login.Status);
        var stored = await AdminScalarAsync<string>("SELECT password_hash FROM nina.user_credential");
        Assert.Contains("m=4096,t=2,p=1", stored, StringComparison.Ordinal);
    }
}
