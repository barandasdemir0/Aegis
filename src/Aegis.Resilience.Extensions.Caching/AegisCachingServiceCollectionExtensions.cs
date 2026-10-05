using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Aegis.Resilience.Core.Strategies.Cache;

namespace Aegis.Resilience.Extensions.Caching;

/// <summary>DI kaydı.</summary>
public static class AegisCachingServiceCollectionExtensions
{
    /// <summary>
    /// DI'daki <see cref="IDistributedCache"/> üzerinde bir <see cref="IAegisCacheStore"/> kaydeder (tekil). Boru hattında:
    /// <c>services.AddAegisPipeline("urun", (b, sp) =&gt; b.AddCache(o =&gt; o.Store = sp.GetRequiredService&lt;IAegisCacheStore&gt;()))</c>.
    /// </summary>
    /// <param name="services">Servis koleksiyonu.</param>
    /// <param name="serializer">Serileştirici (AOT için kaynak üreticili <see cref="SystemTextJsonCacheSerializer"/>).</param>
    /// <param name="keyPrefix">Anahtar öneki.</param>
    public static IServiceCollection AddAegisDistributedCacheStore(
        this IServiceCollection services, IAegisCacheSerializer serializer, string? keyPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serializer);
        services.AddSingleton<IAegisCacheStore>(sp => new DistributedCacheStore(sp.GetRequiredService<IDistributedCache>(), serializer, keyPrefix));
        return services;
    }
}
