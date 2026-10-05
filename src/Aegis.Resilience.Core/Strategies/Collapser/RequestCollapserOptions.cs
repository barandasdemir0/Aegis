using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Strategies.Collapser;

/// <summary>
/// Mükerrer eşzamanlı istekleri tekilleştirme (Request Collapser / Singleflight) seçenekleri.
/// </summary>
public sealed class RequestCollapserOptions
{
    /// <summary>
    /// ZORUNLU. Eşzamanlı istekleri gruplamak için kullanılacak anahtar seçici. Aynı anahtarı üreten uçuştaki çağrılar
    /// tek yürütmeyi paylaşır; bu yüzden anahtar <b>işlemi ve girdisini</b> birlikte tanımlamalıdır
    /// (ör. <c>ctx =&gt; ctx.TryGetProperty&lt;string&gt;("UrunId", out var id) ? $"urun:{id}" : null</c>).
    /// <c>null</c>/boş dönerse o çağrı birleştirilmez.
    /// <para>
    /// 1.0.5 kırıcı değişiklik: varsayılan <c>CorrelationId</c> kaldırıldı. Aynı bağlamdaki FARKLI işlemleri tek işleme
    /// indirip ikinci işleme birincinin sonucunu döndürüyordu (sessiz yanlış veri).
    /// </para>
    /// </summary>
    public Func<AegisContext, string?>? KeySelector { get; set; }

    /// <summary>
    /// Canlı ayar güncellemesi için opsiyonel dinamik sağlayıcı.
    /// </summary>
    public Func<RequestCollapserOptions>? OptionsProvider { get; set; }

    /// <summary>Seçenekleri doğrular (AEGIS-130).</summary>
    public void Validate()
    {
        if (KeySelector == null)
        {
            throw new ArgumentException(
                $"{nameof(RequestCollapserOptions)}.{nameof(KeySelector)} zorunludur: birleştirilecek işlemi ve girdisini tanımlayan bir anahtar seçici verin. " +
                "Varsayılan anahtar yoktur; CorrelationId gibi istek geneli bir anahtar farklı işlemleri yanlışlıkla birleştirir.",
                nameof(KeySelector));
        }
    }
}
