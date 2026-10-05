using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Aegis.Resilience.Distributed.Abstractions;
using StackExchange.Redis;

namespace Aegis.Resilience.Distributed.Redis;

public static class RedisServiceCollectionExtensions
{
    /// <summary>
    /// Aegis Circuit Breaker dağıtık durum deposunu Redis bağlantısı ile kaydeder.
    /// </summary>
    public static IServiceCollection AddAegisRedisStateStore(
        this IServiceCollection services,
        IConnectionMultiplexer redis,
        string keyPrefix = "aegis:cb:",
        TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(redis);

        services.RemoveAll<ICircuitBreakerStateStore>();
        services.AddSingleton<ICircuitBreakerStateStore>(_ => new RedisCircuitBreakerStateStore(redis, keyPrefix, operationTimeout));

        return services;
    }

    /// <summary>
    /// Aegis Circuit Breaker dağıtık durum deposunu Redis bağlantı dizesi ile kaydeder.
    /// </summary>
    public static IServiceCollection AddAegisRedisStateStore(
        this IServiceCollection services,
        string redisConnectionString,
        string keyPrefix = "aegis:cb:",
        TimeSpan? operationTimeout = null)
    {
        var connect = BackgroundConnection(redisConnectionString);

        services.RemoveAll<ICircuitBreakerStateStore>();
        services.AddSingleton<ICircuitBreakerStateStore>(_ => new RedisCircuitBreakerStateStore(connect, keyPrefix, operationTimeout));

        return services;
    }

    /// <summary>
    /// Dağıtık hız sınırlayıcı deposunu Redis bağlantısı ile kaydeder (<see cref="IDistributedRateLimitStore"/>).
    /// Boru hattında: <c>b.AddDistributedRateLimiter(sp.GetRequiredService&lt;IDistributedRateLimitStore&gt;(), o =&gt; ...)</c>.
    /// </summary>
    public static IServiceCollection AddAegisRedisRateLimitStore(
        this IServiceCollection services,
        IConnectionMultiplexer redis,
        string keyPrefix = "aegis:rl:",
        TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(redis);

        services.RemoveAll<IDistributedRateLimitStore>();
        services.AddSingleton<IDistributedRateLimitStore>(_ => new RedisRateLimitStore(redis, keyPrefix, operationTimeout));

        return services;
    }

    /// <summary>
    /// Dağıtık hız sınırlayıcı deposunu Redis bağlantı dizesi ile kaydeder. Redis hazır olana kadar (ve kesintide) pod
    /// başına yerel sınır uygulanır.
    /// </summary>
    public static IServiceCollection AddAegisRedisRateLimitStore(
        this IServiceCollection services,
        string redisConnectionString,
        string keyPrefix = "aegis:rl:",
        TimeSpan? operationTimeout = null)
    {
        var connect = BackgroundConnection(redisConnectionString);

        services.RemoveAll<IDistributedRateLimitStore>();
        services.AddSingleton<IDistributedRateLimitStore>(_ => new RedisRateLimitStore(connect, keyPrefix, operationTimeout));

        return services;
    }

    /// <summary>
    /// Bağlantı dizesinden arka planda kurulan bağlantı. Dize kayıtta ayrıştırılır: hatalı dize uygulama açılırken
    /// bildirilir (fail-fast). Bağlantı arka planda kurulur: Redis yavaş/erişilemezken ilk istek (Kubernetes'te pod açılışı)
    /// senkron bağlantı zaman aşımını beklemez; depo hazır olana kadar yerel duruma düşer.
    /// </summary>
    private static Func<Task<IConnectionMultiplexer>> BackgroundConnection(string redisConnectionString)
    {
        ArgumentNullException.ThrowIfNull(redisConnectionString);
        var options = ConfigurationOptions.Parse(redisConnectionString);

        // AEGIS-157: Redis'e ulaşılamasa bile bağlantı nesnesi oluşturulmalı ve arka planda yeniden denemeli.
        // Varsayılan (abortConnect=true) ile Redis henüz hazır değilken (Kubernetes'te olağan başlangıç sırası)
        // depo hiç oluşturulamıyor, istisna her isteğe sızıp HTTP 500 üretiyor ve her istek bağlantıyı baştan
        // deniyordu — "fail-open" vaadinin tam tersi. Artık depo her zaman oluşur; Redis gelene kadar yerel
        // duruma düşülür, Redis gelince çoklayıcı kendiliğinden bağlanır.
        options.AbortOnConnectFail = false;

        // Bağlantı yokken komutlar kuyrukta bekletilmez (varsayılan: zaman aşımına kadar ~5 sn bekleme). Depolar için
        // doğru davranış anında yerel duruma düşmektir; aksi halde Redis kesintisinde HER çağrı 5 sn gecikir.
        options.BacklogPolicy = BacklogPolicy.FailFast;

        return async () => await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);
    }
}
