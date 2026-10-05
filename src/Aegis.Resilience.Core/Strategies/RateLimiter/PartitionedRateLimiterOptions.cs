using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Strategies.RateLimiter;

/// <summary>
/// Bölümlendirilmiş / Çok Kiracılı (Partitioned / Multi-Tenant) Hız Sınırlayıcı seçenekleri.
/// </summary>
public sealed class PartitionedRateLimiterOptions
{
    /// <summary>
    /// Çağrı context'inden bölüm / kiracı anahtarını (TenantId, ClientId, UserId vb.) seçen delegasyon.
    /// Belirtilmezse varsayılan olarak context.CustomProperties["PartitionKey"] veya context.PipelineName kullanılır.
    /// </summary>
    public Func<AegisContext, string>? PartitionKeySelector { get; set; }

    /// <summary>
    /// Her bir bölüme özel hız sınırı seçenekleri üreten fabrika fonksiyonu.
    /// Belirtilmezse DefaultOptions kullanılır.
    /// </summary>
    public Func<string, RateLimiterOptions>? OptionsFactory { get; set; }

    /// <summary>
    /// Bölüm için özel seçenek tanımlanmadığında kullanılacak varsayılan hız sınırı seçenekleri.
    /// </summary>
    public RateLimiterOptions DefaultOptions { get; set; } = new();

    /// <summary>
    /// Bellek sızıntısı ve cardinality patlamasını önlemek için saklanacak maksimum bölüm sayısı.
    /// Varsayılan: 10,000. Sınır aşıldığında en eski / aktif olmayan bölümler otomatik tahliye edilir.
    /// </summary>
    public int MaxPartitions { get; set; } = 10_000;

    /// <summary>
    /// Canlı ayar güncellemesi için opsiyonel dinamik sağlayıcı.
    /// </summary>
    public Func<PartitionedRateLimiterOptions>? OptionsProvider { get; set; }

    /// <summary>Seçenekleri doğrular; geçersiz değer için <see cref="ArgumentOutOfRangeException"/> fırlatır (AEGIS-130).</summary>
    public void Validate()
    {
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.AtLeast(MaxPartitions, 1, nameof(PartitionedRateLimiterOptions));
        ArgumentNullException.ThrowIfNull(DefaultOptions, nameof(DefaultOptions));
        DefaultOptions.Validate();
    }
}
