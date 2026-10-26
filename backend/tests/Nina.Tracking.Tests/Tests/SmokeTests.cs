using System.Net;
using Nina.Tracking.Tests.Infrastructure;

namespace Nina.Tracking.Tests.Tests;

public sealed class SmokeTests(PostgresFixture postgres) : TrackingTestBase(postgres)
{
    [Fact]
    public async Task Push_then_pull_roundtrip()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var sleep = Guid.NewGuid();
        var results = await PushOkAsync(ana, Mut.Create(Mut.Sleep, baby, sleep, Mut.SleepData(T0, T0.AddHours(1))));
        Assert.Equal("APPLIED", results[0]!["status"]!.GetValue<string>());
        var (changes, _) = await PullAllAsync(ana, baby);
        Assert.Contains(changes, c => c["entity_type"]!.GetValue<string>() == "SLEEP_SESSION");
        Assert.Contains(changes, c => c["entity_type"]!.GetValue<string>() == "BABY");
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync($"/v1/babies/{baby}/timeline", ana.AccessToken)).Status);
    }
}
