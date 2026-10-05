using System.Diagnostics;
using System.Runtime.CompilerServices;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Strategies.RateLimiter;

/// <summary>
/// Gecikme sürelerini (RTT) canlı ölçerek hedef servisin kapasitesine göre eşzamanlılık limitini
/// otonom olarak daraltıp genişleten Netflix Gradient tabanlı adaptif hız sınırlayıcı.
/// </summary>
public sealed class AdaptiveConcurrencyStrategy : AegisStrategy
{
    private readonly AdaptiveConcurrencyOptions _options;

    /// <inheritdoc />
    public override object? Options => _options;
    private readonly AegisLock _lock = new();

    private double _currentLimit;
    private double _minRttMs = double.MaxValue;
    private double _smoothedRttMs;
    private long _sampleCount;
    private double _pendingMinRttMs = double.MaxValue; // tek aykırı örneğin minRtt olmasını engeller (AEGIS-127)
    private int _activeExecutions;
    private long _lastMinRttResetTimestamp = Stopwatch.GetTimestamp();
    private static readonly TimeSpan MinRttResetInterval = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    protected override void OnTimeProviderChanged() => Interlocked.Exchange(ref _lastMinRttResetTimestamp, GetTimestamp());

    public override string Name => "AdaptiveConcurrency";

    /// <summary>
    /// Hâlihazırda uygulanan eşzamanlılık limiti.
    /// <c>double</c> alanlar C# bellek modelinde atomik okuma garantisi taşımadığı için kilit altında okunur (AEGIS-124).
    /// </summary>
    public int CurrentLimit
    {
        get
        {
            lock (_lock)
            {
                return (int)Math.Round(_currentLimit);
            }
        }
    }

    /// <summary>
    /// O an yürütülmekte olan işlem sayısı.
    /// </summary>
    public int ActiveExecutions => Volatile.Read(ref _activeExecutions);

    private double _candidateMinRttMs = double.MaxValue;

    public AdaptiveConcurrencyStrategy(AdaptiveConcurrencyOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate(); // fail-fast: geçersiz yapılandırma kurulum anında bildirilir (AEGIS-130)
        _currentLimit = options.InitialConcurrency;
    }

    // En sık yol (boşta kapasite var, iptal yok, geri çağrı senkron tamamlanır) async değildir: izin alınır, sonuç doğrudan
    // döner ve ölçüm kilit altında kaydedilir. Kapasite doluysa, iptal edilmişse ya da geri çağrı beklemeliyse
    // ExecuteQueuedAsync / AwaitAndReleaseAsync devralır (kuyruk, gecikme, reddetme).
    protected override ValueTask<Outcome<TResult>> ExecuteCoreAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        if (context.CancellationToken.IsCancellationRequested || !TryEnter())
        {
            return ExecuteQueuedAsync(callback, context, state);
        }

        var startTimestamp = GetTimestamp();
        ValueTask<Outcome<TResult>> pending;
        try
        {
            pending = callback(context, state);
        }
        catch
        {
            Release(startTimestamp);
            throw;
        }

        if (!pending.IsCompletedSuccessfully)
        {
            return AwaitAndReleaseAsync(pending, startTimestamp, context);
        }

        var outcome = pending.Result;
        Release(startTimestamp);
        return new ValueTask<Outcome<TResult>>(outcome);
    }

    /// <summary>Boşta kapasite varsa bir izin alır (kilit altında; <c>double</c> limit atomik okunamaz).</summary>
    private bool TryEnter()
    {
        lock (_lock)
        {
            if (_activeExecutions >= (int)Math.Round(_currentLimit))
            {
                return false;
            }

            _activeExecutions++;
            return true;
        }
    }

    /// <summary>İzni bırakır ve ölçülen RTT ile limiti ayarlar (Self-Tuning Gradient).</summary>
    private void Release(long startTimestamp)
    {
        var elapsedMs = GetElapsedTime(startTimestamp).TotalMilliseconds;
        lock (_lock)
        {
            _activeExecutions = Math.Max(0, _activeExecutions - 1);
            UpdateConcurrencyLimit(elapsedMs);
        }
    }

