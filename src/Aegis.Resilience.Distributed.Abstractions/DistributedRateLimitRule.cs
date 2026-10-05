using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Strategies.RateLimiter;

namespace Aegis.Resilience.Distributed.Abstractions;

/// <summary>Bir sınırlayıcının kuralı (depoya iletilir).</summary>
public readonly struct DistributedRateLimitRule
{
    /// <param name="algorithm">Algoritma.</param>
    /// <param name="permitLimit">Pencere başına izin ya da kova kapasitesi.</param>
    /// <param name="window">Pencere uzunluğu ya da kova dolum periyodu.</param>
    /// <param name="tokensPerPeriod">Kovada periyot başına dolan jeton (diğer algoritmalarda kullanılmaz).</param>
    public DistributedRateLimitRule(DistributedRateLimitAlgorithm algorithm, int permitLimit, TimeSpan window, int tokensPerPeriod)
    {
        Algorithm = algorithm;
        PermitLimit = permitLimit;
        Window = window;
        TokensPerPeriod = tokensPerPeriod;
    }

    public DistributedRateLimitAlgorithm Algorithm { get; }

    public int PermitLimit { get; }

    public TimeSpan Window { get; }

    public int TokensPerPeriod { get; }

    /// <summary>
    /// Durumun depoda tutulması gereken süre: bu süre boyunca kullanılmayan anahtar tam dolu kova / sıfır sayaç ile aynıdır,
    /// dolayısıyla silinebilir (depolar anahtar ömrünü buna göre ayarlar).
    /// </summary>
    public TimeSpan StateLifetime
    {
        get
        {
            var windowMs = Math.Max(1, (long)Window.TotalMilliseconds);
            var retainedMs = Algorithm == DistributedRateLimitAlgorithm.TokenBucket
                ? windowMs * ((PermitLimit + TokensPerPeriod - 1) / Math.Max(1, TokensPerPeriod))
                : 2 * windowMs;
            return TimeSpan.FromMilliseconds(retainedMs + 1000);
        }
    }
}
