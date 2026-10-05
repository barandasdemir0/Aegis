using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Distributed.Abstractions;
using StackExchange.Redis;

namespace Aegis.Resilience.Distributed.Redis;

/// <summary>
/// Çoklu pod (Kubernetes / Distributed) mimarilerinde Circuit Breaker durumunu ve sayaçlarını
/// Redis üzerinde atomik Lua scriptleri ile yöneten, Redis kesintilerinde yerel hafızaya düşerek
/// (Fail-Open / Resilient Fallback) iş akışının çökmesini önleyen kurumsal durum deposu.
/// <para>
/// Open -> HalfOpen geçişi Redis'in kendi TTL'inden (milisaniye hassasiyetli PTTL) türetilir; pod saatleri arasındaki
/// farktan etkilenmez. Open anahtarı <c>BreakDuration + HalfOpenRetention</c> ömrüyle yazılır; kalan ömür
/// <see cref="HalfOpenRetention"/>'a düştüğünde devre HalfOpen okunur. Bu süre içinde hiç deneme isteği gelmezse
/// anahtar düşer ve devre kapanır.
/// </para>
/// </summary>
public sealed class RedisCircuitBreakerStateStore : ICircuitBreakerStateStore
{
    /// <summary>
    /// Açılma süresi dolduktan sonra devrenin deneme isteği beklerken HalfOpen kalacağı en uzun süre.
    /// </summary>
    public static readonly TimeSpan HalfOpenRetention = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Tek bir Redis işlemi için varsayılan üst süre (AEGIS-161). Redis ÇÖKMEDEN yavaşlarsa (ağ tıkanıklığı, bellek baskısı,
    /// uzun süren komut) her korunan çağrı Redis gecikmesini miras alıyordu: 1,5 sn gecikmede çağrı başına 3 sn.
    /// Süre aşılınca işlem yerel duruma düşer ve depo kısa süre "erişilemez" raporlanır (health check görür).
    /// </summary>
    public static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromMilliseconds(250);

    private readonly RedisConnection _connection;
    private readonly string _keyPrefix;
    private readonly ConcurrentDictionary<string, FallbackState> _fallbackStates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (int Success, int Failure, long WindowStart)> _fallbackStats = new(StringComparer.Ordinal);

    /// <summary>Redis erişilemezken kullanılan yerel durum. Open, <see cref="OpenUntil"/> sonunda HalfOpen'a döner (asla kalıcı değildir).</summary>
    private readonly record struct FallbackState(CircuitState State, long OpenUntil);

    private const string RecordLuaScript = """
        local statsKey = KEYS[1]
        local isSuccess = ARGV[1]
        local ttlSeconds = tonumber(ARGV[2])

        if isSuccess == "1" then
            redis.call("HINCRBY", statsKey, "success", 1)
        else
            redis.call("HINCRBY", statsKey, "failure", 1)
        end

        local currentTtl = redis.call("TTL", statsKey)
        if currentTtl < 0 then
            redis.call("EXPIRE", statsKey, ttlSeconds)
        end

        local success = tonumber(redis.call("HGET", statsKey, "success") or "0")
        local failure = tonumber(redis.call("HGET", statsKey, "failure") or "0")
        return { success, failure }
        """;

    private const string ReleaseProbeLuaScript = """
        if redis.call("GET", KEYS[1]) == ARGV[1] then
            return redis.call("DEL", KEYS[1])
        end
        return 0
        """;

    /// <summary>Redis bağlantısı şu an kurulu mu (AEGIS-160). Yanlış adres/şifre veya kesintide <c>false</c>; depo o sırada yerel duruma düşer.</summary>
    /// <para>Bağlı olsa bile son <c>5 sn</c> içinde işlem süre sınırı aşıldıysa (yavaş Redis) <c>false</c> döner.</para>
    public bool IsAvailable => _connection.IsAvailable;

    public RedisCircuitBreakerStateStore(IConnectionMultiplexer redis, string keyPrefix = "aegis:cb:", TimeSpan? operationTimeout = null)
        : this(new RedisConnection(redis, operationTimeout), keyPrefix)
    {
    }

    /// <summary>
    /// Bağlantıyı arka planda kuran depo (AEGIS-157). Bağlantı hazır olana kadar hiçbir çağrı beklemez;
    /// depo anında yerel duruma düşer (fail-open). Bağlantı denemesi başarısız olursa bir sonraki kullanımda yeniden başlatılır.
    /// </summary>
    internal RedisCircuitBreakerStateStore(Func<Task<IConnectionMultiplexer>> connect, string keyPrefix, TimeSpan? operationTimeout = null)
        : this(new RedisConnection(connect, operationTimeout), keyPrefix)
    {
    }

    private RedisCircuitBreakerStateStore(RedisConnection connection, string keyPrefix)
    {
        _connection = connection;
        _keyPrefix = keyPrefix;
    }

    public async ValueTask<CircuitState> GetStateAsync(string circuitKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = _connection.GetDatabase();
            var entry = await db.StringGetWithExpiryAsync(StateKey(circuitKey)).WaitAsync(_connection.OperationTimeout, cancellationToken).ConfigureAwait(false);
            var state = Decode(entry.Value, entry.Expiry, out var remainingOpen);

            _fallbackStates[circuitKey] = new FallbackState(state, state == CircuitState.Open ? AddToNow(remainingOpen) : 0);
            _connection.MarkSuccess();
            return state;
        }
        catch (Exception ex) when (RedisConnection.IsTransient(ex))
        {
            _connection.MarkTransientFailure();
            // Fail-open: Redis ulaşılamazken yerel duruma başvur. Yerel Open süresi dolunca HalfOpen döner;
            // Redis kesintisi devreyi asla kalıcı olarak açık bırakamaz.
            if (!_fallbackStates.TryGetValue(circuitKey, out var fallback))
            {
                return CircuitState.Closed;
            }

            return fallback.State == CircuitState.Open && Stopwatch.GetTimestamp() >= fallback.OpenUntil
                ? CircuitState.HalfOpen
                : fallback.State;
        }
    }

