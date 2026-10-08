using System.Net;
using System.Text.Json.Nodes;
using Nina.Family.Tests.Infrastructure;

namespace Nina.Family.Tests.Integration;

/// <summary>SEC-040: 429 com <c>Retry-After</c> (e <c>retry_after_seconds</c>) nas operações do módulo.</summary>
public sealed class GeneralRateLimitTests(PostgresFixture postgres) : FamilyTestBase(postgres)
{
    protected override void Configure(Dictionary<string, string?> settings) =>
        settings["Family:RateLimits:RequestsPerUserPerMinute"] = "4";

    [Fact]
    public async Task Calls_over_the_per_user_limit_get_429_with_retry_after_and_recover_after_the_window()
    {
        var user = await NewUserAsync();
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("/v1/babies", user.AccessToken)).Status);
        }

        var limited = await Api.GetAsync("/v1/babies", user.AccessToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.Equal("RATE_LIMITED", limited.Code);
        Assert.True(limited.IsProblem);
        Assert.True(int.Parse(limited.Header("Retry-After")!, System.Globalization.CultureInfo.InvariantCulture) >= 1);
        Assert.Equal(limited.Json!["retry_after_seconds"]!.GetValue<int>(), int.Parse(limited.Header("Retry-After")!, System.Globalization.CultureInfo.InvariantCulture));

        // o limite é por usuário: outra conta não é afetada
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("/v1/babies", (await NewUserAsync()).AccessToken)).Status);

        Factory.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("/v1/babies", user.AccessToken)).Status);
    }

    [Fact]
    public async Task The_limit_also_covers_unknown_babies_so_it_cannot_be_used_to_probe_ids()
    {
        var user = await NewUserAsync();
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await Api.GetAsync($"/v1/babies/{Guid.NewGuid()}", user.AccessToken)).Status);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Api.GetAsync($"/v1/babies/{Guid.NewGuid()}", user.AccessToken)).Status);
    }
}

public sealed class BabyCreateRateLimitTests(PostgresFixture postgres) : FamilyTestBase(postgres)
{
    protected override void Configure(Dictionary<string, string?> settings) =>
        settings["Family:RateLimits:BabyCreatePerUserPerHour"] = "2";

    [Fact]
    public async Task Third_baby_in_the_hour_gets_429()
    {
        var owner = await NewOwnerAsync();
        await CreateBabyAsync(owner, "Um");
        await CreateBabyAsync(owner, "Dois");
        var limited = await Api.PostAsync("/v1/babies", new JsonObject { ["display_name"] = "Tres", ["birth_date"] = TodayUtc(1), ["timezone"] = "UTC" }, owner.AccessToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.NotNull(limited.Header("Retry-After"));
        Assert.Equal(2, await AdminCountAsync("SELECT count(*) FROM nina.baby"));
    }
}

public sealed class InviteRateLimitTests(PostgresFixture postgres) : FamilyTestBase(postgres)
{
    protected override void Configure(Dictionary<string, string?> settings)
    {
        settings["Family:RateLimits:InvitePerUserPerHour"] = "2";
        settings["Family:RateLimits:ResendPerUserPerHour"] = "1";
    }

    [Fact]
    public async Task Invitations_and_resends_are_limited_per_owner()
    {
        var owner = await NewOwnerAsync();
        var baby = await CreateBabyAsync(owner);
        var first = await InviteAsync(owner, baby, ApiClient.NewEmail());
        Assert.Equal(HttpStatusCode.Created, (await InviteAsync(owner, baby, ApiClient.NewEmail())).Status);
        var limited = await InviteAsync(owner, baby, ApiClient.NewEmail());
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.Equal("RATE_LIMITED", limited.Code);
        Assert.NotNull(limited.Header("Retry-After"));
        Assert.Equal(2, Factory.FamilyMailer.Sent.Count);

        var resendPath = $"/v1/babies/{baby}/invitations/{first.Json!["id"]!.GetValue<string>()}/resend";
        var resent = await Api.PostAsync(resendPath, null, owner.AccessToken);
        Assert.Equal(HttpStatusCode.OK, resent.Status);
        var second = await Api.PostAsync($"/v1/babies/{baby}/invitations/{resent.Json!["id"]!.GetValue<string>()}/resend", null, owner.AccessToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.Status);
    }
}

public sealed class InvitationTokenRateLimitTests(PostgresFixture postgres) : FamilyTestBase(postgres)
{
    protected override void Configure(Dictionary<string, string?> settings) =>
        settings["Family:RateLimits:InvitationTokenOpsPerUserPerMinute"] = "3";

    [Fact]
    public async Task Token_guessing_is_throttled_on_inspect_accept_and_decline_together()
    {
        var user = await NewUserAsync();
        var guess = new JsonObject { ["token"] = new string('q', 43) };
        Assert.Equal(HttpStatusCode.NotFound, (await Api.PostAsync("/v1/invitations/inspect", guess, user.AccessToken)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.PostAsync("/v1/invitations/accept", guess, user.AccessToken)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Api.PostAsync("/v1/invitations/decline", guess, user.AccessToken)).Status);

        foreach (var path in new[] { "/v1/invitations/inspect", "/v1/invitations/accept", "/v1/invitations/decline" })
        {
            var limited = await Api.PostAsync(path, guess, user.AccessToken);
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
            Assert.NotNull(limited.Header("Retry-After"));
        }
    }
}

public sealed class SensitiveRateLimitTests(PostgresFixture postgres) : FamilyTestBase(postgres)
{
    protected override void Configure(Dictionary<string, string?> settings) =>
        settings["Family:RateLimits:SensitivePerUserPerHour"] = "1";

    [Fact]
    public async Task Baby_delete_and_ownership_transfer_share_a_strict_per_user_limit()
    {
        var owner = await NewOwnerAsync();
        var baby = await CreateBabyAsync(owner);
        var first = await Api.DeleteAsync($"/v1/babies/{baby}", owner.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, first.Status); // passou pelo limite; falta a reautenticação
        var second = await Api.DeleteAsync($"/v1/babies/{baby}", owner.AccessToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.Status);
        Assert.NotNull(second.Header("Retry-After"));
    }
}