#if !AEGIS_LEGACY // .NET 8+: iç katman yalnızca Aegis tarafından tek sefer beklenir; async durum makinesi kutusu havuzlanır
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<Outcome<TResult>> AwaitAndReleaseAsync<TResult>(
        ValueTask<Outcome<TResult>> pending, long startTimestamp, AegisContext context)
    {
        try
        {
            return await pending.ConfigureAwait(context.ContinueOnCapturedContext);
        }
        finally
        {
            Release(startTimestamp);
        }
    }

    private async ValueTask<Outcome<TResult>> ExecuteQueuedAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        // 1. Kapasite kontrolü ve QueueTimeout desteği (AEGIS-008)
        var waitStart = GetTimestamp();
        var acquired = false;

        while (!acquired)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            lock (_lock)
            {
                // Kilit zaten tutulduğu için alana doğrudan erişilir (CurrentLimit özelliği tekrar kilit alırdı)
                if (_activeExecutions < (int)Math.Round(_currentLimit))
                {
                    _activeExecutions++;
                    acquired = true;
                    break;
                }
            }

            if (_options.QueueTimeout <= TimeSpan.Zero || GetElapsedTime(waitStart) >= _options.QueueTimeout)
            {
                break;
            }

            var remaining = _options.QueueTimeout - GetElapsedTime(waitStart);
            var sleepTime = remaining < TimeSpan.FromMilliseconds(10) ? remaining : TimeSpan.FromMilliseconds(10);
            if (sleepTime > TimeSpan.Zero)
            {
                await Task.Delay(AegisTimers.Normalize(sleepTime), TimeProvider, context.CancellationToken).ConfigureAwait(context.ContinueOnCapturedContext);
            }
        }

        if (!acquired)
        {
            return await RateLimiterRejection.RejectAsync<TResult>(Telemetry, context, Name, _options.OnRejected,
                $"Adaptif hız sınırı ({CurrentLimit}) aşıldı. Hedef servis gecikmesi sebebiyle kapasite daraltıldı.",
                retryAfter: null).ConfigureAwait(context.ContinueOnCapturedContext);
        }

        var startTimestamp = GetTimestamp();

        try
        {
            return await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
        }
        finally
        {
            Release(startTimestamp); // 2. Kendi kendini ayarlama (Self-Tuning Gradient matematiği)
        }
    }

    /// <summary>
    /// Yalnızca testler için: gerçek zaman ölçümü yapmadan algoritmaya deterministik bir RTT örneği besler.
    /// </summary>
    internal void RecordSampleForTesting(double measuredRttMs)
    {
        lock (_lock)
        {
            UpdateConcurrencyLimit(measuredRttMs);
        }
    }

    /// <summary>
    /// Ölçülebilir en küçük RTT (1 mikrosaniye). Stopwatch çözünürlüğü altında kalan (0.0 ms ölçülen) örnekler
    /// bu tabana çekilir.
    /// </summary>
    private const double MinMeasurableRttMs = 0.001;

    private void UpdateConcurrencyLimit(double measuredRttMs)
    {
        // AEGIS-132: Senkron tamamlanan çağrılarda (önbellek isabeti, ValueTask.FromResult) Stopwatch iki ardışık
        // okumada AYNI zaman damgasını verebilir -> ölçülen RTT tam 0.0 olur. Bu değer minRTT'ye yazıldığında
        // aşağıdaki gradyan kararı (_minRttMs > 0 koruması) bir daha hiç çalışmıyor ve limit MinRttResetInterval
        // (5 dk) boyunca DONUYORDU: hedef servis çökse bile limit daralmıyordu. Yük testinde "10 -> 10" olarak gözlemlendi.
        if (double.IsNaN(measuredRttMs) || measuredRttMs < MinMeasurableRttMs)
        {
            measuredRttMs = MinMeasurableRttMs;
        }

        _sampleCount++;

        TrackMinRtt(measuredRttMs);
        UpdateSmoothedRtt(measuredRttMs);

        // Isınma bitmeden limit değiştirilmez (AEGIS-127)
        if (_sampleCount >= Math.Max(1, _options.WarmupSamples))
        {
            ApplyGradient();
        }
    }

    /// <summary>MinRTT takibi (aykırı değer korumalı): pencere minimumu, periyodik yenileme ve iki örnekle doğrulama.</summary>
    private void TrackMinRtt(double measuredRttMs)
    {
        if (measuredRttMs < _candidateMinRttMs)
        {
            _candidateMinRttMs = measuredRttMs;
        }

        var elapsedSinceReset = GetElapsedTime(_lastMinRttResetTimestamp);
        if (elapsedSinceReset >= MinRttResetInterval)
        {
            // Periyodik yenileme: minRTT'nin sonsuza kadar eski bir "altın çağ" değerine takılı kalmasını engelle (AEGIS-008).
            // AEGIS-143: Pencere boyunca gözlenen gerçek minimum DOĞRUDAN benimsenir. Eski kod yükselişi %10 ile
            // sınırlıyordu (Math.Min(min*1.1, candidate)); ortam kalıcı olarak 1ms -> 15ms'ye kaydığında baseline'ın
            // toparlanması saatler alıyor, limit bu sürede MinConcurrency'de kilitli kalıyordu. Aday 5 dakikalık
            // pencerenin MİNİMUMU olduğu için tek bir yavaş sapma onu yükseltemez; yalnızca kalıcı kayma yansır.
            _minRttMs = _candidateMinRttMs < double.MaxValue ? _candidateMinRttMs : _minRttMs;
            _candidateMinRttMs = double.MaxValue;
            _pendingMinRttMs = double.MaxValue;
            _lastMinRttResetTimestamp = GetTimestamp();
        }
        else if (measuredRttMs < _minRttMs)
        {
            // AEGIS-127: TEK bir anormal hızlı örnek (ör. Windows zamanlayıcısının erken tetiklemesi,
            // önbellekten dönen tek yanıt) minRTT'yi anında düşürürse, kararlı durumdaki tüm normal
            // yanıtlar "N kat yavaşlama" gibi görünür ve limit her örnekte yarılanarak MinConcurrency'ye
            // çöker — test host'ta 6.8ms'lik tek bir örneğin limiti 10'dan 2'ye indirdiği gözlemlendi.
            // Kural: yeni minimum ancak ARDIŞIK İKİ örnek tarafından doğrulanırsa kabul edilir.
            if (_pendingMinRttMs < double.MaxValue && measuredRttMs <= _pendingMinRttMs * 1.5)
            {
                _minRttMs = Math.Min(measuredRttMs, _pendingMinRttMs);
                _pendingMinRttMs = double.MaxValue;
            }
            else if (_minRttMs == double.MaxValue)
            {
                _minRttMs = measuredRttMs; // ilk örnek: doğrulama beklenemez
            }
            else
            {
                _pendingMinRttMs = measuredRttMs; // aday; bir sonraki örnek onaylarsa kabul edilecek
            }
        }
        else
        {
            _pendingMinRttMs = double.MaxValue; // aday doğrulanmadı -> tek aykırı değerdi, at
        }
    }

    /// <summary>Hareketli ortalama (EMA); ısınma boyunca basit ortalamayla tohumlanır.</summary>
    private void UpdateSmoothedRtt(double measuredRttMs)
    {
        var warmupSamples = Math.Max(1, _options.WarmupSamples);
        if (_sampleCount <= warmupSamples)
        {
            // Isınma: soğuk başlangıç örnekleri (JIT, bağlantı kurulumu) EMA'yı tek başına şişirmesin;
            // basit ortalama ile tohumla.
            _smoothedRttMs = _smoothedRttMs <= 0
                ? measuredRttMs
                : _smoothedRttMs + (measuredRttMs - _smoothedRttMs) / _sampleCount;
        }
        else
        {
            _smoothedRttMs = (1 - _options.SmoothingFactor) * _smoothedRttMs + _options.SmoothingFactor * measuredRttMs;
        }
    }

    /// <summary>Gradient kararı (Netflix Gradient2 ile uyumlu): tolerans altında genişlet, üstünde aşım oranında kıs.</summary>
    private void ApplyGradient()
    {
        if (_minRttMs > 0 && _minRttMs < double.MaxValue && _smoothedRttMs > 0)
        {
            // Tolerans çizgisi: minRTT + max(mutlak taban, minRTT * oran). Varsayılan oran 1.0 =>
            // ölçülen ortalama gecikme minRTT'nin 2 KATINA kadar "sağlıklı" sayılır. Bu, Netflix
            // Gradient2'deki varsayılan tolerance=2.0 ile aynıdır (AEGIS-122 / AEGIS-127).
            var toleratedRttMs = _minRttMs + Math.Max(_options.MinRttJitterToleranceMs, _minRttMs * _options.RttJitterToleranceRatio);

            if (_smoothedRttMs <= toleratedRttMs)
            {
                // Sistem hızlı ve rahat -> Limiti yavaşça genişlet (additive increase)
                _currentLimit = Math.Min(_options.MaxConcurrency, _currentLimit + 0.5);
            }
            else
            {
                // Gecikme tolerans çizgisini AŞTI -> Limiti aşım oranında kıs (multiplicative decrease).
                // Gradyan, ham minRTT'ye değil tolerans çizgisine göre hesaplanır: böylece çizginin hemen
                // üzerindeki hafif aşımlar %5-10 gibi yumuşak kesintiler üretir, gerçek çöküşlerde ise
                // 0.5 tabanıyla hızlı daralma korunur.
                var gradient = Math.Max(0.5, Math.Min(1.0, toleratedRttMs / _smoothedRttMs));
                _currentLimit = Math.Max(_options.MinConcurrency, _currentLimit * gradient);
            }
        }
    }
}
