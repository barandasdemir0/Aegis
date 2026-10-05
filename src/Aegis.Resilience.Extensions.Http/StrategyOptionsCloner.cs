using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.RateLimiter;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// Anahtar (authority / uç nokta) başına ayrı boru hattı kurulurken seçenekleri kopyalar. Durum stratejide tutulduğu için
/// kopya şart değildir; ancak <see cref="CircuitBreakerStateProvider"/> tek devreye bağlanabildiğinden kopyada bırakılmaz.
/// <see cref="CircuitBreakerManualControl"/> korunur: tek kontrol tüm anahtarların devrelerini birlikte yönetir.
/// </summary>
internal static class StrategyOptionsCloner
{
    public static CircuitBreakerOptions CloneForKey(CircuitBreakerOptions source) => new()
    {
        FailureRatio = source.FailureRatio,
        SamplingDuration = source.SamplingDuration,
        MinimumThroughput = source.MinimumThroughput,
        BreakDuration = source.BreakDuration,
        BreakDurationGenerator = source.BreakDurationGenerator,
        ShouldHandle = source.ShouldHandle,
        ShouldHandleResult = source.ShouldHandleResult,
        ShouldHandleOutcome = source.ShouldHandleOutcome,
        OnOpened = source.OnOpened,
        OnClosed = source.OnClosed,
        OnHalfOpened = source.OnHalfOpened,
        SlowCallDurationThreshold = source.SlowCallDurationThreshold,
        SlowCallRateThreshold = source.SlowCallRateThreshold,
        ConsecutiveFailureThreshold = source.ConsecutiveFailureThreshold,
        HalfOpenSuccessThreshold = source.HalfOpenSuccessThreshold,
        SamplingCount = source.SamplingCount,
        Mode = source.Mode,
        ManualControl = source.ManualControl,
        OptionsProvider = source.OptionsProvider
    };

    public static ConcurrencyLimiterOptions CloneForKey(ConcurrencyLimiterOptions source) => new()
    {
        MaxConcurrentExecutions = source.MaxConcurrentExecutions,
        QueueLimit = source.QueueLimit,
        QueueTimeout = source.QueueTimeout,
        OnRejected = source.OnRejected,
        OptionsProvider = source.OptionsProvider
    };
}
