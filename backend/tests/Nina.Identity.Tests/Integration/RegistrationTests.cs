using System.Net;
using System.Text.Json.Nodes;
using Nina.Identity.Mail;
using Nina.Identity.Tests.Infrastructure;
using Npgsql;

namespace Nina.Identity.Tests.Integration;

/// <summary>RF-001, RF-003-A1, AD-03: cadastro com 202 uniforme, confirmação por e-mail e anti-enumeração.</summary>
public sealed class RegistrationTests(PostgresFixture postgres) : IntegrationTestBase(postgres)
{
    private static JsonObject RegisterBody(string email, string password = TestConstants.GoodPassword, JsonArray? consents = null) => new()
    {
        ["email"] = email,
        ["password"] = password,
        ["display_name"] = "Ana",
        ["locale"] = "pt-BR",
        ["timezone"] = "America/Sao_Paulo",
        ["consents"] = consents ?? ApiClient.Consents(),
    };

    [Fact]
    public async Task Register_returns_identical_202_for_new_unverified_and_verified_email()
    {
        var email = ApiClient.NewEmail();
        var first = await Api.PostAsync("/v1/auth/register", RegisterBody(email));
        var again = await Api.PostAsync("/v1/auth/register", RegisterBody(email));
        await Api.VerifyAsync(email, Api.LastCode(email));
        var verified = await Api.PostAsync("/v1/auth/register", RegisterBody(email));

        Assert.Equal(HttpStatusCode.Accepted, first.Status);
        Assert.Equal("""{"status":"VERIFICATION_PENDING","resend_after_seconds":60}""", first.Json!.ToJsonString());
        foreach (var other in new[] { again, verified })
        {
            Assert.Equal(first.Status, other.Status);
            Assert.Equal(first.Json.ToJsonString(), other.Json!.ToJsonString());
            Assert.Equal(first.ContentType, other.ContentType);
        }

        var count = await AdminScalarAsync<long>("SELECT count(*) FROM nina.app_user WHERE email_normalized = @e", new NpgsqlParameter("e", email));
        Assert.Equal(1, count);
        Assert.Contains(Factory.Mailer.Sent, m => m.To == email && m.Kind == MailKind.AlreadyRegistered);
    }

    [Fact]
    public async Task Register_does_not_create_an_active_session_or_login_before_confirmation()
    {
        var email = ApiClient.NewEmail();
        await Api.PostAsync("/v1/auth/register", RegisterBody(email));
        var login = await Api.PostAsync("/v1/auth/login", new JsonObject
        {
            ["email"] = email,
            ["password"] = TestConstants.GoodPassword,
            ["device"] = ApiClient.Device(),
        });

        Assert.Equal(HttpStatusCode.Unauthorized, login.Status);
        Assert.Equal("INVALID_CREDENTIALS", login.Code);
        Assert.Equal(0, await AdminScalarAsync<long>("SELECT count(*) FROM nina.auth_session"));
        Assert.Null(await AdminScalarAsync<DateTime?>("SELECT email_verified_at FROM nina.app_user WHERE email_normalized = @e", new NpgsqlParameter("e", email)));
    }

    [Fact]
    public async Task Register_stores_hash_not_password_and_records_both_consents()
    {
        var email = ApiClient.NewEmail();
        await Api.PostAsync("/v1/auth/register", RegisterBody(email));

        var hash = await AdminScalarAsync<string>("SELECT c.password_hash FROM nina.user_credential c JOIN nina.app_user u ON u.id = c.user_id WHERE u.email_normalized = @e", new NpgsqlParameter("e", email));
        Assert.StartsWith("$argon2id$v=19$", hash, StringComparison.Ordinal);
        Assert.DoesNotContain(TestConstants.GoodPassword, hash, StringComparison.Ordinal);
        var consents = await AdminScalarAsync<long>(
            "SELECT count(*) FROM nina.consent_record r JOIN nina.app_user u ON u.id = r.user_id WHERE u.email_normalized = @e AND r.status = 'GRANTED' AND r.source = 'ONBOARDING'",
            new NpgsqlParameter("e", email));
        Assert.Equal(2, consents);
    }

    [Fact]
    public async Task Register_normalizes_email_case_and_does_not_create_a_second_account()
    {
        var email = ApiClient.NewEmail();
        await Api.PostAsync("/v1/auth/register", RegisterBody(email));
        await Api.PostAsync("/v1/auth/register", RegisterBody("  " + email.ToUpperInvariant() + " "));

        Assert.Equal(1, await AdminScalarAsync<long>("SELECT count(*) FROM nina.app_user WHERE email_normalized = @e", new NpgsqlParameter("e", email)));
    }

