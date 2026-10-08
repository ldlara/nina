using System.Net;
using System.Text.Json.Nodes;
using Nina.Identity.Mail;
using Nina.Identity.Tests.Infrastructure;
using Npgsql;

namespace Nina.Identity.Tests.Integration;

/// <summary>RF-002, SEC-014/017: recuperação de senha, troca de senha e reautenticação (X-Reauth-Token).</summary>
public sealed class PasswordAndReauthTests(PostgresFixture postgres) : IntegrationTestBase(postgres)
{
    private const string NewPassword = "a-brand-new-passphrase-42";

    private Task<ApiResponse> Forgot(string email) =>
        Api.PostAsync("/v1/auth/password/forgot", new JsonObject { ["email"] = email });

    private Task<ApiResponse> Reset(string token, string password = NewPassword) =>
        Api.PostAsync("/v1/auth/password/reset", new JsonObject { ["token"] = token, ["new_password"] = password });

    private string LastResetToken(string email) =>
        Factory.Mailer.Sent.Last(m => m.To == email && m.Kind == MailKind.PasswordReset).Secret!;

    private async Task<string> ReauthAsync(Session s, string? password = null)
    {
        var response = await Api.PostAsync("/v1/auth/reauthenticate", new JsonObject { ["password"] = password ?? s.Password }, s.AccessToken);
        Assert.Equal(HttpStatusCode.OK, response.Status);
        return response.Json!["reauth_token"]!.GetValue<string>();
    }

    [Fact]
    public async Task Forgot_returns_identical_202_for_registered_and_unknown_emails_and_only_mails_the_real_one()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var unknown = ApiClient.NewEmail();

        var known = await Forgot(s.Email);
        var other = await Forgot(unknown);