    public async ValueTask SetStateAsync(string circuitKey, CircuitState state, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        _fallbackStates[circuitKey] = new FallbackState(state, state == CircuitState.Open ? AddToNow(ttl) : 0);
        if (state == CircuitState.Closed)
        {
            _fallbackStats.TryRemove(circuitKey, out _);
        }

        try
        {
            var db = _connection.GetDatabase();
            var key = StateKey(circuitKey);

            switch (state)
            {
                case CircuitState.Closed:
                    // Kapanış pencere sayaçlarını da sıfırlar: eski hatalar yeni kapanan devreyi hemen yeniden açmamalı.
                    await db.KeyDeleteAsync(new RedisKey[] { key, StatsKey(circuitKey) }).WaitAsync(_connection.OperationTimeout, cancellationToken).ConfigureAwait(false);
                    break;

                case CircuitState.Open:
                    var retentionMs = (long)HalfOpenRetention.TotalMilliseconds;
                    await db.StringSetAsync(
                        key,
                        FormattableString.Invariant($"{(int)CircuitState.Open}|{retentionMs}"),
                        ttl + HalfOpenRetention).WaitAsync(_connection.OperationTimeout, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    if (ttl > TimeSpan.Zero)
                    {
                        await db.StringSetAsync(key, (int)state, ttl).WaitAsync(_connection.OperationTimeout, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await db.StringSetAsync(key, (int)state).WaitAsync(_connection.OperationTimeout, cancellationToken).ConfigureAwait(false);
                    }
                    break;
            }
        }
        catch (Exception ex) when (RedisConnection.IsTransient(ex))
        {
            _connection.MarkTransientFailure();
            // Redis ulaşılamaz olduğunda yerel hafıza güncellendi; çağıran akış kesintiye uğramaz.
        }
    }

    public async ValueTask<(int SuccessCount, int FailureCount)> RecordResultAsync(
        string circuitKey,
        bool isSuccess,
        TimeSpan samplingDuration,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var db = _connection.GetDatabase();
            var ttlSeconds = (int)Math.Min(int.MaxValue, Math.Max(1, samplingDuration.TotalSeconds));

            var result = (RedisValue[]?)await db.ScriptEvaluateAsync(
                RecordLuaScript,
                new RedisKey[] { StatsKey(circuitKey) },
                new RedisValue[] { isSuccess ? "1" : "0", ttlSeconds }).WaitAsync(_connection.OperationTimeout, cancellationToken).ConfigureAwait(false);

            if (result != null && result.Length == 2)
            {
                var successes = (int)result[0];
                var failures = (int)result[1];
                _fallbackStats[circuitKey] = (successes, failures, Stopwatch.GetTimestamp());
                _connection.MarkSuccess();
                return (successes, failures);
            }

            return (0, 0);
        }
        catch (Exception ex) when (RedisConnection.IsTransient(ex))
        {
            _connection.MarkTransientFailure();
            // Yerel fallback istatistiği güncelle
            var now = Stopwatch.GetTimestamp();
            var entry = _fallbackStats.AddOrUpdate(
                circuitKey,
                _ => (isSuccess ? 1 : 0, isSuccess ? 0 : 1, now),
                (_, old) =>
                {
                    if (Stopwatch.GetElapsedTime(old.WindowStart, now) > samplingDuration)
                    {
                        return (isSuccess ? 1 : 0, isSuccess ? 0 : 1, now);
                    }
                    return (old.Success + (isSuccess ? 1 : 0), old.Failure + (isSuccess ? 0 : 1), old.WindowStart);
                });

            return (entry.Success, entry.Failure);
        }
    }

    public async ValueTask<bool> TryAcquireProbeAsync(string circuitKey, string ownerId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = _connection.GetDatabase();
            var acquired = await db.StringSetAsync(ProbeKey(circuitKey), ownerId, lease, When.NotExists).WaitAsync(_connection.OperationTimeout, cancellationToken).ConfigureAwait(false);
            _connection.MarkSuccess();
            return acquired;
        }
        catch (Exception ex) when (RedisConnection.IsTransient(ex))
        {
            _connection.MarkTransientFailure();
            // Fail-open: Redis yokken tekillik pod içinde sağlanır (strateji yerel kapıyı ayrıca tutar).
            return true;
        }
    }

