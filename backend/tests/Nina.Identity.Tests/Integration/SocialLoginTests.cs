using System.Net;
using System.Text.Json.Nodes;
using Nina.Identity.Tests.Infrastructure;
using Npgsql;

namespace Nina.Identity.Tests.Integration;

/// <summary>ADR-0007: Google/Apple (validação do id_token atrás de interface, com fake), sem fusão automática por e-mail.</summary>
public sealed class SocialLoginTests(PostgresFixture postgres) : IntegrationTestBase(postgres)
{
    private static JsonObject Body(string provider, string subject, string? email, bool verified = true, string nonce = "nonce-0001", JsonArray? consents = null, string tokenNonce = "nonce-0001")
    {
        var body = new JsonObject
        {
            ["id_token"] = FakeIdentityTokenVerifier.Token(provider, subject, email, verified, tokenNonce),
            ["nonce"] = nonce,
            ["device"] = ApiClient.Device(),
        };
        if (consents is not null)
        {
            body["consents"] = consents;
        }

        return body;
    }

    [Theory]
    [InlineData("google", "GOOGLE")]
    [InlineData("apple", "APPLE")]
    public async Task New_account_without_consents_gets_403_CONSENT_REQUIRED_and_nothing_is_created(string path, string provider)
    {
        var response = await Api.PostAsync($"/v1/auth/{path}", Body(provider, "sub-1", ApiClient.NewEmail()));

        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Equal("CONSENT_REQUIRED", response.Code);
        var required = response.Json!["required_consents"]!.AsArray().Select(c => c!["purpose_key"]!.GetValue<string>()).ToList();
        Assert.Contains("TERMS_OF_USE", required);
        Assert.Contains("PRIVACY_POLICY", required);
        Assert.Equal(0, await AdminScalarAsync<long>("SELECT count(*) FROM nina.app_user"));
    }

