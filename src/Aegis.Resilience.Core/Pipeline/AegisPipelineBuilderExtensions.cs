using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Strategies.Cache;
using Aegis.Resilience.Core.Strategies.Chaos;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.Collapser;
using Aegis.Resilience.Core.Strategies.Fallback;
using Aegis.Resilience.Core.Strategies.Hedging;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Core.Strategies.Timeout;

namespace Aegis.Resilience.Core.Pipeline;

/// <summary>
/// Pipeline Builder için pratik ve akıcı (Fluent) genişletme metotları.
/// </summary>
public static class AegisPipelineBuilderExtensions
{
    /// <summary>
    /// Boru hattının saatini ayarlar (Polly: <c>builder.TimeProvider = ...</c>). Somut <see cref="AegisPipelineBuilder"/>
    /// ile çağrı sırası önemsizdir (Build anında tüm stratejilere uygulanır); diğer builder'larda o ana kadar eklenmiş
    /// stratejilere hemen uygulanır.
    /// </summary>
    /// <summary>
    /// Telemetriyi yapılandırır (Polly: <c>ConfigureTelemetry</c>): olay dinleyicileri, metrik zenginleştiricileri, önem
    /// sağlayıcı. Yalnızca somut <see cref="AegisPipelineBuilder"/> destekler.
    /// </summary>
    public static IAegisPipelineBuilder WithTelemetry(this IAegisPipelineBuilder builder, Action<Telemetry.AegisTelemetryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        if (builder is not AegisPipelineBuilder concrete)
        {
            throw new NotSupportedException($"{nameof(WithTelemetry)} yalnızca {nameof(AegisPipelineBuilder)} ile kullanılabilir.");
        }

        configure(concrete.TelemetryOptions ??= new Telemetry.AegisTelemetryOptions());
        return builder;
    }

    /// <summary>Boru hattı örneğinin adını ayarlar (bkz. <see cref="AegisPipelineBuilder.InstanceName"/>).</summary>
    public static IAegisPipelineBuilder WithInstanceName(this IAegisPipelineBuilder builder, string instanceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);

        if (builder is not AegisPipelineBuilder concrete)
        {
            throw new NotSupportedException($"{nameof(WithInstanceName)} yalnızca {nameof(AegisPipelineBuilder)} ile kullanılabilir.");
        }

