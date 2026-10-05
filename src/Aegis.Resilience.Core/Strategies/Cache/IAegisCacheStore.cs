namespace Aegis.Resilience.Core.Strategies.Cache;

/// <summary>
/// Cache stratejisinin dış deposu (Polly v7: <c>IAsyncCacheProvider</c>). Verilmezse yerleşik bellek içi depo kullanılır
/// (LRU, kapasite sınırı, monotonik saat). Dağıtık depo için <c>Aegis.Resilience.Extensions.Caching</c> paketindeki
/// <c>IDistributedCache</c> deposu kullanılabilir. Depo hataları çağrıyı düşürmez: okuma hatası ıskalama sayılır, yazma
/// hatası yok sayılır (<see cref="CacheOptions.OnCacheError"/> bildirilir).
/// </summary>
public interface IAegisCacheStore
{
    /// <summary>Anahtardaki değeri okur.</summary>
    ValueTask<AegisCacheLookup<T>> TryGetAsync<T>(string key, CancellationToken cancellationToken);

    /// <summary>Değeri verilen süre kuralıyla yazar.</summary>
    ValueTask SetAsync<T>(string key, T value, AegisCacheEntryOptions options, CancellationToken cancellationToken);

    /// <summary>Anahtarı siler (geçersiz kılma).</summary>
    ValueTask RemoveAsync(string key, CancellationToken cancellationToken);
}