    [Theory]
    [InlineData("google", "GOOGLE")]
    [InlineData("apple", "APPLE")]
    public async Task New_account_with_consents_is_created_verified_and_logged_in(string path, string provider)
    {
        var email = ApiClient.NewEmail();
        var response = await Api.PostAsync($"/v1/auth/{path}", Body(provider, "sub-1", email, consents: ApiClient.Consents()));

        Assert.Equal(HttpStatusCode.OK, response.Status);
        var user = response.Json!["user"]!;
        Assert.Equal(email, user["email"]!.GetValue<string>());
        Assert.True(user["email_verified"]!.GetValue<bool>());
        Assert.False(user["has_password"]!.GetValue<bool>());
        Assert.Equal(provider, user["identities"]![0]!["provider"]!.GetValue<string>());
        Assert.Equal(2, await AdminScalarAsync<long>("SELECT count(*) FROM nina.consent_record WHERE status = 'GRANTED'"));
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("/v1/me", response.Json["access_token"]!.GetValue<string>())).Status);
    }

    [Fact]
    public async Task Returning_user_logs_in_by_subject_even_if_the_provider_email_changed()
    {
        var first = await Api.PostAsync("/v1/auth/google", Body("GOOGLE", "sub-stable", ApiClient.NewEmail(), consents: ApiClient.Consents()));
        var second = await Api.PostAsync("/v1/auth/google", Body("GOOGLE", "sub-stable", ApiClient.NewEmail()));

        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Equal(first.Json!["user"]!["id"]!.ToString(), second.Json!["user"]!["id"]!.ToString());
        Assert.Equal(1, await AdminScalarAsync<long>("SELECT count(*) FROM nina.app_user"));
    }

    [Fact]
    public async Task Verified_email_of_an_existing_password_account_is_not_merged()
    {
        var existing = await Api.RegisterAndVerifyAsync();

        var response = await Api.PostAsync("/v1/auth/google", Body("GOOGLE", "sub-attacker", existing.Email, consents: ApiClient.Consents()));

        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        Assert.Equal("IDENTITY_LINK_REQUIRED", response.Code);
        Assert.Equal(0, await AdminScalarAsync<long>("SELECT count(*) FROM nina.user_identity"));
    }

    [Fact]
    public async Task Unverified_pending_registration_is_not_taken_over_by_a_social_login()
    {
        var email = ApiClient.NewEmail();
        await Api.PostAsync("/v1/auth/register", new JsonObject
        {
            ["email"] = email,
            ["password"] = TestConstants.GoodPassword,
            ["locale"] = "pt-BR",
            ["consents"] = ApiClient.Consents(),
        });

        var response = await Api.PostAsync("/v1/auth/apple", Body("APPLE", "sub-x", email, consents: ApiClient.Consents()));

        Assert.Equal("IDENTITY_LINK_REQUIRED", response.Code);
    }

    [Fact]
    public async Task Invalid_token_wrong_nonce_unverified_email_and_missing_email_are_401()
    {
        var invalid = await Api.PostAsync("/v1/auth/google", new JsonObject { ["id_token"] = "garbage-token-0123456789", ["nonce"] = "nonce-0001", ["device"] = ApiClient.Device() });
        var nonce = await Api.PostAsync("/v1/auth/google", Body("GOOGLE", "s", ApiClient.NewEmail(), nonce: "sent-nonce-1", tokenNonce: "other-nonce-2", consents: ApiClient.Consents()));
        var unverified = await Api.PostAsync("/v1/auth/google", Body("GOOGLE", "s2", ApiClient.NewEmail(), verified: false, consents: ApiClient.Consents()));
        var noEmail = await Api.PostAsync("/v1/auth/apple", Body("APPLE", "s3", null, consents: ApiClient.Consents()));
        var wrongProvider = await Api.PostAsync("/v1/auth/apple", Body("GOOGLE", "s4", ApiClient.NewEmail(), consents: ApiClient.Consents()));

        foreach (var r in new[] { invalid, nonce, unverified, noEmail, wrongProvider })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r.Status);
            Assert.Equal("INVALID_ID_TOKEN", r.Code);
        }

        Assert.Equal(0, await AdminScalarAsync<long>("SELECT count(*) FROM nina.app_user"));
    }

    [Fact]
    public async Task Social_login_is_rate_limited_per_ip()
    {
        ApiResponse? last = null;
        for (var i = 0; i < 40; i++)
        {
            last = await Api.PostAsync("/v1/auth/google", new JsonObject { ["id_token"] = "garbage-token-0123456789", ["nonce"] = "nonce-0001", ["device"] = ApiClient.Device() }, tweak: r => r.Headers.Add("X-Forwarded-For", "192.0.2.50"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.Status);
    }

    [Fact]
    public async Task Link_identity_requires_reauth_and_then_the_provider_login_reaches_the_same_account()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var link = new JsonObject { ["provider"] = "GOOGLE", ["id_token"] = FakeIdentityTokenVerifier.Token("GOOGLE", "sub-link", s.Email, true, "nonce-link-9"), ["nonce"] = "nonce-link-9" };

        var denied = await Api.SendAsync(HttpMethod.Post, "/v1/me/identities", link, s.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.Status);
        Assert.Equal("REAUTH_REQUIRED", denied.Code);

        var token = await Api.ReauthAsync(s, "IDENTITY_LINK");
        var linked = await Api.SendAsync(HttpMethod.Post, "/v1/me/identities", link, s.AccessToken, r => r.Headers.Add("X-Reauth-Token", token));
        Assert.Equal(HttpStatusCode.OK, linked.Status);
        Assert.Equal("GOOGLE", linked.Json!["identities"]![0]!["provider"]!.GetValue<string>());

        var login = await Api.PostAsync("/v1/auth/google", Body("GOOGLE", "sub-link", s.Email));
        Assert.Equal(HttpStatusCode.OK, login.Status);
        Assert.Equal(s.UserId.ToString(), login.Json!["user"]!["id"]!.GetValue<string>());

        var reused = await Api.SendAsync(HttpMethod.Post, "/v1/me/identities", link, s.AccessToken, r => r.Headers.Add("X-Reauth-Token", token));
        Assert.Equal("REAUTH_REQUIRED", reused.Code); // uso único

        var token2 = await Api.ReauthAsync(s, "IDENTITY_LINK");
        var again = await Api.SendAsync(HttpMethod.Post, "/v1/me/identities", link, s.AccessToken, r => r.Headers.Add("X-Reauth-Token", token2));
        Assert.Equal(HttpStatusCode.Conflict, again.Status);
        Assert.Equal("IDENTITY_ALREADY_LINKED", again.Code);
    }

    [Fact]
    public async Task Link_identity_that_belongs_to_another_account_is_a_conflict()
    {
        var owner = await Api.PostAsync("/v1/auth/google", Body("GOOGLE", "sub-owned", ApiClient.NewEmail(), consents: ApiClient.Consents()));
        Assert.Equal(HttpStatusCode.OK, owner.Status);
        var s = await Api.RegisterAndVerifyAsync();
        var reauth = await Api.ReauthAsync(s, "IDENTITY_LINK");

        var response = await Api.SendAsync(
            HttpMethod.Post, "/v1/me/identities",
            new JsonObject { ["provider"] = "GOOGLE", ["id_token"] = FakeIdentityTokenVerifier.Token("GOOGLE", "sub-owned", "x@example.org", true, "nonce-0001"), ["nonce"] = "nonce-0001" },
            s.AccessToken, r => r.Headers.Add("X-Reauth-Token", reauth));

        Assert.Equal("IDENTITY_IN_USE", response.Code);
    }

    [Fact]
    public async Task Reauthenticate_with_the_linked_provider_works_and_a_foreign_identity_does_not()
    {
        var created = await Api.PostAsync("/v1/auth/apple", Body("APPLE", "sub-re", ApiClient.NewEmail(), consents: ApiClient.Consents()));
        var access = created.Json!["access_token"]!.GetValue<string>();

        var ok = await Api.PostAsync("/v1/auth/reauthenticate", new JsonObject { ["provider"] = "APPLE", ["id_token"] = FakeIdentityTokenVerifier.Token("APPLE", "sub-re", null, true, "reauth-001"), ["nonce"] = "reauth-001" }, access);
        var foreign = await Api.PostAsync("/v1/auth/reauthenticate", new JsonObject { ["provider"] = "APPLE", ["id_token"] = FakeIdentityTokenVerifier.Token("APPLE", "someone-else", null, true, "reauth-002"), ["nonce"] = "reauth-002" }, access);

        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, foreign.Status);
        Assert.Equal(1, await AdminScalarAsync<long>("SELECT count(*) FROM nina.user_identity WHERE provider_subject = @s", new NpgsqlParameter("s", "sub-re")));
    }
}
