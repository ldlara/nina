using System.Net;
using System.Text.Json.Nodes;
using Nina.Identity.Tests.Infrastructure;
using Npgsql;

namespace Nina.Identity.Tests.Integration;

/// <summary>SEC-011, RF-054, RF-002-A4, AZ-17: refresh rotativo com detecção de reuso, logout, sessões e dispositivos.</summary>
public sealed class RefreshAndSessionTests(PostgresFixture postgres) : IntegrationTestBase(postgres)
{
    private Task<ApiResponse> Refresh(string token, Guid deviceId) =>
        Api.PostAsync("/v1/auth/refresh", new JsonObject { ["refresh_token"] = token, ["device_id"] = deviceId.ToString() });

    [Fact]
    public async Task Refresh_rotates_the_token_and_issues_a_new_access_token()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var refreshed = await Refresh(s.RefreshToken, s.DeviceId);

        Assert.Equal(HttpStatusCode.OK, refreshed.Status);
        var next = refreshed.Json!["refresh_token"]!.GetValue<string>();
        Assert.NotEqual(s.RefreshToken, next);
        Assert.Equal(s.SessionId.ToString(), refreshed.Json["session_id"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("/v1/me", refreshed.Json["access_token"]!.GetValue<string>())).Status);
        Assert.Equal(HttpStatusCode.OK, (await Refresh(next, s.DeviceId)).Status);
    }

    [Fact]
    public async Task Refresh_stores_only_the_hash_of_the_token()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var hash = await AdminScalarAsync<byte[]>("SELECT token_hash FROM nina.refresh_token WHERE session_id = @s", new NpgsqlParameter("s", s.SessionId));

        Assert.Equal(32, hash!.Length);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s.RefreshToken)), hash);
    }

    [Fact]
    public async Task Reusing_a_rotated_refresh_token_revokes_the_whole_session_family()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var first = await Refresh(s.RefreshToken, s.DeviceId);
        var legitNext = first.Json!["refresh_token"]!.GetValue<string>();
        var legitAccess = first.Json["access_token"]!.GetValue<string>();

        var replay = await Refresh(s.RefreshToken, s.DeviceId);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.Status);
        Assert.Equal("REFRESH_TOKEN_REUSED", replay.Code);

        // Toda a família cai: o token novo e o access token da sessão deixam de funcionar.
        var afterRefresh = await Refresh(legitNext, s.DeviceId);
        Assert.Equal(HttpStatusCode.Unauthorized, afterRefresh.Status);
        Assert.Equal("SESSION_REVOKED", afterRefresh.Code);
        var afterAccess = await Api.GetAsync("/v1/me", legitAccess);
        Assert.Equal("SESSION_REVOKED", afterAccess.Code);

        Assert.Equal("REUSE_DETECTED", await AdminScalarAsync<string>("SELECT revoked_reason FROM nina.auth_session WHERE id = @s", new NpgsqlParameter("s", s.SessionId)));
        Assert.Equal(1, await AdminScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE action = 'auth.refresh_reuse_detected' AND is_critical"));
    }

    [Fact]
    public async Task Refresh_rejects_garbage_forged_and_wrong_device_tokens()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var other = await Api.RegisterAndVerifyAsync();

        var garbage = await Refresh("rt_garbage", s.DeviceId);
        var wrongDevice = await Refresh(s.RefreshToken, Guid.NewGuid());
        // Token de outro usuário com o userId trocado pelo do primeiro: RLS esconde a sessão.
        var crossed = await Refresh(other.RefreshToken, other.DeviceId);

        Assert.Equal("INVALID_REFRESH_TOKEN", garbage.Code);
        Assert.Equal("INVALID_REFRESH_TOKEN", wrongDevice.Code);
        Assert.Equal(HttpStatusCode.OK, crossed.Status);
        // O refresh do dispositivo errado não deve ter consumido o token.
        Assert.Equal(HttpStatusCode.OK, (await Refresh(s.RefreshToken, s.DeviceId)).Status);
    }

    [Fact]
    public async Task Refresh_fails_after_the_absolute_session_lifetime()
    {
        var s = await Api.RegisterAndVerifyAsync();
        Factory.Time.Advance(TimeSpan.FromDays(31));

        var response = await Refresh(s.RefreshToken, s.DeviceId);

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        Assert.Equal("SESSION_EXPIRED", response.Code);
    }

    [Fact]
    public async Task Logout_revokes_the_session_immediately_and_is_idempotent()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var logout = await Api.PostAsync("/v1/auth/logout", null, s.AccessToken);
        var again = await Api.PostAsync("/v1/auth/logout", null, s.AccessToken);
        var me = await Api.GetAsync("/v1/me", s.AccessToken);
        var refresh = await Refresh(s.RefreshToken, s.DeviceId);

        Assert.Equal(HttpStatusCode.NoContent, logout.Status);
        Assert.Equal(HttpStatusCode.NoContent, again.Status);
        Assert.Equal("SESSION_REVOKED", me.Code);
        Assert.Equal("SESSION_REVOKED", refresh.Code);
    }

    [Fact]
    public async Task Logout_removes_the_push_token_of_the_device()
    {
        var s = await Api.RegisterAndVerifyAsync();
        await Api.SendAsync(HttpMethod.Put, $"/v1/me/push-tokens/{s.DeviceId}", new JsonObject { ["platform"] = "APNS", ["token"] = "tok-1" }, s.AccessToken);
        Assert.Equal(1, await AdminScalarAsync<long>("SELECT count(*) FROM nina.device_push_token"));

        await Api.PostAsync("/v1/auth/logout", null, s.AccessToken);

        Assert.Equal(0, await AdminScalarAsync<long>("SELECT count(*) FROM nina.device_push_token"));
    }

    [Fact]
    public async Task Sessions_list_shows_only_own_active_sessions_and_marks_the_current_one()
    {
        var a = await Api.RegisterAndVerifyAsync();
        var a2 = await Api.LoginAsync(a.Email, a.Password, platform: "ANDROID");
        var b = await Api.RegisterAndVerifyAsync();

        var listA = await Api.GetAsync("/v1/me/sessions", a2.AccessToken);
        var listB = await Api.GetAsync("/v1/me/sessions", b.AccessToken);

        var items = listA.Json!["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.Single(items, i => i!["is_current"]!.GetValue<bool>());
        Assert.Contains(items, i => i!["platform"]!.GetValue<string>() == "ANDROID");
        Assert.DoesNotContain(listB.Json!["items"]!.AsArray(), i => i!["id"]!.GetValue<string>() == a.SessionId.ToString());
        Assert.Single(listB.Json["items"]!.AsArray());
    }

    [Fact]
    public async Task Revoking_a_session_kills_its_tokens_at_once_and_other_users_get_404()
    {
        var a = await Api.RegisterAndVerifyAsync();
        var a2 = await Api.LoginAsync(a.Email, a.Password);
        var intruder = await Api.RegisterAndVerifyAsync();

        var foreign = await Api.SendAsync(HttpMethod.Delete, $"/v1/me/sessions/{a.SessionId}", null, intruder.AccessToken);
        var revoke = await Api.SendAsync(HttpMethod.Delete, $"/v1/me/sessions/{a.SessionId}", null, a2.AccessToken);
        var revokeAgain = await Api.SendAsync(HttpMethod.Delete, $"/v1/me/sessions/{a.SessionId}", null, a2.AccessToken);

        Assert.Equal(HttpStatusCode.NotFound, foreign.Status);
        Assert.Equal(HttpStatusCode.NoContent, revoke.Status);
        Assert.Equal(HttpStatusCode.NoContent, revokeAgain.Status);
        Assert.Equal("SESSION_REVOKED", (await Api.GetAsync("/v1/me", a.AccessToken)).Code);
        Assert.Equal("SESSION_REVOKED", (await Refresh(a.RefreshToken, a.DeviceId)).Code);
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("/v1/me", a2.AccessToken)).Status);
    }

    [Fact]
    public async Task Revoking_a_nonexistent_session_returns_404()
    {
        var a = await Api.RegisterAndVerifyAsync();
        var response = await Api.SendAsync(HttpMethod.Delete, $"/v1/me/sessions/{Guid.NewGuid()}", null, a.AccessToken);

        Assert.Equal(HttpStatusCode.NotFound, response.Status);
        Assert.Equal("NOT_FOUND", response.Code);
    }

    [Fact]
    public async Task Revoking_other_sessions_keeps_the_current_one_and_returns_the_count()
    {
        var a = await Api.RegisterAndVerifyAsync();
        var a2 = await Api.LoginAsync(a.Email, a.Password);
        var a3 = await Api.LoginAsync(a.Email, a.Password);

        var response = await Api.PostAsync("/v1/me/session-revocations", null, a3.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal(2, response.Json!["revoked_count"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("/v1/me", a3.AccessToken)).Status);
        Assert.Equal("SESSION_REVOKED", (await Api.GetAsync("/v1/me", a.AccessToken)).Code);
        Assert.Equal("SESSION_REVOKED", (await Api.GetAsync("/v1/me", a2.AccessToken)).Code);
    }

    [Fact]
    public async Task Revoked_session_does_not_delete_the_clients_pending_state_contract_is_401_only()
    {
        // RF-001-A4: o servidor só responde 401 SESSION_REVOKED; não há efeito colateral nos dados do usuário.
        var s = await Api.RegisterAndVerifyAsync();
        await Api.SendAsync(HttpMethod.Delete, $"/v1/me/sessions/{s.SessionId}", null, s.AccessToken);

        var response = await Api.GetAsync("/v1/me", s.AccessToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        Assert.Equal(1, await AdminScalarAsync<long>("SELECT count(*) FROM nina.app_user"));
    }
}
