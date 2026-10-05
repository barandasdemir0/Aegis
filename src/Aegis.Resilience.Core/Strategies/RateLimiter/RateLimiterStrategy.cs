using System.Diagnostics;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Strategies.RateLimiter;

/// <summary>
/// Yüksek performanslı ve sıfır-dış-bağımlılıklı Token Bucket algoritması tabanlı zaman pencereli hız sınırlayıcı.
/// Belirli bir zaman aralığında hedef servise izin verilenden fazla istek akmasını önler.
/// </summary>
public sealed class RateLimiterStrategy : AegisStrategy
{
    private readonly RateLimiterOptions _staticOptions;

    /// <inheritdoc />
    public override object? Options => _staticOptions;
    private readonly AegisLock _lock = new();

    private double _availableTokens;
    private long _lastRefillTimestamp;

    private RateLimiterOptions? _lastGoodOptions;

    /// <summary>Canlı seçenekleri doğrulayarak çözer; geçersizse son geçerli seçeneklerle devam eder (AEGIS-145).</summary>
    private RateLimiterOptions ResolveOptions() => DynamicOptionsResolver.Resolve(_staticOptions, _staticOptions.OptionsProvider, static o => o.Validate(), ref _lastGoodOptions);

    public override string Name => "RateLimiter";

    /// <inheritdoc />
    protected override void OnTimeProviderChanged()
    {
        lock (_lock)
        {
            _lastRefillTimestamp = GetTimestamp();
        }
    }

    public double AvailableTokens
    {
        get
        {
            var options = ResolveOptions();
            lock (_lock)
            {
                RefillTokens(options);
                return _availableTokens;
            }
        }
    }

    public RateLimiterStrategy(RateLimiterOptions options)
    {
        _staticOptions = options ?? throw new ArgumentNullException(nameof(options));
        _staticOptions.Validate(); // fail-fast: geçersiz yapılandırma kurulum anında bildirilir (AEGIS-130)
        _availableTokens = Math.Max(1, options.PermitLimit);
        _lastRefillTimestamp = GetTimestamp();
    }

    // Hızlı yol async değildir: token varsa geri çağrının ValueTask'ı doğrudan döner (ek async durum makinesi yok).
    // Bekleme ve red yolu AcquireSlowAsync'tedir.
    protected override ValueTask<Outcome<TResult>> ExecuteCoreAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        if (context.CancellationToken.IsCancellationRequested)
        {
            return new ValueTask<Outcome<TResult>>(Outcome<TResult>.FromException(new OperationCanceledException(context.CancellationToken)));
        }

        var options = ResolveOptions();
        return TryAcquire(options) ? callback(context, state) : AcquireSlowAsync(callback, context, state, options);
    }

    private bool TryAcquire(RateLimiterOptions options)
    {
        lock (_lock)
        {
            RefillTokens(options);
            if (_availableTokens >= 1.0)
            {
                _availableTokens -= 1.0;
                return true;
            }
        }

        return false;
    }

    private async ValueTask<Outcome<TResult>> AcquireSlowAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state,
        RateLimiterOptions options)
    {
        // Token yetersiz: kuyruk süresi varsa token dolana kadar bekle, yoksa (ya da süre dolunca) reddet.
        var acquired = false;
        if (options.QueueTimeout > TimeSpan.Zero)
        {
            var waitStart = GetTimestamp();
            while (!acquired)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                var elapsed = GetElapsedTime(waitStart);
                if (elapsed >= options.QueueTimeout)
                {
                    break;
                }

                // Spin-wait yerine gereken token dolum süresini hesapla (AEGIS-007)
                TimeSpan delayTime;
                lock (_lock)
                {
                    RefillTokens(options);
                    if (_availableTokens >= 1.0)
                    {
                        _availableTokens -= 1.0;
                        acquired = true;
                        break;
                    }

                    var window = options.Window > TimeSpan.Zero ? options.Window : TimeSpan.FromMinutes(1);
                    var permitLimit = Math.Max(1, options.PermitLimit);
                    var refillRatePerSecond = permitLimit / window.TotalSeconds;
                    var missingTokens = Math.Max(0.01, 1.0 - _availableTokens);
                    var secondsNeeded = refillRatePerSecond > 0 ? missingTokens / refillRatePerSecond : 0.05;

                    var remainingTimeout = options.QueueTimeout - elapsed;
                    var calculatedDelay = TimeSpan.FromSeconds(Math.Min(1.0, Math.Max(0.005, secondsNeeded)));
                    delayTime = calculatedDelay < remainingTimeout ? calculatedDelay : remainingTimeout;
                }

                if (delayTime <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(AegisTimers.Normalize(delayTime), TimeProvider, context.CancellationToken).ConfigureAwait(context.ContinueOnCapturedContext);
                acquired = TryAcquire(options);
            }
        }

        if (!acquired)
        {
            return await RateLimiterRejection.RejectAsync<TResult>(Telemetry, context, Name, options.OnRejected,
                $"Hız kotası aşıldı: '{context.PipelineName ?? "default"}' boru hattında {options.Window.TotalSeconds:F0} saniyelik zaman penceresinde en fazla {options.PermitLimit} isteğe izin verilmektedir.",
                EstimateRetryAfter(options)).ConfigureAwait(context.ContinueOnCapturedContext);
        }

        return await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
    }

    /// <summary>Bir sonraki token'ın dolmasına kalan süre (Polly: token bucket RetryAfter metaverisi).</summary>
    private TimeSpan EstimateRetryAfter(RateLimiterOptions options)
    {
        lock (_lock)
        {
            RefillTokens(options);
            var window = options.Window > TimeSpan.Zero ? options.Window : TimeSpan.FromMinutes(1);
            var refillRatePerSecond = Math.Max(1, options.PermitLimit) / window.TotalSeconds;
            var missingTokens = Math.Max(0, 1.0 - _availableTokens);
            return TimeSpan.FromSeconds(missingTokens / refillRatePerSecond);
        }
    }

    private void RefillTokens(RateLimiterOptions options)
    {
        var now = GetTimestamp();
        var elapsed = TimeProvider.GetElapsedTime(_lastRefillTimestamp, now);
        var window = options.Window > TimeSpan.Zero ? options.Window : TimeSpan.FromMinutes(1);
        var permitLimit = Math.Max(1, options.PermitLimit);

        var refillRatePerSecond = permitLimit / window.TotalSeconds;
        var tokensToAdd = elapsed.TotalSeconds * refillRatePerSecond;

        if (tokensToAdd > 0)
        {
            _availableTokens = Math.Min(permitLimit, _availableTokens + tokensToAdd);
            _lastRefillTimestamp = now;
        }
    }
}
