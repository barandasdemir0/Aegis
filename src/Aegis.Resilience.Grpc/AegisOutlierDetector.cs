using System.Collections.Concurrent;
using Grpc.Core;

namespace Aegis.Resilience.Grpc;

/// <summary>
/// Uç nokta sağlık takibi ve ayıklama kararı (Envoy outlier detection). Uç nokta anahtarı <c>host:port</c>'tur; aynı sunucuya giden
/// tüm kanallar aynı kararı paylaşır. İş parçacığı güvenlidir; ayıklama süresi dolan uç nokta okunurken kendiliğinden geri döner.
/// </summary>
public sealed class AegisOutlierDetector
{
    private readonly AegisOutlierDetectionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, EndpointHealth> _endpoints = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Dedektörü oluşturur.</summary>
    public AegisOutlierDetector(AegisOutlierDetectionOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new AegisOutlierDetectionOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Uç nokta şu an ayıklanmış mı.</summary>
    public bool IsEjected(string endpoint) =>
        _endpoints.TryGetValue(endpoint, out var health) && health.IsEjectedAt(_timeProvider.GetUtcNow());

    /// <summary>Başarılı çağrıyı kaydeder (art arda hata sayacı sıfırlanır).</summary>
    public void RecordSuccess(string endpoint)
    {
        if (_endpoints.TryGetValue(endpoint, out var health))
        {
            health.RecordSuccess();
        }
    }

    /// <summary>
    /// Sunucu hatasını kaydeder (sunucu tarafı gRPC durumu ya da bağlantı hatası). Eşiğe ulaşılırsa ve ayıklama sınırı izin verirse
    /// uç nokta ayıklanır.
    /// </summary>
    public void RecordFailure(string endpoint, int knownEndpoints)
    {
        var health = _endpoints.GetOrAdd(endpoint, static _ => new EndpointHealth());
        var now = _timeProvider.GetUtcNow();
        if (health.RecordFailure() >= _options.ConsecutiveFailures && !health.IsEjectedAt(now) && CanEjectMore(knownEndpoints, now))
        {
            health.Eject(now, _options);
        }
    }

    /// <summary>gRPC durumunu sınıflandırıp kaydeder: sunucu tarafı hata sayılır, diğerleri başarı.</summary>
    public void Record(string endpoint, StatusCode status, int knownEndpoints)
    {
        if (AegisGrpcTransientErrors.IsServerFault(status))
        {
            RecordFailure(endpoint, knownEndpoints);
        }
        else
        {
            RecordSuccess(endpoint);
        }
    }

    // En az bir uç nokta ayıklanabilir; üst sınır havuzun MaxEjectionPercent'i (Envoy).
    private bool CanEjectMore(int knownEndpoints, DateTimeOffset now)
    {
        var ejected = _endpoints.Values.Count(h => h.IsEjectedAt(now));
        var allowed = Math.Max(1, knownEndpoints * _options.MaxEjectionPercent / 100);
        return ejected < allowed && ejected < knownEndpoints - 1;
    }

    private sealed class EndpointHealth
    {
        private readonly object _lock = new();
        private int _consecutiveFailures;
        private int _ejections;
        private DateTimeOffset _ejectedUntil = DateTimeOffset.MinValue;

        public bool IsEjectedAt(DateTimeOffset now)
        {
            lock (_lock)
            {
                return now < _ejectedUntil;
            }
        }

        public void RecordSuccess()
        {
            lock (_lock)
            {
                _consecutiveFailures = 0;
            }
        }

        public int RecordFailure()
        {
            lock (_lock)
            {
                return ++_consecutiveFailures;
            }
        }

        // Ayıklama süresi her seferinde büyür (taban × sayı), üst sınırlıdır; sayaç sıfırlanır ki dönüşte yeniden ölçülsün.
        public void Eject(DateTimeOffset now, AegisOutlierDetectionOptions options)
        {
            lock (_lock)
            {
                _ejections++;
                var duration = TimeSpan.FromTicks(Math.Min(options.MaxEjectionTime.Ticks, options.BaseEjectionTime.Ticks * _ejections));
                _ejectedUntil = now + duration;
                _consecutiveFailures = 0;
            }
        }
    }
}