        Assert.Equal(HttpStatusCode.Accepted, known.Status);
        Assert.Equal(known.Status, other.Status);
        Assert.Equal(known.Json?.ToJsonString(), other.Json?.ToJsonString());
        Assert.Contains(Factory.Mailer.Sent, m => m.To == s.Email && m.Kind == MailKind.PasswordReset);
        Assert.DoesNotContain(Factory.Mailer.Sent, m => m.To == unknown);
    }

    [Fact]
    public async Task Reset_sets_the_new_password_consumes_the_token_and_ends_all_sessions()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var other = await Api.LoginAsync(s.Email, s.Password);
        await Forgot(s.Email);
        var token = LastResetToken(s.Email);

        var reset = await Reset(token);

        Assert.Equal(HttpStatusCode.NoContent, reset.Status);
        Assert.Equal("SESSION_REVOKED", (await Api.GetAsync("/v1/me", s.AccessToken)).Code);
        Assert.Equal("SESSION_REVOKED", (await Api.GetAsync("/v1/me", other.AccessToken)).Code);
        Assert.Equal(HttpStatusCode.OK, (await Api.PostAsync("/v1/auth/login", new JsonObject { ["email"] = s.Email, ["password"] = NewPassword, ["device"] = ApiClient.Device() })).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.PostAsync("/v1/auth/login", new JsonObject { ["email"] = s.Email, ["password"] = s.Password, ["device"] = ApiClient.Device() })).Status);
        Assert.Contains(Factory.Mailer.Sent, m => m.To == s.Email && m.Notice == SecurityNotice.PasswordReset);

        var reuse = await Reset(token, "yet-another-passphrase-77");
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.Status);
        Assert.Equal("INVALID_RESET_TOKEN", reuse.Code);
    }

    [Fact]
    public async Task Reset_rejects_expired_unknown_and_weak_inputs()
    {
        var s = await Api.RegisterAndVerifyAsync();
        await Forgot(s.Email);
        var token = LastResetToken(s.Email);

        var weak = await Reset(token, "short");
        Assert.Equal(HttpStatusCode.BadRequest, weak.Status);
        Assert.Equal("PASSWORD_TOO_SHORT", weak.FieldErrorCode("new_password"));

        var unknown = await Reset("not-a-real-token-at-all");
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.Status);

        Factory.Time.Advance(TimeSpan.FromMinutes(31));
        var expired = await Reset(token);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.Status);
        Assert.Equal("INVALID_RESET_TOKEN", expired.Code);
    }

    [Fact]
    public async Task Reset_token_is_stored_only_as_a_hash()
    {
        var s = await Api.RegisterAndVerifyAsync();
        await Forgot(s.Email);
        var token = LastResetToken(s.Email);

        var matches = await AdminScalarAsync<long>(
            "SELECT count(*) FROM nina.recovery_request WHERE encode(token_hash, 'escape') = @t OR encode(token_hash, 'base64') = @t",
            new NpgsqlParameter("t", token));
        Assert.Equal(0, matches);
    }

    [Fact]
    public async Task Reset_lets_a_social_only_account_add_a_password()
    {
        var email = ApiClient.NewEmail();
        var google = await Api.PostAsync("/v1/auth/google", new JsonObject
        {
            ["id_token"] = FakeIdentityTokenVerifier.Token("GOOGLE", "sub-1", email, true, "n1"),
            ["nonce"] = "n1",
            ["device"] = ApiClient.Device(),
            ["consents"] = ApiClient.Consents(),
        });
        Assert.False(google.Json!["user"]!["has_password"]!.GetValue<bool>());

        await Forgot(email);
        Assert.Equal(HttpStatusCode.NoContent, (await Reset(LastResetToken(email))).Status);

        Assert.Equal(HttpStatusCode.OK, (await Api.PostAsync("/v1/auth/login", new JsonObject { ["email"] = email, ["password"] = NewPassword, ["device"] = ApiClient.Device() })).Status);
    }

    [Fact]
    public async Task Forgot_is_rate_limited_per_email_without_revealing_it()
    {
        var s = await Api.RegisterAndVerifyAsync();
        for (var i = 0; i < 6; i++)
        {
            var r = await Forgot(s.Email);
            Assert.Equal(HttpStatusCode.Accepted, r.Status);
        }

        Assert.Equal(3, Factory.Mailer.Sent.Count(m => m.To == s.Email && m.Kind == MailKind.PasswordReset));
    }

    [Fact]
    public async Task Reauthenticate_returns_a_short_lived_token_and_rejects_wrong_passwords()
    {
        var s = await Api.RegisterAndVerifyAsync();

        var wrong = await Api.PostAsync("/v1/auth/reauthenticate", new JsonObject { ["password"] = "not-my-password-1" }, s.AccessToken);
        var ok = await Api.PostAsync("/v1/auth/reauthenticate", new JsonObject { ["password"] = s.Password }, s.AccessToken);
        var missing = await Api.PostAsync("/v1/auth/reauthenticate", new JsonObject(), s.AccessToken);
        var anonymous = await Api.PostAsync("/v1/auth/reauthenticate", new JsonObject { ["password"] = s.Password });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
        Assert.Equal("INVALID_CREDENTIALS", wrong.Code);
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal(300, ok.Json!["expires_in"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
    }

    [Fact]
    public async Task Reauthenticate_is_rate_limited_after_repeated_failures()
    {
        var s = await Api.RegisterAndVerifyAsync();
        for (var i = 0; i < 5; i++)
        {
            await Api.PostAsync("/v1/auth/reauthenticate", new JsonObject { ["password"] = "not-my-password-1" }, s.AccessToken);
        }

        var blocked = await Api.PostAsync("/v1/auth/reauthenticate", new JsonObject { ["password"] = s.Password }, s.AccessToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.Status);
    }

    [Fact]
    public async Task Change_password_requires_a_valid_reauth_token_bound_to_the_same_session()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var other = await Api.LoginAsync(s.Email, s.Password);
        var body = new JsonObject { ["new_password"] = NewPassword };

        var without = await Api.SendAsync(HttpMethod.Put, "/v1/me/password", body, s.AccessToken);
        var garbage = await Api.SendAsync(HttpMethod.Put, "/v1/me/password", body, s.AccessToken, r => r.Headers.Add("X-Reauth-Token", "garbage"));
        var otherSessionToken = await ReauthAsync(other);
        var wrongSession = await Api.SendAsync(HttpMethod.Put, "/v1/me/password", body, s.AccessToken, r => r.Headers.Add("X-Reauth-Token", otherSessionToken));

        Assert.Equal(HttpStatusCode.Forbidden, without.Status);
        Assert.Equal("REAUTH_REQUIRED", without.Code);
        Assert.Equal("REAUTH_REQUIRED", garbage.Code);
        Assert.Equal("REAUTH_REQUIRED", wrongSession.Code);
    }

    [Fact]
    public async Task Change_password_works_revokes_other_sessions_and_notifies()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var other = await Api.LoginAsync(s.Email, s.Password);
        var reauth = await ReauthAsync(s);

        var change = await Api.SendAsync(HttpMethod.Put, "/v1/me/password", new JsonObject { ["new_password"] = NewPassword }, s.AccessToken, r => r.Headers.Add("X-Reauth-Token", reauth));

        Assert.Equal(HttpStatusCode.NoContent, change.Status);
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("/v1/me", s.AccessToken)).Status);
        Assert.Equal("SESSION_REVOKED", (await Api.GetAsync("/v1/me", other.AccessToken)).Code);
        Assert.Equal(HttpStatusCode.OK, (await Api.PostAsync("/v1/auth/login", new JsonObject { ["email"] = s.Email, ["password"] = NewPassword, ["device"] = ApiClient.Device() })).Status);
        Assert.Contains(Factory.Mailer.Sent, m => m.To == s.Email && m.Notice == SecurityNotice.PasswordChanged);
        Assert.Equal(1, await AdminScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE action = 'auth.password_changed' AND is_critical AND chain_seq IS NOT NULL"));
    }

    [Fact]
    public async Task Reauth_token_expires_after_five_minutes()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var reauth = await ReauthAsync(s);
        Factory.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30));
        var refreshed = await Api.PostAsync("/v1/auth/refresh", new JsonObject { ["refresh_token"] = s.RefreshToken, ["device_id"] = s.DeviceId.ToString() });
        var access = refreshed.Json!["access_token"]!.GetValue<string>();

        var change = await Api.SendAsync(HttpMethod.Put, "/v1/me/password", new JsonObject { ["new_password"] = NewPassword }, access, r => r.Headers.Add("X-Reauth-Token", reauth));

        Assert.Equal(HttpStatusCode.Forbidden, change.Status);
        Assert.Equal("REAUTH_REQUIRED", change.Code);
    }
}
