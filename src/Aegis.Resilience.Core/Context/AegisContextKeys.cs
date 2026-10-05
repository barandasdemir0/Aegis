namespace Aegis.Resilience.Core.Context;

/// <summary>
/// Stratejiler ve eklenti paketleri arasında <see cref="AegisContext.Properties"/> üzerinden paylaşılan iyi bilinen anahtarlar.
/// Paketler arası sözleşme tek yerde tanımlanır; düz metin kopyaları derleme zamanında kopamaz.
/// </summary>
public static class AegisContextKeys
{
    /// <summary>Cache stratejisinde <c>KeySelector</c> verilmediğinde kullanılan önbellek anahtarı (<see cref="string"/> veya ToString'i anlamlı bir değer).</summary>
    public const string CacheKey = "CacheKey";

    /// <summary>Partitioned rate limiter'da <c>PartitionKeySelector</c> verilmediğinde kullanılan bölüm anahtarı.</summary>
    public const string PartitionKey = "PartitionKey";

    /// <summary>HTTP <c>Retry-After</c> başlığından çözülen bekleme süresi (<see cref="TimeSpan"/>); Retry stratejisi gecikmeyi bununla ezer.</summary>
    public const string RetryAfterDelay = "Aegis.Http.RetryAfterDelay";

    /// <summary>
    /// <c>true</c> ise Retry ve Hedging ek deneme üretmez; işlem tam olarak bir kez çalışır ve ilk sonucu/istisnası olduğu gibi yükselir.
    /// Yeniden gönderilmesi güvenli olmayan işlemler (ör. Idempotency-Key'siz POST) için HTTP katmanı tarafından ayarlanır.
    /// </summary>
    public const string SuppressAdditionalAttempts = "Aegis.SuppressAdditionalAttempts";

    /// <summary>
    /// Çağrının ait olduğu <see cref="System.Net.Http.HttpRequestMessage"/> (HTTP işleyicileri yazar). Telemetri zenginleştiricisi
    /// isteğe iliştirilmiş <c>RequestMetadata</c>'yı buradan okur; paketler birbirine bağımlı olmadan veri paylaşır.
    /// </summary>
    public const string HttpRequest = "Aegis.Http.Request";

    /// <summary>Bağlamda ek deneme bastırma işaretinin açık olup olmadığını döner.</summary>
    public static bool AreAdditionalAttemptsSuppressed(AegisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // TryGetProperty sözlüğü OLUŞTURMAZ; Properties erişimi her çağrıda boş bir sözlük yaratıyordu.
        return context.TryGetProperty<bool>(SuppressAdditionalAttempts, out var value) && value;
    }
}
