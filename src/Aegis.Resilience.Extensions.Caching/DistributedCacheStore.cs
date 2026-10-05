using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Aegis.Resilience.Core.Strategies.Cache;

namespace Aegis.Resilience.Extensions.Caching;

/// <summary>
/// Aegis cache stratejisinin <see cref="IDistributedCache"/> deposu (Polly: <c>Polly.Caching.Distributed</c>): Redis,
/// SQL Server, NCache gibi her dağıtık önbellek. Pod'lar arasında ortak önbellek; kayan süre depo tarafından uygulanır.
/// Kullanım: <c>.AddCache(o =&gt; o.Store = new DistributedCacheStore(cache, serializer))</c>.
/// </summary>
public sealed class DistributedCacheStore : IAegisCacheStore
{
    private readonly IDistributedCache _cache;
    private readonly IAegisCacheSerializer _serializer;
    private readonly string _keyPrefix;

    /// <param name="cache">Dağıtık önbellek.</param>
    /// <param name="serializer">Değer serileştiricisi.</param>
    /// <param name="keyPrefix">Anahtar öneki: aynı önbelleği paylaşan uygulamaların anahtarlarını ayırır (ör. <c>"odeme:"</c>).</param>
    public DistributedCacheStore(IDistributedCache cache, IAegisCacheSerializer serializer, string? keyPrefix = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _keyPrefix = keyPrefix ?? string.Empty;
    }

    /// <inheritdoc />
    public async ValueTask<AegisCacheLookup<T>> TryGetAsync<T>(string key, CancellationToken cancellationToken)
    {
        var data = await _cache.GetAsync(_keyPrefix + key, cancellationToken).ConfigureAwait(false);
        return data is null ? AegisCacheLookup.Miss<T>() : AegisCacheLookup.Hit(_serializer.Deserialize<T>(data));
    }

    /// <inheritdoc />
    public async ValueTask SetAsync<T>(string key, T value, AegisCacheEntryOptions options, CancellationToken cancellationToken)
    {
        var entryOptions = options.SlidingExpiration
            ? new DistributedCacheEntryOptions { SlidingExpiration = options.Ttl }
            : new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = options.Ttl };
        await _cache.SetAsync(_keyPrefix + key, _serializer.Serialize(value), entryOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken) =>
        await _cache.RemoveAsync(_keyPrefix + key, cancellationToken).ConfigureAwait(false);
}