        concrete.InstanceName = instanceName;
        return builder;
    }

    public static IAegisPipelineBuilder WithTimeProvider(this IAegisPipelineBuilder builder, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (builder is AegisPipelineBuilder concrete)
        {
            concrete.TimeProvider = timeProvider;
            return builder;
        }

        foreach (var strategy in builder.Strategies)
        {
            if (strategy is AegisStrategy aegisStrategy)
            {
                aegisStrategy.UseTimeProvider(timeProvider);
            }
        }

        return builder;
    }

    public static IAegisPipelineBuilder AddRetry(this IAegisPipelineBuilder builder, Action<RetryOptions>? configure = null)
    {
        var options = new RetryOptions();
        configure?.Invoke(options);
        return builder.AddStrategy(new RetryStrategy(options));
    }

    public static IAegisPipelineBuilder AddRetry(this IAegisPipelineBuilder builder, RetryOptions options)
    {
        return builder.AddStrategy(new RetryStrategy(options));
    }

    public static IAegisPipelineBuilder AddCircuitBreaker(this IAegisPipelineBuilder builder, Action<CircuitBreakerOptions>? configure = null)
    {
        var options = new CircuitBreakerOptions();
        configure?.Invoke(options);
        return builder.AddStrategy(new CircuitBreakerStrategy(options));
    }

    public static IAegisPipelineBuilder AddCircuitBreaker(this IAegisPipelineBuilder builder, CircuitBreakerOptions options)
    {
        return builder.AddStrategy(new CircuitBreakerStrategy(options));
    }

    public static IAegisPipelineBuilder AddTimeout(this IAegisPipelineBuilder builder, TimeSpan timeout, Action<TimeoutOptions>? configure = null)
    {
        var options = new TimeoutOptions { Timeout = timeout };
        configure?.Invoke(options);
        return builder.AddStrategy(new TimeoutStrategy(options));
    }

    public static IAegisPipelineBuilder AddTimeout(this IAegisPipelineBuilder builder, TimeoutOptions options)
    {
        return builder.AddStrategy(new TimeoutStrategy(options));
    }

    public static IAegisPipelineBuilder AddConcurrencyLimiter(this IAegisPipelineBuilder builder, int maxConcurrent, Action<ConcurrencyLimiterOptions>? configure = null)
    {
        var options = new ConcurrencyLimiterOptions { MaxConcurrentExecutions = maxConcurrent };
        configure?.Invoke(options);
        return builder.AddStrategy(new ConcurrencyLimiterStrategy(options));
    }

    public static IAegisPipelineBuilder AddRateLimiter(
        this IAegisPipelineBuilder builder,
        int permitLimit,
        TimeSpan window,
        Action<RateLimiterOptions>? configure = null)
    {
        var options = new RateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window
        };
        configure?.Invoke(options);
        return builder.AddStrategy(new RateLimiterStrategy(options));
    }

    public static IAegisPipelineBuilder AddRateLimiter(this IAegisPipelineBuilder builder, RateLimiterOptions options)
    {
        return builder.AddStrategy(new RateLimiterStrategy(options));
    }

    public static IAegisPipelineBuilder AddSlidingWindowRateLimiter(
        this IAegisPipelineBuilder builder,
        int permitLimit,
        TimeSpan window,
        int segmentsPerWindow = 6,
        Action<SlidingWindowRateLimiterOptions>? configure = null)
    {
        var options = new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            SegmentsPerWindow = segmentsPerWindow
        };
        configure?.Invoke(options);
        return builder.AddStrategy(new SlidingWindowRateLimiterStrategy(options));
    }

    public static IAegisPipelineBuilder AddSlidingWindowRateLimiter(
        this IAegisPipelineBuilder builder,
        SlidingWindowRateLimiterOptions options)
    {
        return builder.AddStrategy(new SlidingWindowRateLimiterStrategy(options));
    }

    public static IAegisPipelineBuilder AddPartitionedRateLimiter(
        this IAegisPipelineBuilder builder,
        Action<PartitionedRateLimiterOptions>? configure = null)
    {
        var options = new PartitionedRateLimiterOptions();
        configure?.Invoke(options);
        return builder.AddStrategy(new PartitionedRateLimiterStrategy(options));
    }

    public static IAegisPipelineBuilder AddPartitionedRateLimiter(
        this IAegisPipelineBuilder builder,
        PartitionedRateLimiterOptions options)
    {
        return builder.AddStrategy(new PartitionedRateLimiterStrategy(options));
    }

    public static IAegisPipelineBuilder AddRequestCollapser(
        this IAegisPipelineBuilder builder,
        Action<RequestCollapserOptions>? configure = null)
    {
        var options = new RequestCollapserOptions();
        configure?.Invoke(options);
        return builder.AddStrategy(new RequestCollapserStrategy(options));
    }

    public static IAegisPipelineBuilder AddRequestCollapser(
        this IAegisPipelineBuilder builder,
        RequestCollapserOptions options)
    {
        return builder.AddStrategy(new RequestCollapserStrategy(options));
    }

    public static IAegisPipelineBuilder AddCache(
        this IAegisPipelineBuilder builder,
        Action<CacheOptions>? configure = null)
    {
        var options = new CacheOptions();
        configure?.Invoke(options);
        return builder.AddStrategy(new CacheStrategy(options));
    }

    public static IAegisPipelineBuilder AddCache(
        this IAegisPipelineBuilder builder,
        TimeSpan ttl,
        Action<CacheOptions>? configure = null)
    {
        var options = new CacheOptions { Ttl = ttl };
        configure?.Invoke(options);
        return builder.AddStrategy(new CacheStrategy(options));
    }

    public static IAegisPipelineBuilder AddCache(
        this IAegisPipelineBuilder builder,
        CacheOptions options)
    {
        return builder.AddStrategy(new CacheStrategy(options));
    }

    public static IAegisPipelineBuilder AddFallback(this IAegisPipelineBuilder builder, Action<FallbackOptions> configure)
    {
        var options = new FallbackOptions();
        configure.Invoke(options);
        return builder.AddStrategy(new FallbackStrategy(options));
    }

    public static IAegisPipelineBuilder AddHedging(this IAegisPipelineBuilder builder, Action<HedgingOptions>? configure = null)
    {
        var options = new HedgingOptions();
        configure?.Invoke(options);
        return builder.AddStrategy(new HedgingStrategy(options));
    }

    public static IAegisPipelineBuilder AddChaos(this IAegisPipelineBuilder builder, Action<ChaosOptions>? configure = null)
    {
        var options = new ChaosOptions();
        configure?.Invoke(options);
        return builder.AddStrategy(new ChaosStrategy(options));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Ayrık kaos stratejileri (Polly/Simmy: AddChaosFault / AddChaosLatency / AddChaosOutcome / AddChaosBehavior).
    // Her biri kendi oranı ve üreticileriyle ayrı bir strateji ekler; birleşik AddChaos aynen desteklenir.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Belirtilen oranda istisna enjekte eder (Polly: <c>AddChaosFault</c>).</summary>
    public static IAegisPipelineBuilder AddChaosFault(
        this IAegisPipelineBuilder builder, double injectionRate, Func<Exception> faultGenerator, Action<ChaosOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(faultGenerator);
        return builder.AddChaos(o =>
        {
            o.Enabled = true;
            o.InjectionRate = injectionRate;
            o.FaultGenerator = faultGenerator;
            configure?.Invoke(o);
        });
    }

    /// <summary>Belirtilen oranda gecikme enjekte eder (Polly: <c>AddChaosLatency</c>). Hata üretmez.</summary>
    public static IAegisPipelineBuilder AddChaosLatency(
        this IAegisPipelineBuilder builder, double injectionRate, TimeSpan latency, Action<ChaosOptions>? configure = null) =>
        builder.AddChaos(o =>
        {
            o.Enabled = true;
            o.InjectionRate = injectionRate;
            o.Latency = latency;
            o.FaultGenerator = null;
            configure?.Invoke(o);
        });

    /// <summary>Belirtilen oranda sahte sonuç döndürür (Polly: <c>AddChaosOutcome</c>). Asıl çağrı yapılmaz.</summary>
    public static IAegisPipelineBuilder AddChaosOutcome(
        this IAegisPipelineBuilder builder, double injectionRate, Func<Context.AegisContext, object?> resultGenerator, Action<ChaosOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(resultGenerator);
        return builder.AddChaos(o =>
        {
            o.Enabled = true;
            o.InjectionRate = injectionRate;
            o.ResultGenerator = resultGenerator;
            o.FaultGenerator = null;
            configure?.Invoke(o);
        });
    }

    /// <summary>Belirtilen oranda ağırlıklı istisna veya sonuç enjekte eder (Polly: <c>AddChaosOutcome(OutcomeGenerator)</c>).</summary>
    public static IAegisPipelineBuilder AddChaosOutcome(
        this IAegisPipelineBuilder builder, double injectionRate, ChaosOutcomeGenerator generator, Action<ChaosOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return builder.AddChaos(o =>
        {
            o.Enabled = true;
            o.InjectionRate = injectionRate;
            o.OutcomeGenerator = generator;
            o.FaultGenerator = null;
            configure?.Invoke(o);
        });
    }

    /// <summary>Belirtilen oranda yan etki çalıştırır, sonra asıl çağrı devam eder (Polly: <c>AddChaosBehavior</c>).</summary>
    public static IAegisPipelineBuilder AddChaosBehavior(
        this IAegisPipelineBuilder builder, double injectionRate, Func<Context.AegisContext, ValueTask> behavior, Action<ChaosOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(behavior);
        return builder.AddChaos(o =>
        {
            o.Enabled = true;
            o.InjectionRate = injectionRate;
            o.BehaviorGenerator = behavior;
            o.FaultGenerator = null;
            configure?.Invoke(o);
        });
    }

    public static IAegisPipelineBuilder AddStaleFallback(this IAegisPipelineBuilder builder, Action<StaleFallbackOptions>? configure = null)
    {
        var options = new StaleFallbackOptions();
        configure?.Invoke(options);
        return builder.AddStrategy(new StaleFallbackStrategy(options));
    }

    public static IAegisPipelineBuilder AddAdaptiveConcurrency(this IAegisPipelineBuilder builder, Action<AdaptiveConcurrencyOptions>? configure = null)
    {
        var options = new AdaptiveConcurrencyOptions();
        configure?.Invoke(options);
        return builder.AddStrategy(new AdaptiveConcurrencyStrategy(options));
    }

    /// <summary>
    /// Mikroservis ve HTTP çağrıları için optimize edilmiş endüstri standardı 5 stratejiyi doğru sırada boru hattına ekler:
    /// 1. Toplam Zaman Aşımı (Total Timeout)
    /// 2. Eşzamanlılık Sınırlayıcı (Concurrency / Bulkhead)
    /// 3. Yeniden Deneme (Retry + Jitter)
    /// 4. Devre Kesici (Circuit Breaker)
    /// 5. İstek Başına Zaman Aşımı (Attempt Timeout)
    /// </summary>
    public static IAegisPipelineBuilder AddStandardResilience(
        this IAegisPipelineBuilder builder,
        TimeSpan? totalTimeout = null,
        int maxConcurrency = 100,
        int retryAttempts = 3,
        TimeSpan? attemptTimeout = null)
    {
        // 1. En dışta Total Timeout
        builder.AddTimeout(totalTimeout ?? TimeSpan.FromSeconds(30));

        // 2. Concurrency Limiter
        builder.AddConcurrencyLimiter(maxConcurrency);

        // 3. Retry with Jitter
        builder.AddRetry(opt =>
        {
            opt.MaxRetryAttempts = retryAttempts;
            opt.BackoffType = DelayBackoffType.Exponential;
            opt.UseJitter = true;
            opt.Delay = TimeSpan.FromMilliseconds(500);
        });

        // 4. Circuit Breaker
        builder.AddCircuitBreaker(opt =>
        {
            opt.FailureRatio = 0.5;
            opt.SamplingDuration = TimeSpan.FromSeconds(10);
            opt.MinimumThroughput = 5;
            opt.BreakDuration = TimeSpan.FromSeconds(15);
        });

        // 5. En içte tekil Attempt Timeout
        builder.AddTimeout(attemptTimeout ?? TimeSpan.FromSeconds(5));

        return builder;
    }
}