    [Theory]
    [InlineData("not-an-email", "INVALID_FORMAT")]
    [InlineData("", "REQUIRED")]
    public async Task Register_rejects_invalid_email(string email, string expectedCode)
    {
        var response = await Api.PostAsync("/v1/auth/register", RegisterBody(email));

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.True(response.IsProblem);
        Assert.Equal("VALIDATION_FAILED", response.Code);
        Assert.Equal(expectedCode, response.FieldErrorCode("email"));
    }

    [Theory]
    [InlineData("short", "TOO_SHORT")]
    [InlineData("password123", "TOO_COMMON")]
    [InlineData("aaaaaaaaaaaa", "TOO_COMMON")]
    public async Task Register_enforces_the_password_policy(string password, string reason)
    {
        var response = await Api.PostAsync("/v1/auth/register", RegisterBody(ApiClient.NewEmail(), password));

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal("PASSWORD_POLICY", response.FieldErrorCode("password"));
        Assert.Equal(reason, response.Json!["errors"]![0]!["meta"]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Register_rejects_passwords_above_128_characters_and_unknown_properties()
    {
        var tooLong = await Api.PostAsync("/v1/auth/register", RegisterBody(ApiClient.NewEmail(), new string('x', 129)));
        var extra = RegisterBody(ApiClient.NewEmail());
        extra["is_admin"] = true;
        var unknown = await Api.PostAsync("/v1/auth/register", extra);

        Assert.Equal("TOO_LONG", tooLong.FieldErrorCode("password"));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.Status);
        Assert.Equal("VALIDATION_FAILED", unknown.Code);
    }

    [Fact]
    public async Task Register_accepts_a_locale_in_any_case_and_stores_the_language_in_lowercase()
    {
        var email = ApiClient.NewEmail();
        var body = RegisterBody(email);
        body["locale"] = "PT-br";
        var response = await Api.PostAsync("/v1/auth/register", body);

        Assert.Equal(HttpStatusCode.Accepted, response.Status);
        Assert.Equal("pt-br", await AdminScalarAsync<string>("SELECT locale FROM nina.app_user WHERE email_normalized = @e", new NpgsqlParameter("e", email)));
    }

    [Fact]
    public async Task Register_requires_terms_and_privacy_consents_at_the_current_version()
    {
        var onlyTerms = new JsonArray(new JsonObject { ["purpose_key"] = "TERMS_OF_USE", ["document_version"] = "1.0.0" });
        var missing = await Api.PostAsync("/v1/auth/register", RegisterBody(ApiClient.NewEmail(), consents: onlyTerms));
        var stale = await Api.PostAsync("/v1/auth/register", RegisterBody(ApiClient.NewEmail(), consents: ApiClient.Consents("0.9.0")));

        Assert.Equal("REQUIRED", missing.FieldErrorCode("consents"));
        Assert.Equal("STALE_VERSION", stale.FieldErrorCode("consents[0].document_version"));
        Assert.Equal(0, await AdminScalarAsync<long>("SELECT count(*) FROM nina.app_user"));
    }

    [Fact]
    public async Task Register_rejects_unknown_timezone_without_creating_the_account()
    {
        var body = RegisterBody(ApiClient.NewEmail());
        body["timezone"] = "Mars/Olympus_Mons";
        var response = await Api.PostAsync("/v1/auth/register", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal("VALIDATION_FAILED", response.Code);
        Assert.Equal(0, await AdminScalarAsync<long>("SELECT count(*) FROM nina.app_user"));
    }

    [Fact]
    public async Task Register_is_rate_limited_per_email()
    {
        var email = ApiClient.NewEmail();
        for (var i = 0; i < 5; i++)
        {
            await Api.PostAsync("/v1/auth/register", RegisterBody(email));
            Factory.Time.Advance(TimeSpan.FromSeconds(61));
        }

        var sentBefore = Factory.Mailer.Sent.Count;
        var sixth = await Api.PostAsync("/v1/auth/register", RegisterBody(email));

        // Por e-mail o limite é silencioso (resposta uniforme, sem novo envio).
        Assert.Equal(HttpStatusCode.Accepted, sixth.Status);
        Assert.Equal(sentBefore, Factory.Mailer.Sent.Count);
    }

    [Fact]
    public async Task Register_is_rate_limited_per_ip_with_429_and_retry_after()
    {
        HttpStatusCode last = default;
        ApiResponse? limited = null;
        for (var i = 0; i < 25 && limited is null; i++)
        {
            var r = await Api.PostAsync("/v1/auth/register", RegisterBody(ApiClient.NewEmail()), tweak: req => req.Headers.Add("X-Forwarded-For", "198.51.100.7"));
            last = r.Status;
            if (r.Status == HttpStatusCode.TooManyRequests)
            {
                limited = r;
            }
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
        Assert.Equal("RATE_LIMITED", limited!.Code);
        Assert.True(limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds > 0);
        Assert.True(limited.Json!["retry_after_seconds"]!.GetValue<int>() > 0);
    }

    [Fact]
    public async Task Verify_issues_session_and_activates_the_account()
    {
        var email = ApiClient.NewEmail();
        await Api.PostAsync("/v1/auth/register", RegisterBody(email));
        var device = ApiClient.Device();
        var verify = await Api.PostAsync("/v1/auth/email/verify", new JsonObject { ["email"] = email, ["code"] = Api.LastCode(email), ["device"] = device });

        Assert.Equal(HttpStatusCode.OK, verify.Status);
        var json = verify.Json!;
        Assert.Equal("Bearer", json["token_type"]!.GetValue<string>());
        Assert.InRange(json["expires_in"]!.GetValue<int>(), 1, 900);
        Assert.StartsWith("rt_", json["refresh_token"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotNull(json["refresh_expires_at"]);
        Assert.Equal("ACTIVE", json["user"]!["status"]!.GetValue<string>());
        Assert.True(json["user"]!["email_verified"]!.GetValue<bool>());
        Assert.True(json["user"]!["has_password"]!.GetValue<bool>());
        Assert.Empty(json["pending_consents"]!.AsArray());
    }

    [Fact]
    public async Task Verify_code_is_single_use_and_expires()
    {
        var email = ApiClient.NewEmail();
        await Api.PostAsync("/v1/auth/register", RegisterBody(email));
        var code = Api.LastCode(email);
        await Api.VerifyAsync(email, code);

        var reuse = await Api.PostAsync("/v1/auth/email/verify", new JsonObject { ["email"] = email, ["code"] = code, ["device"] = ApiClient.Device() });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.Status);
        Assert.Equal("INVALID_VERIFICATION_CODE", reuse.Code);

        var other = ApiClient.NewEmail();
        await Api.PostAsync("/v1/auth/register", RegisterBody(other));
        var otherCode = Api.LastCode(other);
        Factory.Time.Advance(TimeSpan.FromMinutes(16));
        var expired = await Api.PostAsync("/v1/auth/email/verify", new JsonObject { ["email"] = other, ["code"] = otherCode, ["device"] = ApiClient.Device() });
        Assert.Equal(HttpStatusCode.Unauthorized, expired.Status);
    }

    [Fact]
    public async Task Verify_locks_out_after_repeated_wrong_codes_even_for_the_right_code()
    {
        var email = ApiClient.NewEmail();
        await Api.PostAsync("/v1/auth/register", RegisterBody(email));
        var code = Api.LastCode(email);
        var wrong = code == "00000000" ? "11111111" : "00000000";
        for (var i = 0; i < 5; i++)
        {
            var r = await Api.PostAsync("/v1/auth/email/verify", new JsonObject { ["email"] = email, ["code"] = wrong, ["device"] = ApiClient.Device() });
            Assert.Equal(HttpStatusCode.Unauthorized, r.Status);
        }

        var locked = await Api.PostAsync("/v1/auth/email/verify", new JsonObject { ["email"] = email, ["code"] = code, ["device"] = ApiClient.Device() });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.Status);
        Assert.NotNull(locked.Headers.RetryAfter);
    }

    [Fact]
    public async Task Verify_invalidates_the_code_after_five_wrong_attempts_even_once_the_lockout_ends()
    {
        var email = ApiClient.NewEmail();
        await Api.PostAsync("/v1/auth/register", RegisterBody(email));
        var code = Api.LastCode(email);
        var wrong = code == "00000000" ? "11111111" : "00000000";
        for (var i = 0; i < 5; i++)
        {
            await Api.PostAsync("/v1/auth/email/verify", new JsonObject { ["email"] = email, ["code"] = wrong, ["device"] = ApiClient.Device() });
        }

        Factory.Time.Advance(TimeSpan.FromMinutes(16));
        var reuse = await Api.PostAsync("/v1/auth/email/verify", new JsonObject { ["email"] = email, ["code"] = code, ["device"] = ApiClient.Device() });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.Status);
        Assert.Equal("INVALID_VERIFICATION_CODE", reuse.Code);

        // É preciso pedir outro código; esse, sim, funciona.
        await Api.PostAsync("/v1/auth/register", RegisterBody(email));
        await Api.VerifyAsync(email, Api.LastCode(email));
    }

    [Fact]
    public async Task Verify_rejects_malformed_codes_and_unknown_properties()
    {
        var email = ApiClient.NewEmail();
        var letters = await Api.PostAsync("/v1/auth/email/verify", new JsonObject { ["email"] = email, ["code"] = "abcdefgh", ["device"] = ApiClient.Device() });
        var tooShort = await Api.PostAsync("/v1/auth/email/verify", new JsonObject { ["email"] = email, ["code"] = "12345", ["device"] = ApiClient.Device() });
        var extra = await Api.PostAsync("/v1/auth/email/verify", new JsonObject { ["email"] = email, ["code"] = "12345678", ["device"] = ApiClient.Device(), ["x"] = 1 });

        Assert.Equal("INVALID_FORMAT", letters.FieldErrorCode("code"));
        Assert.Equal("INVALID_FORMAT", tooShort.FieldErrorCode("code"));
        Assert.Equal(HttpStatusCode.BadRequest, extra.Status);
    }

    [Fact]
    public async Task Verification_code_has_eight_digits_and_expires_after_ten_minutes()
    {
        var email = ApiClient.NewEmail();
        await Api.PostAsync("/v1/auth/register", RegisterBody(email));
        var code = Api.LastCode(email);
        Factory.Time.Advance(TimeSpan.FromMinutes(11));

        var expired = await Api.PostAsync("/v1/auth/email/verify", new JsonObject { ["email"] = email, ["code"] = code, ["device"] = ApiClient.Device() });

        Assert.Matches("^[0-9]{8}$", code);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.Status);
    }

    [Fact]
    public async Task Verify_for_unknown_email_is_indistinguishable_from_wrong_code()
    {
        var email = ApiClient.NewEmail();
        await Api.PostAsync("/v1/auth/register", RegisterBody(email));
        var wrongCode = await Api.PostAsync("/v1/auth/email/verify", new JsonObject { ["email"] = email, ["code"] = "12345678", ["device"] = ApiClient.Device() });
        var unknown = await Api.PostAsync("/v1/auth/email/verify", new JsonObject { ["email"] = ApiClient.NewEmail(), ["code"] = "12345678", ["device"] = ApiClient.Device() });

        Assert.Equal(wrongCode.Status, unknown.Status);
        Assert.Equal(wrongCode.Code, unknown.Code);
    }

    [Fact]
    public async Task Reregistering_before_confirmation_replaces_password_and_invalidates_the_old_code()
    {
        var email = ApiClient.NewEmail();
        await Api.PostAsync("/v1/auth/register", RegisterBody(email));
        var oldCode = Api.LastCode(email);

        // Dentro do intervalo de reenvio nada muda (nem novo e-mail).
        var sent = Factory.Mailer.Sent.Count;
        await Api.PostAsync("/v1/auth/register", RegisterBody(email, "another-long-password-1"));
        Assert.Equal(sent, Factory.Mailer.Sent.Count);

        Factory.Time.Advance(TimeSpan.FromSeconds(61));
        await Api.PostAsync("/v1/auth/register", RegisterBody(email, "another-long-password-2"));
        var newCode = Api.LastCode(email);

        var stale = await Api.PostAsync("/v1/auth/email/verify", new JsonObject { ["email"] = email, ["code"] = oldCode, ["device"] = ApiClient.Device() });
        Assert.Equal(HttpStatusCode.Unauthorized, stale.Status);
        await Api.VerifyAsync(email, newCode);
        await Api.LoginAsync(email, "another-long-password-2");
        var oldPassword = await Api.PostAsync("/v1/auth/login", new JsonObject { ["email"] = email, ["password"] = TestConstants.GoodPassword, ["device"] = ApiClient.Device() });
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.Status);
    }
}
