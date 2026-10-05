using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Core.Strategies.Timeout;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>Uç nokta grubu (Microsoft: <c>UriEndpointGroup</c>): her denemede gruptan ağırlığa göre bir uç nokta seçilir.</summary>
public class UriEndpointGroup
{
    public IList<WeightedUriEndpoint> Endpoints { get; set; } = new List<WeightedUriEndpoint>();
}
