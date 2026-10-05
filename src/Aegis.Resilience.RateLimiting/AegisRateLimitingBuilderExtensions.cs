using System.Threading.RateLimiting;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.RateLimiting;

/// <summary>
/// <see cref="System.Threading.RateLimiting"/> sınırlayıcılarını boru hattına ekleyen metotlar (Polly.RateLimiting eşdeğeri).
/// Aegis'in kendi <c>AddRateLimiter(permitLimit, window)</c>, <c>AddSlidingWindowRateLimiter</c> ve
/// <c>AddConcurrencyLimiter</c> stratejileri bu paketten bağımsız olarak çalışmaya devam eder.
/// </summary>
public static class AegisRateLimitingBuilderExtensions
{
    /// <summary>
    /// .NET'in bir <see cref="RateLimiter"/> örneğiyle hız sınırı (Polly: <c>AddRateLimiter(RateLimiter)</c>).
    /// Ör. <c>new TokenBucketRateLimiter(...)</c>, <c>new FixedWindowRateLimiter(...)</c>, <c>new SlidingWindowRateLimiter(...)</c>.
    /// Sınırlayıcının ömrü çağırana aittir; boru hattıyla dispose edilmesi için <c>o.OwnedLimiter = limiter</c> verin.
    /// </summary>
    public static IAegisPipelineBuilder AddRateLimiter(
        this IAegisPipelineBuilder builder,
        RateLimiter limiter,
        Action<RateLimitingBridgeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(limiter);
        var options = new RateLimitingBridgeOptions { LeaseFactory = (_, ct) => limiter.AcquireAsync(1, ct) };
        configure?.Invoke(options);
        return builder.AddStrategy(new RateLimitingBridgeStrategy(options));
    }

    /// <summary>Bağlama göre bölümlenmiş .NET sınırlayıcısıyla hız sınırı (kiracı / kullanıcı / uç nokta başına kota).</summary>
    public static IAegisPipelineBuilder AddRateLimiter(
        this IAegisPipelineBuilder builder,
        PartitionedRateLimiter<AegisContext> limiter,
        Action<RateLimitingBridgeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(limiter);
        var options = new RateLimitingBridgeOptions { LeaseFactory = (ctx, ct) => limiter.AcquireAsync(ctx, 1, ct) };
        configure?.Invoke(options);
        return builder.AddStrategy(new RateLimitingBridgeStrategy(options));
    }

    /// <summary>Tam denetimli köprü (Polly: <c>RateLimiterStrategyOptions</c>): izin alma fonksiyonunu siz verirsiniz.</summary>
    public static IAegisPipelineBuilder AddRateLimiter(this IAegisPipelineBuilder builder, RateLimitingBridgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddStrategy(new RateLimitingBridgeStrategy(options));
    }

    /// <summary>
    /// .NET <see cref="TokenBucketRateLimiter"/> ile hız sınırı; sınırlayıcı boru hattına aittir ve onunla dispose edilir.
    /// </summary>
    public static IAegisPipelineBuilder AddTokenBucketRateLimiter(
        this IAegisPipelineBuilder builder,
        TokenBucketRateLimiterOptions limiterOptions,
        Action<RateLimitingBridgeOptions>? configure = null)
    {
        var limiter = new TokenBucketRateLimiter(limiterOptions);
        return builder.AddRateLimiter(limiter, o =>
        {
            o.OwnedLimiter = limiter;
            configure?.Invoke(o);
        });
    }

    /// <summary>.NET <see cref="FixedWindowRateLimiter"/> ile hız sınırı; sınırlayıcı boru hattına aittir.</summary>
    public static IAegisPipelineBuilder AddFixedWindowRateLimiter(
        this IAegisPipelineBuilder builder,
        FixedWindowRateLimiterOptions limiterOptions,
        Action<RateLimitingBridgeOptions>? configure = null)
    {
        var limiter = new FixedWindowRateLimiter(limiterOptions);
        return builder.AddRateLimiter(limiter, o =>
        {
            o.OwnedLimiter = limiter;
            configure?.Invoke(o);
        });
    }
}
