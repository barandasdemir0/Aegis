using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Strategies.RateLimiter;

namespace Aegis.Resilience.Distributed.Abstractions;

/// <summary>Dağıtık hız sınırlama algoritması.</summary>
public enum DistributedRateLimitAlgorithm
{
    /// <summary>
    /// Kova: <see cref="DistributedRateLimitRule.PermitLimit"/> kapasiteli, her <see cref="DistributedRateLimitRule.Window"/>
    /// içinde <see cref="DistributedRateLimitRule.TokensPerPeriod"/> jeton dolar. Kısa patlamalara izin verir, ortalamayı sınırlar.
    /// </summary>
    TokenBucket,

    /// <summary>Sabit pencere: her pencerede en fazla <see cref="DistributedRateLimitRule.PermitLimit"/> istek. En ucuz.</summary>
    FixedWindow,

    /// <summary>
    /// Kayan pencere (iki pencereli ağırlıklı tahmin): pencere sınırında iki kat patlamayı önler, bellek sabittir.
    /// </summary>
    SlidingWindow
}
