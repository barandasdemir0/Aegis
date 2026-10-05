using System.Threading.RateLimiting;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.RateLimiting;

/// <summary>
/// <see cref="System.Threading.RateLimiting"/> köprüsünün seçenekleri (Polly: <c>RateLimiterStrategyOptions</c>).
/// .NET'in hazır sınırlayıcıları (TokenBucket, FixedWindow, SlidingWindow, Concurrency, Partitioned) veya herhangi bir
/// özel <see cref="System.Threading.RateLimiting.RateLimiter"/> kullanılabilir.
/// </summary>
public sealed class RateLimitingBridgeOptions
{
    /// <summary>
    /// Çağrı başına izin (lease) alan fonksiyon (Polly: <c>RateLimiter</c> delegesi). Bağlama göre farklı sınırlayıcı seçmek
    /// (ör. kiracı başına) için kullanılabilir.
    /// </summary>
    public Func<AegisContext, CancellationToken, ValueTask<RateLimitLease>>? LeaseFactory { get; set; }

    /// <summary>İstek reddedildiğinde çağrılır. Hatası yutulur, red yine döner.</summary>
    public Func<RateLimiterRejectedArguments, ValueTask>? OnRejected { get; set; }

    /// <summary>Boru hattı dispose edilirken sınırlayıcı da dispose edilsin mi (sınırlayıcı yalnızca bu boru hattına aitse).</summary>
    public IDisposable? OwnedLimiter { get; set; }
}
