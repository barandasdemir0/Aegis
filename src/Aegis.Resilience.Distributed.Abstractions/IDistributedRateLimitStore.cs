using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Strategies.RateLimiter;

namespace Aegis.Resilience.Distributed.Abstractions;

/// <summary>
/// Dağıtık hız sınırlayıcı deposu: sayaçlar tüm pod'lar arasında ortaktır (Redis: <c>RedisRateLimitStore</c>).
/// Tek sunucu ve testler için <see cref="InMemoryDistributedRateLimitStore"/>.
/// </summary>
public interface IDistributedRateLimitStore
{
    /// <summary>Ortak depo şu an erişilebilir mi (health check için). Erişilemezken depo yerel sınıra düşer.</summary>
    bool IsAvailable { get; }

    /// <summary>Anahtar için <paramref name="permitCount"/> izin ister.</summary>
    ValueTask<DistributedRateLimitDecision> TryAcquireAsync(
        string key, DistributedRateLimitRule rule, int permitCount, CancellationToken cancellationToken);
}
