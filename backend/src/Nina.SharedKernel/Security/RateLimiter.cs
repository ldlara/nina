using System.Collections.Concurrent;

namespace Nina.SharedKernel.Security;

/// <summary><c>Count</c> = tentativas dentro da janela após esta operação (útil para escalonar bloqueios).</summary>
public readonly record struct RateLimitDecision(bool Allowed, int RetryAfterSeconds, int Count = 0);

/// <summary>Limitador de taxa por chave (SEC-040). A implementação padrão é em memória (por instância).</summary>
public interface IRateLimiter
{
    /// <summary>Conta uma tentativa e informa se ainda está dentro do limite.</summary>
    RateLimitDecision Consume(string key, int limit, TimeSpan window);

    /// <summary>Informa se o limite foi atingido, sem contar uma nova tentativa.</summary>
    RateLimitDecision Peek(string key, int limit, TimeSpan window);

    /// <summary>Registra um evento (ex.: falha de login) sem decidir.</summary>
    void Record(string key, TimeSpan window);

    void Reset(string key);

    /// <summary>Bloqueia a chave por um tempo (backoff progressivo, SEC-041).</summary>
    void Block(string key, TimeSpan duration);

    /// <summary>Informa se a chave está bloqueada e por quantos segundos ainda.</summary>
    RateLimitDecision CheckBlocked(string key);
}

/// <summary>
/// Janela deslizante em memória. Limitação conhecida: o estado não é compartilhado entre instâncias; para escala horizontal
/// substituir por implementação distribuída (a interface é a fronteira).
/// </summary>
public sealed class InMemoryRateLimiter(TimeProvider time) : IRateLimiter
{
    private const int MaxKeys = 200_000;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _blocks = new(StringComparer.Ordinal);
    private int _ops;

    public RateLimitDecision Consume(string key, int limit, TimeSpan window)
    {
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket());
        var now = time.GetUtcNow().UtcTicks;
        RateLimitDecision decision;
        lock (bucket)
        {
            bucket.Window = window;
            Prune(bucket, now, window);
            if (bucket.Hits.Count >= limit)
            {
                decision = new RateLimitDecision(false, RetryAfter(bucket, now, window), bucket.Hits.Count);
            }
            else
            {
                bucket.Hits.Enqueue(now);
                decision = new RateLimitDecision(true, 0, bucket.Hits.Count);
            }
        }

        Sweep();
        return decision;
    }

    public RateLimitDecision Peek(string key, int limit, TimeSpan window)
    {
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            return new RateLimitDecision(true, 0);
        }

        var now = time.GetUtcNow().UtcTicks;
        lock (bucket)
        {
            Prune(bucket, now, window);
            return bucket.Hits.Count >= limit
                ? new RateLimitDecision(false, RetryAfter(bucket, now, window))
                : new RateLimitDecision(true, 0);
        }
    }

    public void Record(string key, TimeSpan window)
    {
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket());
        var now = time.GetUtcNow().UtcTicks;
        lock (bucket)
        {
            bucket.Window = window;
            Prune(bucket, now, window);
            bucket.Hits.Enqueue(now);
        }

        Sweep();
    }

    public void Reset(string key)
    {
        _buckets.TryRemove(key, out _);
        _blocks.TryRemove(key, out _);
    }

    public void Block(string key, TimeSpan duration) =>
        _blocks[key] = time.GetUtcNow().UtcTicks + duration.Ticks;

    public RateLimitDecision CheckBlocked(string key)
    {
        if (!_blocks.TryGetValue(key, out var until))
        {
            return new RateLimitDecision(true, 0);
        }

        var remaining = until - time.GetUtcNow().UtcTicks;
        if (remaining <= 0)
        {
            _blocks.TryRemove(key, out _);
            return new RateLimitDecision(true, 0);
        }

        return new RateLimitDecision(false, Math.Max(1, (int)Math.Ceiling(TimeSpan.FromTicks(remaining).TotalSeconds)));
    }

    private static void Prune(Bucket bucket, long now, TimeSpan window)
    {
        while (bucket.Hits.Count > 0 && now - bucket.Hits.Peek() >= window.Ticks)
        {
            bucket.Hits.Dequeue();
        }
    }

    private static int RetryAfter(Bucket bucket, long now, TimeSpan window)
    {
        var oldest = bucket.Hits.Peek();
        var remaining = TimeSpan.FromTicks(oldest + window.Ticks - now);
        return Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
    }

    // Evita crescimento ilimitado: de tempos em tempos remove chaves vazias; acima do teto descarta tudo (fail-open pontual).
    private void Sweep()
    {
        if (Interlocked.Increment(ref _ops) % 512 != 0 && _buckets.Count < MaxKeys)
        {
            return;
        }

        var now = time.GetUtcNow().UtcTicks;
        foreach (var (key, bucket) in _buckets)
        {
            lock (bucket)
            {
                Prune(bucket, now, bucket.Window);
                if (bucket.Hits.Count == 0)
                {
                    _buckets.TryRemove(key, out _);
                }
            }
        }

        if (_buckets.Count >= MaxKeys)
        {
            _buckets.Clear();
        }
    }

    private sealed class Bucket
    {
        public Queue<long> Hits { get; } = new();

        public TimeSpan Window { get; set; }
    }
}
