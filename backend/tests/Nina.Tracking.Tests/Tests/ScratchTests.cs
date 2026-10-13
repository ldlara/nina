using Microsoft.Extensions.DependencyInjection;
using Nina.Tracking.Reads;
using Nina.Tracking.Tests.Infrastructure;

namespace Nina.Tracking.Tests.Tests;

public sealed class ScratchTests(PostgresFixture postgres) : TrackingTestBase(postgres)
{
    [Fact]
    public async Task Scratch()
    {
        var ana = await UserAsync();
        var baby = await BabyAsync(ana);
        var svc = Factory.Services.GetRequiredService<AggregatesService>();
        var ex = await Record.ExceptionAsync(() => svc.GetAsync(ana.UserId, baby, new AggregatesQuery("DAY", null, new HashSet<string> { "SLEEP", "FEEDING", "PUMPING", "DIAPERS" }), DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.True(ex is null, ex?.ToString());
    }
}
