using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Strategies.RateLimiter;

/// <summary>Bir hız/eşzamanlılık reddinin ayrıntısı (Polly: <c>OnRateLimiterRejectedArguments</c>).</summary>
public sealed class RateLimiterRejectedArguments
{
    public required AegisContext Context { get; init; }

    /// <summary>Reddeden strateji (ör. <c>RateLimiter</c>, <c>ConcurrencyLimiter</c>).</summary>
    public required string StrategyName { get; init; }

    /// <summary>Yeniden denemek için önerilen bekleme (biliniyorsa; token bucket ve kayan pencerede hesaplanır).</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// Sınırlayıcının red ayrıntıları (Polly: <c>Lease</c> meta verisi, ör. <c>REASON_PHRASE</c>); System.Threading.RateLimiting
    /// köprüsünde kiralamanın tüm meta verisi. Yerleşik sınırlayıcılarda null.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, object?>>? Metadata { get; init; }
}
