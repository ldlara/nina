using System.Net;
using System.Text.Json.Nodes;
using Nina.Identity.Tests.Infrastructure;
using Npgsql;

namespace Nina.Identity.Tests.Integration;

/// <summary>RF-003, INV-27: consentimentos versionados, append-only e re-aceite quando o documento muda.</summary>
public sealed class ConsentTests(PostgresFixture postgres) : IntegrationTestBase(postgres)
{
    private static JsonObject Input(string purpose, string status, string version = "1.0.0", string source = "SETTINGS") =>
        new() { ["purpose_key"] = purpose, ["document_version"] = version, ["status"] = status, ["source"] = source };

    [Fact]
    public async Task Legal_documents_are_public_and_list_the_current_versions()
    {
        var response = await Api.GetAsync("/v1/legal/documents");

        Assert.Equal(HttpStatusCode.OK, response.Status);
        var items = response.Json!["items"]!.AsArray();
        var terms = items.Single(i => i!["purpose_key"]!.GetValue<string>() == "TERMS_OF_USE")!;
        Assert.Equal("1.0.0", terms["version"]!.GetValue<string>());
        Assert.True(terms["required"]!.GetValue<bool>());
        Assert.StartsWith("https://", terms["url"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", terms["content_hash"]!.GetValue<string>());
    }

    [Fact]
    public async Task Consents_after_signup_show_terms_and_privacy_as_granted_with_nothing_pending()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var response = await Api.GetAsync("/v1/me/consents", s.AccessToken);

        var current = response.Json!["current"]!.AsArray();
        Assert.Equal(["PRIVACY_POLICY", "TERMS_OF_USE"], current.Select(c => c!["purpose_key"]!.GetValue<string>()).Order().ToArray());
        Assert.All(current, c => Assert.Equal("GRANTED", c!["status"]!.GetValue<string>()));
        Assert.Empty(response.Json["pending_required"]!.AsArray());
        Assert.Null(response.Json["history"]);
    }

    [Fact]
    public async Task Grant_then_revoke_appends_records_and_keeps_history()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var granted = await Api.PostAsync("/v1/me/consents", Input("ANALYTICS_PRODUCT", "GRANTED"), s.AccessToken);
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        var revoked = await Api.PostAsync("/v1/me/consents", Input("ANALYTICS_PRODUCT", "REVOKED"), s.AccessToken);

        Assert.Equal(HttpStatusCode.Created, granted.Status);
        Assert.Equal("GRANTED", granted.Json!["status"]!.GetValue<string>());
        Assert.Null(granted.Json["revoked_at"]);
        Assert.Equal(HttpStatusCode.Created, revoked.Status);
        Assert.NotNull(revoked.Json!["revoked_at"]);

        var list = await Api.GetAsync("/v1/me/consents?include_history=true", s.AccessToken);
        var analyticsCurrent = list.Json!["current"]!.AsArray().Single(c => c!["purpose_key"]!.GetValue<string>() == "ANALYTICS_PRODUCT")!;
        Assert.Equal("REVOKED", analyticsCurrent["status"]!.GetValue<string>());
        var history = list.Json["history"]!.AsArray().Where(c => c!["purpose_key"]!.GetValue<string>() == "ANALYTICS_PRODUCT").ToList();
        Assert.Equal(["REVOKED", "SUPERSEDED"], history.Select(h => h!["status"]!.GetValue<string>()).ToArray());
        Assert.Equal(2, await AdminScalarAsync<long>("SELECT count(*) FROM nina.consent_record WHERE purpose_key = 'analytics_product'"));
    }

    [Fact]
    public async Task Consent_records_cannot_be_updated_or_deleted_even_by_the_application_role()
    {
        var s = await Api.RegisterAndVerifyAsync();
        await using var connection = new NpgsqlConnection(Database.AppConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand($"SET LOCAL nina.user_id = '{s.UserId}'", connection, tx))
        {
            await set.ExecuteNonQueryAsync();
        }

        await using var update = new NpgsqlCommand("UPDATE nina.consent_record SET status = 'REVOKED'", connection, tx);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Equal("NN030", ex.SqlState);
    }

