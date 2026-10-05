using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Core.Strategies.Timeout;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>Ağırlıklı uç nokta (Microsoft: <c>WeightedUriEndpoint</c>).</summary>
public sealed class WeightedUriEndpoint
{
    public Uri Uri { get; set; } = default!;

    /// <summary>Grup içindeki seçilme ağırlığı (varsayılan 32).</summary>
    public int Weight { get; set; } = 32;
}
