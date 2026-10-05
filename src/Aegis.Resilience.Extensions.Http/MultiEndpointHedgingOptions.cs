using System.Net.Http;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// Çoklu Uç Nokta Hedging (Multi-Endpoint / Multi-Cluster Hedging) seçenekleri.
/// Birincil veri merkezi veya uç nokta yavaşladığında spekülatif olarak ikincil yedek uç noktaya paralel istek atar.
/// </summary>
public sealed class MultiEndpointHedgingOptions
{
    /// <summary>
    /// İsteklerin yönlendirileceği sıralı hedef uç nokta adresleri (Örn: Primary, Secondary, DR).
    /// </summary>
    public IReadOnlyList<Uri> Endpoints { get; set; } = [];

    /// <summary>
    /// İsteğe göre dinamik uç nokta listesi sağlayan delegasyon (opsiyonel).
    /// </summary>
    public Func<HttpRequestMessage, IReadOnlyList<Uri>>? EndpointsProvider { get; set; }

    /// <summary>
    /// Birincil istekten sonra ikincil yedek uç noktaya paralel istek başlatılmadan önce beklenecek gecikme süresi.
    /// Varsayılan 500 ms.
    /// </summary>
    public TimeSpan HedgingDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Birincile ek yedek deneme sayısı (Polly <c>MaxHedgedAttempts</c> ile aynı anlam; varsayılan 1: 1 birincil + 1 yedek).
    /// </summary>
    public int MaxHedgedAttempts { get; set; } = 1;

    /// <summary>
    /// Dönen HTTP cevabının başarısız sayılıp diğer paralel isteğin beklenmesini belirleyen koşul.
    /// </summary>
    public Func<HttpResponseMessage, bool>? ShouldHandleResult { get; set; }

    /// <summary>
    /// Idempotent olmayan isteklerin (Idempotency-Key'siz POST/PATCH) de birden fazla uç noktaya gönderilmesine izin verir.
    /// Varsayılan false: böyle bir istek yalnızca birincil uç noktaya bir kez gider (çift işlem koruması).
    /// </summary>
    public bool AllowNonIdempotentHedging { get; set; }
}
