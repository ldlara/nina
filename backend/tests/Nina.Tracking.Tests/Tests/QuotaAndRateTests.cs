using System.Net;
using Nina.Tracking.Tests.Infrastructure;

namespace Nina.Tracking.Tests.Tests;

/// <summary>SEC-040/042, AD-17/AD-33: cota diária por bebê (429), teto de entidades vivas (por mutação) e limite de taxa.</summary>
public sealed class QuotaTests(PostgresFixture postgres) : TrackingTestBase(postgres)
{
    [Fact]
    public async Task The_daily_quota_rejects_the_whole_request_with_429_QUOTA_EXCEEDED_and_Retry_After()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        await SetParameterAsync("limits.sync_max_events_per_baby_per_day", "3", "INT");
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("APPLIED", (await PushOkAsync(ana, Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0.AddMinutes(i)))))[0].Status());
        }

        var blocked = await PushAsync(ana, Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0)), Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0)));
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.Status);
        Assert.Equal("QUOTA_EXCEEDED", blocked.Code);
        Assert.True(blocked.Headers.RetryAfter!.Delta!.Value.TotalSeconds >= 1);
        Assert.Equal(blocked.Json!["retry_after_seconds"]!.GetValue<int>(), (int)blocked.Headers.RetryAfter.Delta.Value.TotalSeconds);
        OpenApiContract.AssertValid("Problem", blocked.Json);
        Assert.Equal(3L, await AdminScalarAsync<long>($"SELECT count(*) FROM nina.diaper_event WHERE baby_id = '{baby}'"));

        // a cota é por bebê: outro bebê do mesmo usuário segue livre
        var other = await BabyAsync(ana);
        Assert.Equal("APPLIED", (await PushOkAsync(ana, Mut.Create(Mut.Diaper, other, Guid.NewGuid(), Mut.DiaperData(T0))))[0].Status());
    }

    [Fact]
    public async Task The_live_entity_ceiling_rejects_creates_per_mutation_and_deleting_frees_room()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        await SetParameterAsync("limits.sync_max_entities_per_baby", "2", "INT");
        var first = Guid.NewGuid();
        var results = await PushOkAsync(
            ana,
            Mut.Create(Mut.Diaper, baby, first, Mut.DiaperData(T0)),
            Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0)),
            Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0)));
        Assert.Equal(["APPLIED", "APPLIED", "REJECTED"], results.Select(r => r.Status()).ToArray());
        Assert.Equal("ENTITY_QUOTA_EXCEEDED", results[2].ProblemCode());
        Assert.False(results[2]!["retryable"]!.GetValue<bool>());

        await PushOkAsync(ana, Mut.Delete(Mut.Diaper, baby, first, results[0].Version()));
        Assert.Equal("APPLIED", (await PushOkAsync(ana, Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0))))[0].Status());
    }
}

public sealed class RateLimitTests(PostgresFixture postgres) : TrackingTestBase(postgres)
{
    protected override void Configure(Dictionary<string, string?> settings)
    {
        settings["Tracking:PushPerUserPerMinute"] = "3";
        settings["Tracking:PullPerUserPerMinute"] = "2";
    }

    [Fact]
    public async Task Push_and_pull_are_rate_limited_per_user_with_Retry_After()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await PushAsync(ana, Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0)))).Status);
        }

        var limited = await PushAsync(ana, Mut.Create(Mut.Diaper, baby, Guid.NewGuid(), Mut.DiaperData(T0)));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.Equal("RATE_LIMITED", limited.Code);
        Assert.NotNull(limited.Headers.RetryAfter);

        Assert.Equal(HttpStatusCode.OK, (await PullAsync(ana, baby)).Status);
        Assert.Equal(HttpStatusCode.OK, (await PullAsync(ana, baby)).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PullAsync(ana, baby)).Status);
    }
}
