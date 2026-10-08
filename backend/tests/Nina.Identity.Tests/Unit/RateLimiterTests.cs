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

public sealed class RateLimiterEvictionTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-08T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Reaching_the_cap_never_forgets_a_key_that_is_already_at_its_limit()
    {
        var limiter = new InMemoryRateLimiter(_time, maxKeys: 100);
        Assert.True(limiter.Consume("attacker", 1, TimeSpan.FromHours(1)).Allowed);
        Assert.False(limiter.Consume("attacker", 1, TimeSpan.FromHours(1)).Allowed);

        // inunda a tabela com chaves de outros clientes (muito além do teto)
        for (var i = 0; i < 1_000; i++)
        {
            limiter.Consume($"noise:{i}", 5, TimeSpan.FromHours(1));
        }

        Assert.True(limiter.KeyCount <= 100);
        Assert.False(limiter.Consume("attacker", 1, TimeSpan.FromHours(1)).Allowed); // antes: Clear() liberava
    }

    [Fact]
    public void Eviction_is_least_recently_used_among_keys_below_their_limit()
    {
        var limiter = new InMemoryRateLimiter(_time, maxKeys: 10);
        for (var i = 0; i < 10; i++)
        {
            limiter.Consume($"k{i}", 5, TimeSpan.FromHours(1));
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        limiter.Consume("k0", 5, TimeSpan.FromHours(1)); // k0 volta a ser a mais recente
        limiter.Consume("new", 5, TimeSpan.FromHours(1)); // força evicção (remove k1, a mais antiga)

        Assert.Equal(3, limiter.Consume("k0", 5, TimeSpan.FromHours(1)).Count); // k0 manteve o histórico (3ª ação)
        Assert.Equal(1, limiter.Consume("k1", 5, TimeSpan.FromHours(1)).Count); // k1 foi evictada e recomeçou
    }

    [Fact]
    public void When_the_table_is_full_of_keys_at_their_limit_new_keys_are_denied_fail_closed()
    {
        var limiter = new InMemoryRateLimiter(_time, maxKeys: 5);
        for (var i = 0; i < 5; i++)
        {
            limiter.Consume($"full{i}", 1, TimeSpan.FromHours(1));
        }

        var denied = limiter.Consume("newcomer", 10, TimeSpan.FromHours(1));

        Assert.False(denied.Allowed);
        Assert.True(denied.RetryAfterSeconds >= 1);
        _time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1)); // expiradas liberam espaço
        Assert.True(limiter.Consume("newcomer", 10, TimeSpan.FromHours(1)).Allowed);
    }

    [Fact]
    public void Failure_counters_recorded_through_record_keep_blocking_when_the_table_saturates()
    {
        var limiter = new InMemoryRateLimiter(_time, maxKeys: 5);
        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.Peek("fail:ip:1", 3, TimeSpan.FromMinutes(15)).Allowed);
            limiter.Record("fail:ip:1", TimeSpan.FromMinutes(15));
        }

        for (var i = 0; i < 100; i++)
        {
            limiter.Consume($"noise:{i}", 5, TimeSpan.FromMinutes(15));
        }

        Assert.False(limiter.Peek("fail:ip:1", 3, TimeSpan.FromMinutes(15)).Allowed);
    }
}
