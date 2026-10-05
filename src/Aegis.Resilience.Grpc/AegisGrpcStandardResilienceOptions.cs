using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Core.Strategies.Timeout;

namespace Aegis.Resilience.Grpc;

/// <summary>
/// Standart gRPC istemci zinciri (Microsoft standart HTTP işleyicisinin sırası, gRPC kurallarıyla): eşzamanlılık sınırı → toplam
/// zaman aşımı → retry → devre kesici → deneme zaman aşımı. gRPC deadline'ı verilmişse o da tüm denemeleri kapsar; hangisi önce
/// dolarsa o geçerlidir.
/// </summary>
public sealed class AegisGrpcStandardResilienceOptions
{
    /// <summary>Eşzamanlı çağrı sınırı (varsayılan 1000; dolunca beklemeden ret → <c>ResourceExhausted</c>).</summary>
    public ConcurrencyLimiterOptions RateLimiter { get; set; } = new() { MaxConcurrentExecutions = 1000 };

    /// <summary>Tüm denemeler dahil üst süre (varsayılan 30 sn).</summary>
    public TimeoutOptions TotalRequestTimeout { get; set; } = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Yeniden deneme: <see cref="AegisGrpcTransientErrors.ShouldRetry"/> (yalnızca <c>Unavailable</c> ve deneme zaman aşımı;
    /// sunucu negatif pushback verdiyse hayır), sunucu pushback'i gecikme olarak kullanılır. 3 deneme, 1 sn taban, üstel + jitter.
    /// Retry fırtınasına karşı <see cref="RetryOptions.Budget"/> verilebilir (gRPC retry throttling).
    /// </summary>
    public RetryOptions Retry { get; set; } = new()
    {
        MaxRetryAttempts = 3,
        Delay = TimeSpan.FromSeconds(1),
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        ShouldHandle = AegisGrpcTransientErrors.ShouldRetry
    };

    /// <summary>
    /// Devre kesici: yalnızca sunucu tarafı hatalar (<see cref="AegisGrpcTransientErrors.IsCircuitFailure"/>); istemci hataları
    /// (<c>InvalidArgument</c>, <c>NotFound</c>, <c>PermissionDenied</c>...) devreyi açmaz.
    /// </summary>
    public CircuitBreakerOptions CircuitBreaker { get; set; } = new() { ShouldHandle = AegisGrpcTransientErrors.IsCircuitFailure };

    /// <summary>Deneme başına süre (varsayılan 10 sn); dolunca deneme yeniden denenir.</summary>
    public TimeoutOptions AttemptTimeout { get; set; } = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Seçenekleri ve tutarlılığı doğrular (deneme süresi toplamdan kısa olmalı).</summary>
    public void Validate()
    {
        RateLimiter.Validate();
        TotalRequestTimeout.Validate();
        Retry.Validate();
        CircuitBreaker.Validate();
        AttemptTimeout.Validate();
        if (AttemptTimeout.Timeout >= TotalRequestTimeout.Timeout && TotalRequestTimeout.Timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentException(
                $"{nameof(AegisGrpcStandardResilienceOptions)}: deneme zaman aşımı ({AttemptTimeout.Timeout}) toplam zaman aşımından " +
                $"({TotalRequestTimeout.Timeout}) kısa olmalı; aksi halde hiçbir yeniden deneme yapılamaz.");
        }
    }

    /// <summary>Seçeneklerden standart zinciri kurar.</summary>
    public void Configure(IAegisPipelineBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        Validate();
        builder
            .AddStrategy(new ConcurrencyLimiterStrategy(RateLimiter))
            .AddTimeout(TotalRequestTimeout)
            .AddRetry(Retry)
            .AddCircuitBreaker(CircuitBreaker)
            .AddTimeout(AttemptTimeout);
    }
}
