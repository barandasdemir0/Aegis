using Aegis.Resilience.Distributed.Abstractions;
using StackExchange.Redis;

namespace Aegis.Resilience.Distributed.Redis;

/// <summary>
/// Hız sınırlayıcı sayaçlarını Redis'te tüm pod'lar arasında paylaşan depo (AspNetCoreRateLimit.Redis / RedisRateLimiting
/// eşdeğeri). Her karar tek bir atomik Lua betiğiyle verilir; saat olarak Redis'in <c>TIME</c> komutu kullanılır, böylece
/// pod saatleri arasındaki fark sonucu etkilemez.
/// <para>
/// Redis erişilemezken (kesinti, yavaşlık: <see cref="RedisCircuitBreakerStateStore.DefaultOperationTimeout"/>) sınırsız
/// geçiş yerine aynı kuralla POD BAŞINA yerel sınıra düşülür; koruma sürer. Redis dönünce ortak kota kendiliğinden devam eder.
/// </para>
/// </summary>
public sealed class RedisRateLimitStore : IDistributedRateLimitStore
{
    // InMemoryDistributedRateLimitStore'daki RateLimitAlgorithms ile birebir aynı formüller (ms çözünürlüğü).
    // ARGV: 1=algoritma (0 kova, 1 sabit, 2 kayan), 2=limit, 3=pencere ms, 4=periyot başına jeton, 5=izin, 6=anahtar ömrü ms.
    // Dönüş: { verildi(0/1), kalan, yeniden deneme ms (-1: yok) }.
    private const string AcquireLuaScript = """
        local key = KEYS[1]
        local algorithm = tonumber(ARGV[1])
        local limit = tonumber(ARGV[2])
        local windowMs = tonumber(ARGV[3])
        local tokensPerPeriod = tonumber(ARGV[4])
        local permits = tonumber(ARGV[5])
        local time = redis.call("TIME")
        local now = tonumber(time[1]) * 1000 + math.floor(tonumber(time[2]) / 1000)
        local acquired, remaining, retry = 0, 0, -1

        if algorithm == 0 then
            local rate = tokensPerPeriod / windowMs
            local tokens = tonumber(redis.call("HGET", key, "tokens"))
            local last = tonumber(redis.call("HGET", key, "last"))
            if tokens == nil then
                tokens = limit
                last = now
            end
            if now - last > 0 then
                tokens = math.min(limit, tokens + (now - last) * rate)
                last = now
            end
            if tokens >= permits then
                tokens = tokens - permits
                acquired = 1
            elseif permits <= limit then
                retry = math.max(1, math.ceil((permits - tokens) / rate))
            end
            remaining = math.floor(tokens)
            redis.call("HSET", key, "tokens", tostring(tokens), "last", last)
        else
            local windowId = math.floor(now / windowMs)
            local storedId = tonumber(redis.call("HGET", key, "wid") or "-1")
            local current = tonumber(redis.call("HGET", key, "cur") or "0")
            local previous = tonumber(redis.call("HGET", key, "prev") or "0")
            if storedId ~= windowId then
                if windowId == storedId + 1 then previous = current else previous = 0 end
                current = 0
            end

            if algorithm == 1 then
                if current + permits <= limit then
                    current = current + permits
                    acquired = 1
                    remaining = limit - current
                else
                    remaining = math.max(0, limit - current)
                    if permits <= limit then
                        retry = math.max(1, (windowId + 1) * windowMs - now)
                    end
                end
            else
                local elapsed = now - windowId * windowMs
                local estimated = previous * ((windowMs - elapsed) / windowMs) + current
                if estimated + permits <= limit then
                    current = current + permits
                    acquired = 1
                    remaining = math.floor(limit - estimated - permits)
                else
                    remaining = math.max(0, math.floor(limit - estimated))
                    if permits <= limit then
                        local room = limit - current - permits
                        local wait
                        if room >= 0 and previous > 0 then
                            wait = windowMs * (1 - room / previous) - elapsed
                        elseif current == 0 then
                            wait = windowMs - elapsed
                        else
                            wait = (windowMs - elapsed) + math.max(0, windowMs * (1 - (limit - permits) / current))
                        end
                        retry = math.max(1, math.ceil(wait))
                    end
                end
            end
            redis.call("HSET", key, "wid", windowId, "cur", current, "prev", previous)
        end

        redis.call("PEXPIRE", key, ARGV[6])
        return { acquired, remaining, retry }
        """;

    private readonly RedisConnection _connection;
    private readonly string _keyPrefix;
    private readonly InMemoryDistributedRateLimitStore _localFallback = new();

    /// <param name="redis">Redis bağlantısı.</param>
    /// <param name="keyPrefix">Anahtar öneki.</param>
    /// <param name="operationTimeout">Tek işlemin üst süresi (varsayılan 250 ms); aşılırsa yerel sınıra düşülür.</param>
    public RedisRateLimitStore(IConnectionMultiplexer redis, string keyPrefix = "aegis:rl:", TimeSpan? operationTimeout = null)
        : this(new RedisConnection(redis, operationTimeout), keyPrefix)
    {
    }

    /// <summary>Bağlantıyı arka planda kuran depo: bağlantı hazır olana kadar yerel sınır kullanılır.</summary>
    internal RedisRateLimitStore(Func<Task<IConnectionMultiplexer>> connect, string keyPrefix, TimeSpan? operationTimeout = null)
        : this(new RedisConnection(connect, operationTimeout), keyPrefix)
    {
    }

    private RedisRateLimitStore(RedisConnection connection, string keyPrefix)
    {
        _connection = connection;
        _keyPrefix = keyPrefix ?? throw new ArgumentNullException(nameof(keyPrefix));
    }

    /// <inheritdoc />
    public bool IsAvailable => _connection.IsAvailable;

    /// <inheritdoc />
    public async ValueTask<DistributedRateLimitDecision> TryAcquireAsync(
        string key, DistributedRateLimitRule rule, int permitCount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        try
        {
            var result = (RedisValue[]?)await _connection.GetDatabase().ScriptEvaluateAsync(
                AcquireLuaScript,
                new RedisKey[] { _keyPrefix + key },
                new RedisValue[]
                {
                    (int)rule.Algorithm, rule.PermitLimit, Math.Max(1, (long)rule.Window.TotalMilliseconds), rule.TokensPerPeriod,
                    permitCount, (long)rule.StateLifetime.TotalMilliseconds
                }).WaitAsync(_connection.OperationTimeout, cancellationToken).ConfigureAwait(false);

            _connection.MarkSuccess();
            var retryAfterMs = (long)result![2];
            return new DistributedRateLimitDecision(
                (int)result[0] == 1, (int)result[1], retryAfterMs < 0 ? null : TimeSpan.FromMilliseconds(retryAfterMs));
        }
        catch (Exception ex) when (RedisConnection.IsTransient(ex))
        {
            _connection.MarkTransientFailure();
            return await _localFallback.TryAcquireAsync(key, rule, permitCount, cancellationToken).ConfigureAwait(false);
        }
    }
}
