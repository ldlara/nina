using System.Collections.Concurrent;

namespace Nina.SharedKernel.Security;

/// <summary><c>Count</c> = tentativas dentro da janela após esta operação (útil para escalonar bloqueios).</summary>
public readonly record struct RateLimitDecision(bool Allowed, int RetryAfterSeconds, int Count = 0);

/// <summary>Limitador de taxa por chave (SEC-040). A interface é a fronteira de distribuição: a implementação padrão
/// (<see cref="InMemoryRateLimiter"/>) é por instância; para mais de uma réplica registre uma implementação com estado
/// compartilhado (Redis ou tabela com UPSERT atômico) no lugar dela (NR-10). Contrato: implementações devem falhar fechado
/// (na dúvida, negar), nunca descartar o estado de chaves já no limite.</summary>
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
/// Janela deslizante em memória. LIMITAÇÕES CONHECIDAS (NR-10): o estado é por instância (N réplicas = N vezes os limites) e
/// se perde no restart; para escala horizontal substituir <see cref="IRateLimiter"/> por implementação distribuída.
/// Memória limitada a <c>maxKeys</c> chaves, com política FAIL-CLOSED: no teto removem-se primeiro as chaves expiradas e depois
/// as menos recentemente usadas (LRU) entre as que ainda NÃO atingiram o limite; chaves já no limite e bloqueios ativos nunca
/// são evictados (um atacante não consegue "lavar" o próprio bloqueio enchendo a tabela). Se, mesmo assim, não houver espaço
/// (tabela cheia de chaves em limite), chaves novas são NEGADAS em vez de admitidas.
/// </summary>
public sealed class InMemoryRateLimiter(TimeProvider time, int maxKeys = InMemoryRateLimiter.DefaultMaxKeys) : IRateLimiter
{
    public const int DefaultMaxKeys = 200_000;
    private const int SweepEvery = 512;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _blocks = new(StringComparer.Ordinal);
    private readonly object _evictionLock = new();
    private int _ops;

    /// <summary>Quantidade de chaves de janela retidas (observabilidade e testes).</summary>
    public int KeyCount => _buckets.Count;

    public RateLimitDecision Consume(string key, int limit, TimeSpan window)
    {
        var now = time.GetUtcNow().UtcTicks;
        if (!TryGetBucket(key, now, out var bucket))
        {
            return new RateLimitDecision(false, SaturatedRetryAfterSeconds(window), limit);
        }

        RateLimitDecision decision;
        lock (bucket)
        {
            bucket.Window = window;
            bucket.Limit = limit;
            bucket.LastAccess = now;
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
            bucket.LastAccess = now;
            bucket.Limit = limit;
            Prune(bucket, now, window);
            return bucket.Hits.Count >= limit
                ? new RateLimitDecision(false, RetryAfter(bucket, now, window))
                : new RateLimitDecision(true, 0);
        }
    }

    public void Record(string key, TimeSpan window)
    {
        var now = time.GetUtcNow().UtcTicks;
        if (!TryGetBucket(key, now, out var bucket))
        {
            // Sem espaço: registrar a falha como bloqueio pela janela inteira (fail-closed).
            Block(key, window);
            return;
        }

        lock (bucket)
        {
            bucket.Window = window;
            bucket.LastAccess = now;
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

    private static int SaturatedRetryAfterSeconds(TimeSpan window) =>
        Math.Max(1, (int)Math.Ceiling(Math.Min(window.TotalSeconds, 60)));

    private bool TryGetBucket(string key, long now, out Bucket bucket)
    {
        if (_buckets.TryGetValue(key, out bucket!))
        {
            return true;
        }

        if (_buckets.Count >= maxKeys && !MakeRoom(now))
        {
            bucket = null!;
            return false;
        }

        bucket = _buckets.GetOrAdd(key, _ => new Bucket { LastAccess = now });
        return true;
    }

    // Libera espaço no teto: 1) expiradas; 2) LRU entre as que não atingiram o limite (nunca as que estão em limite).
    private bool MakeRoom(long now)
    {
        lock (_evictionLock)
        {
            if (_buckets.Count < maxKeys)
            {
                return true;
            }

            RemoveExpired(now);
            if (_buckets.Count < maxKeys)
            {
                return true;
            }

            var target = Math.Max(1, maxKeys / 10);
            var candidates = new List<(string Key, long LastAccess)>();
            foreach (var (key, bucket) in _buckets)
            {
                lock (bucket)
                {
                    if (bucket.Hits.Count < bucket.Limit)
                    {
                        candidates.Add((key, bucket.LastAccess));
                    }
                }
            }

            foreach (var (key, _) in candidates.OrderBy(c => c.LastAccess).Take(target))
            {
                _buckets.TryRemove(key, out _);
            }

            return _buckets.Count < maxKeys;
        }
    }

    private void RemoveExpired(long now)
    {
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

        foreach (var (key, until) in _blocks)
        {
            if (until <= now)
            {
                _blocks.TryRemove(key, out _);
            }
        }
    }

    // Manutenção periódica: só remove o que expirou (nunca descarta estado vivo).
    private void Sweep()
    {
        if (Interlocked.Increment(ref _ops) % SweepEvery != 0)
        {
            return;
        }

        lock (_evictionLock)
        {
            RemoveExpired(time.GetUtcNow().UtcTicks);
        }
    }

    private sealed class Bucket
    {
        public Queue<long> Hits { get; } = new();

        public TimeSpan Window { get; set; }

        public int Limit { get; set; } = int.MaxValue;

        public long LastAccess { get; set; }
    }
}