    public async ValueTask ReleaseProbeAsync(string circuitKey, string ownerId, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = _connection.GetDatabase();
            await db.ScriptEvaluateAsync(ReleaseProbeLuaScript, new RedisKey[] { ProbeKey(circuitKey) }, new RedisValue[] { ownerId })
                .WaitAsync(_connection.OperationTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (RedisConnection.IsTransient(ex))
        {
            _connection.MarkTransientFailure();
            // Hak kiralama süresi sonunda kendiliğinden düşer.
        }
    }

    /// <summary>
    /// Redis değerini duruma çevirir. Open değeri "1|{retentionMs}" biçimindedir; kalan ömür (PTTL, ms) retention'a
    /// düştüyse HalfOpen'dır. Eski sürümlerin yazdığı düz "1" değeri (retention bilgisi yok) Open kabul edilir ve TTL'i
    /// bitince kapanır. Çözülemeyen değerler güvenli tarafta Closed sayılır.
    /// </summary>
    internal static CircuitState Decode(RedisValue value, TimeSpan? expiry, out TimeSpan remainingOpen)
    {
        remainingOpen = TimeSpan.Zero;
        if (value.IsNullOrEmpty)
        {
            return CircuitState.Closed;
        }

        var text = (string)value!;
        var separator = text.IndexOf('|', StringComparison.Ordinal);
        var stateText = separator < 0 ? text : text.Substring(0, separator);
        if (!int.TryParse(stateText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stateInt) ||
            !Enum.IsDefined(typeof(CircuitState), stateInt))
        {
            return CircuitState.Closed;
        }

        var state = (CircuitState)stateInt;
        if (state != CircuitState.Open)
        {
            return state;
        }

        // Span aşırı yüklemesi netstandard2.0/.NET Framework'te yok; tek kod yolu için Substring (Redis değeri kısa, tahsis önemsiz).
#pragma warning disable CA1846
        var retentionText = separator < 0 ? string.Empty : text.Substring(separator + 1);
#pragma warning restore CA1846
        if (separator < 0 || !long.TryParse(retentionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var retentionMs))
        {
            remainingOpen = expiry ?? TimeSpan.Zero;
            return CircuitState.Open;
        }

        var retention = TimeSpan.FromMilliseconds(retentionMs);
        if (expiry is not { } remaining || remaining <= retention)
        {
            return CircuitState.HalfOpen;
        }

        remainingOpen = remaining - retention;
        return CircuitState.Open;
    }

    private string StateKey(string circuitKey) => $"{_keyPrefix}{circuitKey}:state";

    private string StatsKey(string circuitKey) => $"{_keyPrefix}{circuitKey}:stats";

    private string ProbeKey(string circuitKey) => $"{_keyPrefix}{circuitKey}:probe";

    private static long AddToNow(TimeSpan duration)
    {
        var ticks = duration.TotalSeconds * Stopwatch.Frequency;
        var now = Stopwatch.GetTimestamp();
        return ticks >= long.MaxValue - now ? long.MaxValue : now + (long)ticks;
    }
}
