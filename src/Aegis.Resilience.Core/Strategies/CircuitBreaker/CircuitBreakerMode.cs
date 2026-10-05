namespace Aegis.Resilience.Core.Strategies.CircuitBreaker;

/// <summary>Devre kesicinin çalışma kipi (resilience4j: <c>METRICS_ONLY</c>, <c>DISABLED</c>).</summary>
public enum CircuitBreakerMode
{
    /// <summary>Varsayılan: devre açıkken istekler reddedilir.</summary>
    Enforce,

    /// <summary>
    /// Gölge kip: durum makinesi, olaylar ve metrikler normal çalışır ama hiçbir istek reddedilmez. Yeni bir devreyi üretime almadan
    /// önce "hangi anda açılırdı" diye izlemek ve eşikleri doğrulamak için.
    /// </summary>
    Shadow,

    /// <summary>Devre tamamen geçirgendir: sonuç kaydedilmez, durum değişmez, olay üretilmez.</summary>
    Disabled
}
