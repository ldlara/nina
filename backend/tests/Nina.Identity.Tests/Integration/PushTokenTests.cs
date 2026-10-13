using System.Net;
using System.Text.Json.Nodes;
using Nina.Identity.Tests.Infrastructure;
using Npgsql;

namespace Nina.Identity.Tests.Integration;

/// <summary>RF-037/039: registro idempotente de token de push por dispositivo; o token nunca volta nas respostas.</summary>
public sealed class PushTokenTests(PostgresFixture postgres) : IntegrationTestBase(postgres)
{
    private static JsonObject Body(string token = "apns-token-1", string platform = "APNS") =>
        new() { ["platform"] = platform, ["token"] = token, ["environment"] = "SANDBOX", ["app_version"] = "1.0.0", ["os_notifications_authorized"] = true };

    [Fact]
    public async Task Put_registers_and_updates_the_token_for_the_device_without_echoing_it()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var first = await Api.SendAsync(HttpMethod.Put, $"/v1/me/push-tokens/{s.DeviceId}", Body(), s.AccessToken);
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        var second = await Api.SendAsync(HttpMethod.Put, $"/v1/me/push-tokens/{s.DeviceId}", Body("apns-token-2"), s.AccessToken);

        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(s.DeviceId.ToString(), first.Json!["device_id"]!.GetValue<string>());
        Assert.Equal("APNS", first.Json["platform"]!.GetValue<string>());
        Assert.Null(first.Json["token"]);
        Assert.Equal(first.Json["registered_at"]!.ToString(), second.Json!["registered_at"]!.ToString());
        Assert.NotEqual(first.Json["updated_at"]!.ToString(), second.Json["updated_at"]!.ToString());
        Assert.Equal(1, await AdminScalarAsync<long>("SELECT count(*) FROM nina.device_push_token"));
        Assert.Equal("apns-token-2", await AdminScalarAsync<string>("SELECT token FROM nina.device_push_token"));
    }

    [Fact]
    public async Task Put_validates_platform_token_and_device_ownership()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var mismatch = await Api.SendAsync(HttpMethod.Put, $"/v1/me/push-tokens/{s.DeviceId}", Body(platform: "FCM"), s.AccessToken);
        var empty = await Api.SendAsync(HttpMethod.Put, $"/v1/me/push-tokens/{s.DeviceId}", Body(token: string.Empty), s.AccessToken);
        var foreignDevice = await Api.SendAsync(HttpMethod.Put, $"/v1/me/push-tokens/{Guid.NewGuid()}", Body(), s.AccessToken);
        var anonymous = await Api.SendAsync(HttpMethod.Put, $"/v1/me/push-tokens/{s.DeviceId}", Body());

        Assert.Equal("PLATFORM_MISMATCH", mismatch.FieldErrorCode("platform"));
        Assert.Equal("REQUIRED", empty.FieldErrorCode("token"));
        Assert.Equal(HttpStatusCode.NotFound, foreignDevice.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
    }

    [Fact]
    public async Task Another_user_cannot_register_or_remove_a_token_for_a_device_they_do_not_have()
    {
        var a = await Api.RegisterAndVerifyAsync();
        var b = await Api.RegisterAndVerifyAsync();
        await Api.SendAsync(HttpMethod.Put, $"/v1/me/push-tokens/{a.DeviceId}", Body(), a.AccessToken);

        var hijack = await Api.SendAsync(HttpMethod.Put, $"/v1/me/push-tokens/{a.DeviceId}", Body("other"), b.AccessToken);
        await Api.SendAsync(HttpMethod.Delete, $"/v1/me/push-tokens/{a.DeviceId}", null, b.AccessToken);

        Assert.Equal(HttpStatusCode.NotFound, hijack.Status);
        Assert.Equal("apns-token-1", await AdminScalarAsync<string>("SELECT token FROM nina.device_push_token"));
    }

    [Fact]
    public async Task The_same_token_registered_by_another_user_is_a_conflict_not_a_leak()
    {
        var a = await Api.RegisterAndVerifyAsync();
        var b = await Api.RegisterAndVerifyAsync();
        await Api.SendAsync(HttpMethod.Put, $"/v1/me/push-tokens/{a.DeviceId}", Body("shared"), a.AccessToken);

        var response = await Api.SendAsync(HttpMethod.Put, $"/v1/me/push-tokens/{b.DeviceId}", Body("shared"), b.AccessToken);

        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        Assert.Equal("PUSH_TOKEN_CONFLICT", response.Code);
    }

    [Fact]
    public async Task Delete_is_idempotent()
    {
        var s = await Api.RegisterAndVerifyAsync();
        await Api.SendAsync(HttpMethod.Put, $"/v1/me/push-tokens/{s.DeviceId}", Body(), s.AccessToken);

        var first = await Api.SendAsync(HttpMethod.Delete, $"/v1/me/push-tokens/{s.DeviceId}", null, s.AccessToken);
        var second = await Api.SendAsync(HttpMethod.Delete, $"/v1/me/push-tokens/{s.DeviceId}", null, s.AccessToken);

        Assert.Equal(HttpStatusCode.NoContent, first.Status);
        Assert.Equal(HttpStatusCode.NoContent, second.Status);
        Assert.Equal(0, await AdminScalarAsync<long>("SELECT count(*) FROM nina.device_push_token WHERE device_id = @d", new NpgsqlParameter("d", s.DeviceId)));
    }

    [Fact]
    public async Task Revoking_a_session_removes_its_device_token()
    {
        var a = await Api.RegisterAndVerifyAsync();
        var a2 = await Api.LoginAsync(a.Email, a.Password, platform: "ANDROID");
        await Api.SendAsync(HttpMethod.Put, $"/v1/me/push-tokens/{a2.DeviceId}", Body("fcm-1", "FCM"), a2.AccessToken);

        await Api.SendAsync(HttpMethod.Delete, $"/v1/me/sessions/{a2.SessionId}", null, a.AccessToken);

        Assert.Equal(0, await AdminScalarAsync<long>("SELECT count(*) FROM nina.device_push_token"));
    }
}
