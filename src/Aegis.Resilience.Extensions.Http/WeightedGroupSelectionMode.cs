using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Core.Strategies.Timeout;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>Ağırlıklı grupların seçim zamanı (Microsoft: <c>WeightedGroupSelectionMode</c>).</summary>
public enum WeightedGroupSelectionMode
{
    /// <summary>İlk denemede grup ağırlığa göre seçilir; sonraki denemeler kalan grupları tanım sırasıyla dener.</summary>
    InitialAttempt,

    /// <summary>Her denemede kalan gruplar arasından ağırlığa göre seçim yapılır.</summary>
    EveryAttempt
}
