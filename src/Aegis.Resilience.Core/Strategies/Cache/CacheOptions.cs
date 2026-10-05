using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Strategies.Cache;

/// <summary>
/// Cache-Aside stratejisi yapılandırma seçenekleri.
/// </summary>
public sealed class CacheOptions
{
    /// <summary>
    /// Çağrı context'inden benzersiz önbellek anahtarı üreten delegasyon.
    /// Belirtilmezse varsayılan olarak context.CustomProperties["CacheKey"] veya pipeline adı kullanılır.
    /// </summary>
    public Func<AegisContext, string>? KeySelector { get; set; }

    /// <summary>
    /// Önbellek geçerlilik süresi (TTL - Time to Live). Varsayılan 30 saniye.
    /// </summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// True ise her isabette süre yeniden başlar: sık okunan girdi yaşar, okunmayan <see cref="Ttl"/> sonra düşer
    /// (Polly: <c>SlidingTtl</c>). Varsayılan false (sabit süre).
    /// </summary>
    public bool SlidingExpiration { get; set; }

    /// <summary>
    /// Sonuca ve bağlama göre girdi başına süre (Polly: <c>ResultTtl</c>); ör. HTTP yanıtının <c>max-age</c> değeri ya da
    /// günün belli bir saatine kadar (mutlak süre). Sıfır veya negatif dönerse sonuç önbelleğe alınmaz. Null ise <see cref="Ttl"/>.
    /// </summary>
    public Func<AegisContext, object?, TimeSpan>? TtlGenerator { get; set; }

    /// <summary>Dış depo (ör. dağıtık önbellek). Null ise yerleşik bellek içi depo (<see cref="MaxEntries"/> ile sınırlı).</summary>
    public IAegisCacheStore? Store { get; set; }

    /// <summary>
    /// Depo okuma/yazma hatasında çağrılır (anahtar, istisna). Hata çağrıyı düşürmez: okuma hatası ıskalama sayılır, yazma
    /// hatası yok sayılır (Polly: <c>onCacheGetError</c> / <c>onCachePutError</c>).
    /// </summary>
    public Action<string, Exception>? OnCacheError { get; set; }

    /// <summary>
    /// Null / default dönen sonuçların önbelleğe alınıp alınmayacağı. Varsayılan false.
    /// </summary>
    public bool CacheNulls { get; set; }

    /// <summary>
    /// Önbellekte saklanabilecek maksimum girdi sayısı (Bellek patlaması ve cardinality koruması - AEGIS-105).
    /// Varsayılan 10.000.
    /// </summary>
    public int MaxEntries { get; set; } = 10_000;

    /// <summary>
    /// Önbellek isabet ettiğinde tetiklenen callback.
    /// </summary>
    public Action<string, object?>? OnCacheHit { get; set; }

    /// <summary>
    /// Önbellekte bulunamadığında tetiklenen callback.
    /// </summary>
    public Action<string>? OnCacheMiss { get; set; }

    /// <summary>
    /// Canlı ayar güncellemesi için opsiyonel sağlayıcı delegasyon.
    /// </summary>
    public Func<CacheOptions>? OptionsProvider { get; set; }

    /// <summary>Seçenekleri doğrular; geçersiz değer için <see cref="ArgumentOutOfRangeException"/> fırlatır (AEGIS-130).</summary>
    public void Validate()
    {
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.NonNegative(Ttl, nameof(CacheOptions));
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.AtLeast(MaxEntries, 1, nameof(CacheOptions));
    }
}
