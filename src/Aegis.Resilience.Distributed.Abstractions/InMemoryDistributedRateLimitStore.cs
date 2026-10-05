using System.Collections.Concurrent;

namespace Aegis.Resilience.Distributed.Abstractions;

/// <summary>
/// Bellek içi hız sınırlayıcı deposu: tek sunucu, testler ve Redis deposunun erişilemezken düştüğü yerel sınır.
/// Algoritmalar Redis deposundaki Lua betikleriyle birebir aynıdır (milisaniye çözünürlüğü).
/// </summary>
public sealed class InMemoryDistributedRateLimitStore : IDistributedRateLimitStore
{
    /// <summary>Bu sayıyı aşınca süresi geçmiş anahtarlar temizlenir (rastgele bölüm anahtarıyla bellek şişirmeye karşı).</summary>
    private const int SweepThreshold = 10_000;

    private readonly TimeProvider _timeProvider;
    private readonly long _originTimestamp;
    private readonly ConcurrentDictionary<string, LimiterState> _states = new(StringComparer.Ordinal);

    // ConcurrentDictionary.Count tüm kilitleri alır; sıcak yolda okunmaması için anahtar sayısı ayrıca tutulur.
    private int _count;
    private int _isSweeping;

    // Canlı anahtar eşiği aştığında temizlik hiçbir şey silemez; her istekte tam tarama (O(n)) CPU tüketirdi. Aralık sınırlıdır.
    private const long SweepIntervalMs = 1_000;
    private long _nextSweepMs;

    /// <param name="timeProvider">Saat (testlerde sahte saat). Varsayılan sistem saati.</param>
    public InMemoryDistributedRateLimitStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _originTimestamp = _timeProvider.GetTimestamp();
    }

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public ValueTask<DistributedRateLimitDecision> TryAcquireAsync(
        string key, DistributedRateLimitRule rule, int permitCount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        // Monotonik saat: duvar saati sıçramaları (NTP, elle ayar) kovayı boşaltmaz ya da doldurmaz (AEGIS-144).
        var nowMs = (long)_timeProvider.GetElapsedTime(_originTimestamp).TotalMilliseconds;
        if (Volatile.Read(ref _count) > SweepThreshold && nowMs >= Volatile.Read(ref _nextSweepMs))
        {
            Sweep(nowMs);
        }

        if (!_states.TryGetValue(key, out var state))
        {
            state = Add(key);
        }

        lock (state)
        {
            state.ExpiresAtMs = nowMs + (long)rule.StateLifetime.TotalMilliseconds;
            return new ValueTask<DistributedRateLimitDecision>(RateLimitAlgorithms.Acquire(state, rule, permitCount, nowMs));
        }
    }

    private LimiterState Add(string key)
    {
        var created = new LimiterState();
        var actual = _states.GetOrAdd(key, created);
        if (ReferenceEquals(actual, created))
        {
            Interlocked.Increment(ref _count);
        }

        return actual;
    }

    // Aynı anda tek temizlik: eşiği aşan her istek ayrı ayrı sözlüğü taramasın.
    private void Sweep(long nowMs)
    {
        if (Interlocked.CompareExchange(ref _isSweeping, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Volatile.Write(ref _nextSweepMs, nowMs + SweepIntervalMs);
            foreach (var entry in _states)
            {
                if (Volatile.Read(ref entry.Value.ExpiresAtMs) < nowMs && _states.TryRemove(entry.Key, out _))
                {
                    Interlocked.Decrement(ref _count);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _isSweeping, 0);
        }
    }
}
