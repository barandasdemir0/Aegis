using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Strategies.Retry;

/// <summary>Bir yeniden denemenin bilgisi (Polly: <c>OnRetryArguments</c> / <c>RetryDelayGeneratorArguments</c>).</summary>
public sealed class RetryAttemptContext
{
    /// <summary>
    /// Yeniden deneme numarası, 0'dan başlar (Polly ile aynı): ilk yeniden deneme 0, ikincisi 1. 2.0.0 öncesinde 1'den başlıyordu.
    /// </summary>
    public int AttemptNumber { get; init; }
    public TimeSpan RetryDelay { get; init; }
    public Exception? Exception { get; init; }
    public object? Result { get; init; }
    public AegisContext Context { get; init; } = default!;
}