    [Theory]
    [InlineData("TERMS_OF_USE", "REVOKED", "1.0.0", "status", "REVOCATION_NOT_ALLOWED")]
    [InlineData("ANALYTICS_PRODUCT", "GRANTED", "0.1.0", "document_version", "STALE_VERSION")]
    [InlineData("NOT_A_PURPOSE", "GRANTED", "1.0.0", "purpose_key", "UNSUPPORTED_VALUE")]
    [InlineData("ANALYTICS_PRODUCT", "MAYBE", "1.0.0", "status", "UNSUPPORTED_VALUE")]
    [InlineData("CHILD_DATA_GUARDIAN", "GRANTED", "1.0.0", "baby_id", "REQUIRED")]
    public async Task Record_consent_validates_input(string purpose, string status, string version, string field, string code)
    {
        var s = await Api.RegisterAndVerifyAsync();
        var response = await Api.PostAsync("/v1/me/consents", Input(purpose, status, version), s.AccessToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal(code, response.FieldErrorCode(field));
    }

    [Fact]
    public async Task Baby_scoped_consent_for_an_unknown_baby_is_404()
    {
        var s = await Api.RegisterAndVerifyAsync();
        var input = Input("CHILD_DATA_GUARDIAN", "GRANTED");
        input["baby_id"] = Guid.NewGuid().ToString();

        var response = await Api.PostAsync("/v1/me/consents", input, s.AccessToken);

        Assert.Equal(HttpStatusCode.NotFound, response.Status);
    }

    [Fact]
    public async Task A_new_document_version_makes_consent_pending_until_the_user_accepts_it()
    {
        var s = await Api.RegisterAndVerifyAsync();
        await AdminExecAsync("UPDATE nina.consent_purpose SET current_version = '2.0.0' WHERE purpose_key = 'privacy_policy'");

        var list = await Api.GetAsync("/v1/me/consents", s.AccessToken);
        var pending = list.Json!["pending_required"]!.AsArray();
        Assert.Equal("PRIVACY_POLICY", Assert.Single(pending)!["purpose_key"]!.GetValue<string>());
        Assert.Equal("2.0.0", pending[0]!["version"]!.GetValue<string>());

        var login = await Api.PostAsync("/v1/auth/login", new JsonObject { ["email"] = s.Email, ["password"] = s.Password, ["device"] = ApiClient.Device() });
        var pendingInToken = login.Json!["pending_consents"]!.AsArray();
        Assert.Equal("PRIVACY_POLICY", Assert.Single(pendingInToken)!["purpose_key"]!.GetValue<string>());

        var accept = await Api.PostAsync("/v1/me/consents", Input("PRIVACY_POLICY", "GRANTED", "2.0.0", "PROMPT"), s.AccessToken);
        Assert.Equal(HttpStatusCode.Created, accept.Status);
        var after = await Api.GetAsync("/v1/me/consents?include_history=true", s.AccessToken);
        Assert.Empty(after.Json!["pending_required"]!.AsArray());
        Assert.Equal(2, after.Json["history"]!.AsArray().Count(c => c!["purpose_key"]!.GetValue<string>() == "PRIVACY_POLICY"));
    }

    [Fact]
    public async Task Users_only_see_their_own_consents()
    {
        var a = await Api.RegisterAndVerifyAsync();
        var b = await Api.RegisterAndVerifyAsync();
        await Api.PostAsync("/v1/me/consents", Input("MARKETING_EMAIL", "GRANTED"), a.AccessToken);

        var listB = await Api.GetAsync("/v1/me/consents?include_history=true", b.AccessToken);

        Assert.DoesNotContain(listB.Json!["history"]!.AsArray(), c => c!["purpose_key"]!.GetValue<string>() == "MARKETING_EMAIL");
    }

    [Fact]
    public async Task Consent_changes_are_audited_without_pii()
    {
        var s = await Api.RegisterAndVerifyAsync();
        await Api.PostAsync("/v1/me/consents", Input("ANALYTICS_PRODUCT", "GRANTED"), s.AccessToken);

        Assert.True(await AdminScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE action IN ('consent.granted','consent.revoked')") >= 3);
        Assert.Equal(0, await AdminScalarAsync<long>("SELECT count(*) FROM nina.audit_event WHERE metadata_safe::text ILIKE '%@%'"));
    }
}
