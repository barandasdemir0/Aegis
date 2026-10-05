using System.Collections.Concurrent;
using System.Diagnostics;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;

namespace Aegis.Resilience.Distributed.Abstractions;

/// <summary>
/// Tek süreç içinde çalışan, test ve geliştirme amaçlı <see cref="ICircuitBreakerStateStore"/> implementasyonu.
/// Zaman ölçümü monotoniktir (sistem saati değişimlerinden etkilenmez).
/// </summary>
public sealed class InMemoryCircuitBreakerStateStore : ICircuitBreakerStateStore
{
    /// <summary>Süreç içi depo her zaman erişilebilirdir.</summary>
    public bool IsAvailable => true;

    private sealed class CircuitEntry
    {
        public CircuitState State = CircuitState.Closed;
        public long StateExpiresAt = long.MaxValue;
        public long WindowStart = Stopwatch.GetTimestamp();
        public int SuccessCount;
        public int FailureCount;
        public string? ProbeOwner;
        public long ProbeExpiresAt;
    }

    private readonly ConcurrentDictionary<string, CircuitEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask<CircuitState> GetStateAsync(string circuitKey, CancellationToken cancellationToken = default)
    {
        if (_entries.TryGetValue(circuitKey, out var entry))
        {
            lock (entry)
            {
                if (entry.State == CircuitState.Open && Stopwatch.GetTimestamp() >= entry.StateExpiresAt)
                {
                    entry.State = CircuitState.HalfOpen;
                }
                return ValueTask.FromResult(entry.State);
            }
        }

        return ValueTask.FromResult(CircuitState.Closed);
    }

    public ValueTask SetStateAsync(string circuitKey, CircuitState state, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var entry = _entries.GetOrAdd(circuitKey, _ => new CircuitEntry());
        lock (entry)
        {
            entry.State = state;
            entry.StateExpiresAt = state == CircuitState.Open ? AddToNow(ttl) : long.MaxValue;
            if (state == CircuitState.Closed)
            {
                entry.WindowStart = Stopwatch.GetTimestamp();
                entry.SuccessCount = 0;
                entry.FailureCount = 0;
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<(int SuccessCount, int FailureCount)> RecordResultAsync(
        string circuitKey,
        bool isSuccess,
        TimeSpan samplingDuration,
        CancellationToken cancellationToken = default)
    {
        var entry = _entries.GetOrAdd(circuitKey, _ => new CircuitEntry());
        lock (entry)
        {
            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(entry.WindowStart, now) >= samplingDuration)
            {
                entry.WindowStart = now;
                entry.SuccessCount = 0;
                entry.FailureCount = 0;
            }

            if (isSuccess)
            {
                entry.SuccessCount++;
            }
            else
            {
                entry.FailureCount++;
            }

            return ValueTask.FromResult((entry.SuccessCount, entry.FailureCount));
        }
    }

    public ValueTask<bool> TryAcquireProbeAsync(string circuitKey, string ownerId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        var entry = _entries.GetOrAdd(circuitKey, _ => new CircuitEntry());
        lock (entry)
        {
            var now = Stopwatch.GetTimestamp();
            if (entry.ProbeOwner != null && now < entry.ProbeExpiresAt)
            {
                return ValueTask.FromResult(false);
            }

            entry.ProbeOwner = ownerId;
            entry.ProbeExpiresAt = AddToNow(lease);
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask ReleaseProbeAsync(string circuitKey, string ownerId, CancellationToken cancellationToken = default)
    {
        if (_entries.TryGetValue(circuitKey, out var entry))
        {
            lock (entry)
            {
                if (entry.ProbeOwner == ownerId)
                {
                    entry.ProbeOwner = null;
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    private static long AddToNow(TimeSpan duration)
    {
        // double aritmetiği: TimeSpan.MaxValue gibi değerler long'a çevrilirken taşmaz, "sonsuz" sayılır.
        var ticks = duration.TotalSeconds * Stopwatch.Frequency;
        var now = Stopwatch.GetTimestamp();
        return ticks >= long.MaxValue - now ? long.MaxValue : now + (long)ticks;
    }
}
