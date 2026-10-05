using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Core.Strategies.Timeout;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>Ağırlıklı uç nokta grubu (Microsoft: <c>WeightedUriEndpointGroup</c>).</summary>
public sealed class WeightedUriEndpointGroup : UriEndpointGroup
{
    /// <summary>Grubun seçilme ağırlığı (varsayılan 32).</summary>
    public int Weight { get; set; } = 32;
}
