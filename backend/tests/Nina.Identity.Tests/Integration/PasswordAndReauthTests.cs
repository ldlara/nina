using System.Net;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;
using Nina.Identity.Mail;
using Nina.Identity.Tests.Infrastructure;
using Nina.SharedKernel.Security;
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
        Assert.Equal("PASSWORD_POLICY", weak.FieldErrorCode("new_password"));

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
            ["id_token"] = FakeIdentityTokenVerifier.Token("GOOGLE", "sub-1", email, true, "nonce-0001"),
            ["nonce"] = "nonce-0001",
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
    public async Task Reauthenticate_returns_a_short_lived_scoped_token_and_rejects_wrong_passwords()
    {
        var s = await Api.RegisterAndVerifyAsync();

        var wrong = await Api.PostAsync("/v1/auth/reauthenticate", new JsonObject { ["password"] = "not-my-password-1" }, s.AccessToken);
        var ok = await Api.PostAsync("/v1/auth/reauthenticate", new JsonObject { ["password"] = s.Password, ["scope"] = new JsonArray("ACCOUNT_PASSWORD_CHANGE") }, s.AccessToken);
        var anonymous = await Api.PostAsync("/v1/auth/reauthenticate", new JsonObject { ["password"] = s.Password });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
        Assert.Equal("INVALID_CREDENTIALS", wrong.Code);
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal(300, ok.Json!["expires_in"]!.GetValue<int>());
        Assert.Equal("ACCOUNT_PASSWORD_CHANGE", ok.Json["scope"]![0]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
    }

    [Fact]
    public async Task Reauthenticate_without_scope_omits_it_in_the_response_for_the_v1_transition()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var response = await Api.PostAsync("/v1/auth/reauthenticate", new JsonObject { ["password"] = s.Password }, s.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Null(response.Json!["scope"]);
    }

    [Fact]
    public async Task Reauthenticate_validates_exactly_one_credential_and_the_scopes()
    {
        var s = await Api.RegisterAndVerifyAsync();
        Task<ApiResponse> Post(JsonObject body) => Api.PostAsync("/v1/auth/reauthenticate", body, s.AccessToken);

        var none = await Post(new JsonObject());
        var both = await Post(new JsonObject { ["password"] = s.Password, ["provider"] = "GOOGLE", ["id_token"] = new string('x', 20), ["nonce"] = "nonce-0001" });
        var badScope = await Post(new JsonObject { ["password"] = s.Password, ["scope"] = new JsonArray("NOPE") });
        var tooMany = await Post(new JsonObject { ["password"] = s.Password, ["scope"] = new JsonArray("IDENTITY_LINK", "IDENTITY_UNLINK", "BABY_DELETE", "ACCOUNT_DELETE") });
        var duplicate = await Post(new JsonObject { ["password"] = s.Password, ["scope"] = new JsonArray("IDENTITY_LINK", "IDENTITY_LINK") });
        var extra = await Post(new JsonObject { ["password"] = s.Password, ["admin"] = true });
        var longPassword = await Post(new JsonObject { ["password"] = new string('p', 129) });

        Assert.Equal("REQUIRED", none.FieldErrorCode("password"));
        Assert.Equal("CONFLICTING_CREDENTIALS", both.FieldErrorCode("password"));
        Assert.Equal("UNSUPPORTED_VALUE", badScope.FieldErrorCode("scope[0]"));
        Assert.Equal("TOO_MANY", tooMany.FieldErrorCode("scope"));
        Assert.Equal("DUPLICATE", duplicate.FieldErrorCode("scope"));
        Assert.Equal(HttpStatusCode.BadRequest, extra.Status);
        Assert.Equal("TOO_LONG", longPassword.FieldErrorCode("password"));
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
        Assert.NotNull(blocked.Headers.RetryAfter);
    }

    private Task<ApiResponse> ChangePassword(Session s, string? reauth, string password = NewPassword) =>
        Api.SendAsync(HttpMethod.Put, "/v1/me/password", new JsonObject { ["new_password"] = password }, s.AccessToken, r =>
        {
            if (reauth is not null)
            {
                r.Headers.Add("X-Reauth-Token", reauth);
            }
        });

    [Fact]
    public async Task Change_password_requires_a_valid_reauth_token_bound_to_the_same_session_and_scope()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var other = await Api.LoginAsync(s.Email, s.Password);
        var otherSessionToken = await Api.ReauthAsync(other, ReauthScopes.AccountPasswordChange);
        var wrongScope = await Api.ReauthAsync(s, ReauthScopes.IdentityLink);

        foreach (var attempt in new[] { null, "garbage-reauth-token-0123", otherSessionToken, wrongScope })
        {
            var response = await ChangePassword(s, attempt);
            Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
            Assert.Equal("REAUTH_REQUIRED", response.Code);
        }

        Assert.Equal(HttpStatusCode.OK, (await Api.PostAsync("/v1/auth/login", new JsonObject { ["email"] = s.Email, ["password"] = s.Password, ["device"] = ApiClient.Device() })).Status);
    }

    [Fact]
    public async Task Reauth_token_is_single_use()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var token = await Api.ReauthAsync(s, ReauthScopes.AccountPasswordChange);

        var first = await ChangePassword(s, token);
        var replay = await ChangePassword(s, token, "yet-another-passphrase-77");

        Assert.Equal(HttpStatusCode.NoContent, first.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.Status);
        Assert.Equal("REAUTH_REQUIRED", replay.Code);
        Assert.Equal(HttpStatusCode.OK, (await Api.PostAsync("/v1/auth/login", new JsonObject { ["email"] = s.Email, ["password"] = NewPassword, ["device"] = ApiClient.Device() })).Status);
    }

    [Fact]
    public async Task Concurrent_uses_of_the_same_reauth_token_succeed_exactly_once()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var token = await Api.ReauthAsync(s, ReauthScopes.AccountPasswordChange);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => ChangePassword(s, token, $"concurrent-passphrase-{i}")));

        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.NoContent));
        Assert.Equal(7, results.Count(r => r.Status == HttpStatusCode.Unauthorized && r.Code == "REAUTH_REQUIRED"));
    }

    [Fact]
    public async Task Reauth_token_without_scope_serves_exactly_one_sensitive_operation()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var token = await Api.ReauthAsync(s);

        Assert.Equal(HttpStatusCode.NoContent, (await ChangePassword(s, token)).Status);
        Assert.Equal("REAUTH_REQUIRED", (await ChangePassword(s, token)).Code);
    }

    [Fact]
    public async Task Change_password_validates_the_new_password_before_consuming_nothing_it_should_not()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var token = await Api.ReauthAsync(s, ReauthScopes.AccountPasswordChange);

        var weak = await ChangePassword(s, token, "short");
        var strong = await ChangePassword(s, token);

        Assert.Equal(HttpStatusCode.BadRequest, weak.Status);
        Assert.Equal("PASSWORD_POLICY", weak.FieldErrorCode("new_password"));
        // A recusa por política acontece depois do consumo: o token é de uso único mesmo quando a operação falha.
        Assert.Equal(HttpStatusCode.Unauthorized, strong.Status);
    }

    [Fact]
    public async Task Change_password_works_revokes_other_sessions_and_notifies()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var other = await Api.LoginAsync(s.Email, s.Password);
        var reauth = await Api.ReauthAsync(s, ReauthScopes.AccountPasswordChange);

        var change = await ChangePassword(s, reauth);

        Assert.Equal(HttpStatusCode.NoContent, change.Status);
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("/v1/me", s.AccessToken)).Status);
        Assert.Equal("SESSION_REVOKED", (await Api.GetAsync("/v1/me", other.AccessToken)).Code);
        Assert.Equal(HttpStatusCode.OK, (await Api.PostAsync("/v1/auth/login", new JsonObject { ["email"] = s.Email, ["password"] = NewPassword, ["device"] = ApiClient.Device() })).Status);
        Assert.Contains(Factory.Mailer.Sent, m => m.To == s.Email && m.Notice == SecurityNotice.PasswordChanged);
        Assert.Equal(1, await AdminScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE action = 'auth.password_changed' AND is_critical AND chain_seq IS NOT NULL"));
        Assert.Equal(1, await AdminScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE action = 'auth.reauth_consumed'"));
    }

    [Fact]
    public async Task Reauth_token_expires_after_five_minutes()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var reauth = await Api.ReauthAsync(s, ReauthScopes.AccountPasswordChange);
        Factory.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(45));
        var refreshed = await Api.PostAsync("/v1/auth/refresh", new JsonObject { ["refresh_token"] = s.RefreshToken, ["device_id"] = s.DeviceId.ToString() });
        var access = refreshed.Json!["access_token"]!.GetValue<string>();

        var change = await Api.SendAsync(HttpMethod.Put, "/v1/me/password", new JsonObject { ["new_password"] = NewPassword }, access, r => r.Headers.Add("X-Reauth-Token", reauth));

        Assert.Equal(HttpStatusCode.Unauthorized, change.Status);
        Assert.Equal("REAUTH_REQUIRED", change.Code);
    }

    [Fact]
    public async Task Password_reset_ends_every_session_and_refresh_family_and_leaves_no_usable_reauth_token()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var second = await Api.LoginAsync(s.Email, s.Password);
        var reauth = await Api.ReauthAsync(s, ReauthScopes.AccountPasswordChange);
        await Forgot(s.Email);
        Assert.Equal(HttpStatusCode.NoContent, (await Reset(LastResetToken(s.Email))).Status);

        foreach (var session in new[] { s, second })
        {
            Assert.Equal("SESSION_REVOKED", (await Api.GetAsync("/v1/me", session.AccessToken)).Code);
            var refresh = await Api.PostAsync("/v1/auth/refresh", new JsonObject { ["refresh_token"] = session.RefreshToken, ["device_id"] = session.DeviceId.ToString() });
            Assert.Equal("SESSION_REVOKED", refresh.Code);
        }

        var viaOldReauth = await ChangePassword(s, reauth);
        Assert.Equal(HttpStatusCode.Unauthorized, viaOldReauth.Status);
        Assert.Equal(0, await AdminScalarAsync<long>("SELECT count(*) FROM nina.auth_session WHERE revoked_at IS NULL"));
    }

    // ------------------------------------------------------------ NR-03: o jti nasce no livro-razao do banco

    [Fact]
    public async Task NR03_the_reauth_jti_is_issued_to_the_ledger_with_user_session_scope_and_validity_and_then_consumed()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var token = await Api.ReauthAsync(s, ReauthScopes.AccountPasswordChange, ReauthScopes.IdentityLink);

        Assert.Equal(1, await AdminScalarAsync<long>("SELECT count(*) FROM nina.reauth_jti WHERE user_id = @u AND session_id = @s AND consumed_at IS NULL AND scopes = ARRAY['ACCOUNT_PASSWORD_CHANGE','IDENTITY_LINK']::text[] AND expires_at - issued_at <= interval '5 minutes'",
            new NpgsqlParameter("u", s.UserId), new NpgsqlParameter("s", s.SessionId)));
        Assert.Equal(HttpStatusCode.NoContent, (await ChangePassword(s, token)).Status);
        Assert.Equal("ACCOUNT_PASSWORD_CHANGE", await AdminScalarAsync<string>("SELECT consumed_scope FROM nina.reauth_jti WHERE user_id = @u", new NpgsqlParameter("u", s.UserId)));
    }

    [Fact]
    public async Task NR03_a_correctly_signed_token_whose_jti_was_never_issued_in_the_ledger_is_refused()
    {
        // Mesmo um JWT com assinatura valida (o que um atacante so teria com a chave) nao vale sem a linha de emissao assinada pelo servidor.
        var s = await Api.RegisterAndVerifyAsync();
        var tokens = Factory.Services.GetRequiredService<Nina.Identity.Services.TokenService>();
        var (rogue, _, _) = tokens.IssueReauthToken(s.UserId, s.SessionId, [ReauthScopes.AccountPasswordChange]);

        var response = await ChangePassword(s, rogue);

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        Assert.Equal("REAUTH_REQUIRED", response.Code);
        Assert.Equal(0, await AdminScalarAsync<long>("SELECT count(*) FROM nina.reauth_jti"));
        Assert.Equal(HttpStatusCode.OK, (await Api.PostAsync("/v1/auth/login", new JsonObject { ["email"] = s.Email, ["password"] = s.Password, ["device"] = ApiClient.Device() })).Status);   // senha intacta
    }

    [Fact]
    public async Task NR03_without_the_server_key_in_the_database_no_reauth_token_is_handed_out()
    {
        var s = await Api.RegisterAndVerifyAsync();
        await AdminExecAsync("DELETE FROM nina.server_key");

        var response = await Api.PostAsync("/v1/auth/reauthenticate", new JsonObject { ["password"] = s.Password, ["scope"] = new JsonArray(ReauthScopes.AccountPasswordChange) }, s.AccessToken);

        Assert.NotEqual(HttpStatusCode.OK, response.Status);      // sem a chave o banco recusa a emissao (NN070, falha fechada): nenhum token e entregue
        Assert.Null(response.Json?["reauth_token"]);
    }
}
