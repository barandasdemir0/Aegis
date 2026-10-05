using Aegis.Resilience.Core.Abstractions;

namespace Aegis.Resilience.Distributed.Abstractions;

/// <summary>
/// Dağıtık dayanıklılık stratejileri için boru hattı (Pipeline) genişletme metotları.
/// </summary>
public static class DistributedPipelineBuilderExtensions
{
    /// <summary>
    /// Devre kesici durumunu verilen <see cref="ICircuitBreakerStateStore"/> üzerinden tüm pod'lar arasında
    /// paylaşan dağıtık Circuit Breaker stratejisini boru hattına ekler.
    /// </summary>
    public static IAegisPipelineBuilder AddDistributedCircuitBreaker(
        this IAegisPipelineBuilder builder,
        ICircuitBreakerStateStore stateStore,
        Action<DistributedCircuitBreakerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(stateStore);

        var options = new DistributedCircuitBreakerOptions();
        configure?.Invoke(options);

        return builder.AddStrategy(new DistributedCircuitBreakerStrategy(stateStore, options));
    }

    /// <summary>
    /// Devre kesici durumunu verilen <see cref="ICircuitBreakerStateStore"/> üzerinden paylaşan
    /// dağıtık Circuit Breaker stratejisini boru hattına ekler.
    /// </summary>
    public static IAegisPipelineBuilder AddDistributedCircuitBreaker(
        this IAegisPipelineBuilder builder,
        ICircuitBreakerStateStore stateStore,
        DistributedCircuitBreakerOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(options);

        return builder.AddStrategy(new DistributedCircuitBreakerStrategy(stateStore, options));
    }

    /// <summary>
    /// Kotayı verilen <see cref="IDistributedRateLimitStore"/> üzerinden tüm pod'lar arasında paylaşan hız sınırlayıcıyı
    /// ekler. Örnek: <c>.AddDistributedRateLimiter(store, o =&gt; { o.LimiterKey = "stripe"; o.PermitLimit = 50; })</c>.
    /// </summary>
    public static IAegisPipelineBuilder AddDistributedRateLimiter(
        this IAegisPipelineBuilder builder,
        IDistributedRateLimitStore store,
        Action<DistributedRateLimiterOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(store);

        var options = new DistributedRateLimiterOptions();
        configure?.Invoke(options);
        return builder.AddStrategy(new DistributedRateLimiterStrategy(store, options));
    }
}
