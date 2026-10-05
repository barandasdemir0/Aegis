using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;

namespace Aegis.TortureTests.Harness;

/// <summary>
/// Kütüphaneler arasında birebir eşlenmiş zincir: yeniden deneme (gecikmesiz) → devre kesici → deneme zaman aşımı.
/// Polly'nin alt sınırları (örnekleme ve açık kalma en az 500 ms) her iki tarafta da aynen kullanılır.
/// </summary>
public static class Chains
{
    public const int MaxRetryAttempts = 3;
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan Sampling = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan BreakDuration = TimeSpan.FromMilliseconds(500);

    public static ISubject Standard(Library library, TimeProvider? timeProvider = null) => library switch
    {
        Library.Aegis => new AegisSubject(AegisStandard(timeProvider).Build()),
        Library.Polly => new PollySubject(PollyStandard(timeProvider).Build()),
        _ => throw new NotSupportedByLibraryException("Microsoft.Extensions.Resilience çekirdek boru hattı olarak Polly'yi kullanır.")
    };

    public static IAegisPipelineBuilder AegisStandard(TimeProvider? timeProvider = null)
    {
        var builder = new AegisPipelineBuilder("torture")
            .AddRetry(o => { o.MaxRetryAttempts = MaxRetryAttempts; o.Delay = TimeSpan.Zero; o.UseJitter = false; })
            .AddCircuitBreaker(o => { o.FailureRatio = 0.5; o.MinimumThroughput = 10; o.SamplingDuration = Sampling; o.BreakDuration = BreakDuration; })
            .AddTimeout(AttemptTimeout);
        return timeProvider is null ? builder : builder.WithTimeProvider(timeProvider);
    }

    public static ResiliencePipelineBuilder PollyStandard(TimeProvider? timeProvider = null)
    {
        var builder = PollyStandardInto(new ResiliencePipelineBuilder());
        if (timeProvider is not null)
        {
            builder.TimeProvider = timeProvider;
        }

        return builder;
    }

    /// <summary>Standart zinciri verilen Polly kurucusuna ekler (ör. kayıt defterinin kurucusu).</summary>
    public static ResiliencePipelineBuilder PollyStandardInto(ResiliencePipelineBuilder builder)
    {
        builder
            .AddRetry(new RetryStrategyOptions { MaxRetryAttempts = MaxRetryAttempts, Delay = TimeSpan.Zero, UseJitter = false })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 10,
                SamplingDuration = Sampling,
                BreakDuration = BreakDuration
            })
            .AddTimeout(AttemptTimeout);
        return builder;
    }
}
