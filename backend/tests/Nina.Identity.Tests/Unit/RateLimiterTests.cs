using Microsoft.Extensions.Time.Testing;
using Nina.SharedKernel.Security;

namespace Nina.Identity.Tests.Unit;

public sealed class RateLimiterTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-08T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    private InMemoryRateLimiter Create() => new(_time);

    [Fact]
    public void Consume_allows_up_to_the_limit_then_blocks_with_retry_after()
    {
        var limiter = Create();
        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.Consume("k", 3, TimeSpan.FromMinutes(1)).Allowed);
            _time.Advance(TimeSpan.FromSeconds(10));
        }

        var blocked = limiter.Consume("k", 3, TimeSpan.FromMinutes(1));

        Assert.False(blocked.Allowed);
        Assert.Equal(30, blocked.RetryAfterSeconds);
    }

    [Fact]
    public void Window_slides_and_frees_capacity()
    {
        var limiter = Create();
        for (var i = 0; i < 2; i++)
        {
            limiter.Consume("k", 2, TimeSpan.FromMinutes(1));
        }

        Assert.False(limiter.Consume("k", 2, TimeSpan.FromMinutes(1)).Allowed);
        _time.Advance(TimeSpan.FromSeconds(61));

        Assert.True(limiter.Consume("k", 2, TimeSpan.FromMinutes(1)).Allowed);
    }

    [Fact]
    public void Keys_are_independent_and_reset_clears_them()
    {
        var limiter = Create();
        limiter.Consume("a", 1, TimeSpan.FromMinutes(1));

        Assert.False(limiter.Consume("a", 1, TimeSpan.FromMinutes(1)).Allowed);
        Assert.True(limiter.Consume("b", 1, TimeSpan.FromMinutes(1)).Allowed);
        limiter.Reset("a");
        Assert.True(limiter.Consume("a", 1, TimeSpan.FromMinutes(1)).Allowed);
    }

    [Fact]
    public void Peek_and_record_count_failures_without_consuming_on_check()
    {
        var limiter = Create();
        for (var i = 0; i < 5; i++)
        {
            Assert.True(limiter.Peek("f", 5, TimeSpan.FromMinutes(15)).Allowed);
            limiter.Record("f", TimeSpan.FromMinutes(15));
        }

        var decision = limiter.Peek("f", 5, TimeSpan.FromMinutes(15));
        Assert.False(decision.Allowed);
        Assert.InRange(decision.RetryAfterSeconds, 1, 900);
        Assert.False(limiter.Peek("f", 5, TimeSpan.FromMinutes(15)).Allowed); // Peek não altera o estado
    }

    [Fact]
    public async Task Concurrent_consumers_never_exceed_the_limit()
    {
        var limiter = Create();
        var allowed = 0;
        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() =>
        {
            if (limiter.Consume("hot", 25, TimeSpan.FromMinutes(1)).Allowed)
            {
                Interlocked.Increment(ref allowed);
            }
        })));

        Assert.Equal(25, allowed);
    }
}
