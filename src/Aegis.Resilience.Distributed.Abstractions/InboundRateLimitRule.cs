namespace Aegis.Resilience.Distributed.Abstractions;

/// <summary>
/// Gelen istek hız sınırlama kuralı (ASP.NET Core ve Web API 2 paketlerinin ortak tabanı). Aynı uç noktaya birden çok kural
/// (ör. saniyede 10 ve saatte 1000) verilebilir; istek, eşleşen TÜM kuralları geçmelidir.
/// </summary>
public class InboundRateLimitRule
{
    /// <summary>
    /// Uç nokta deseni: <c>"*"</c> (hepsi), <c>"/api/siparis"</c>, <c>"GET:/api/urun/*"</c>, <c>"*:/api/*"</c>.
    /// Yol büyük/küçük harf duyarsızdır; <c>*</c> her karakter dizisiyle eşleşir.
    /// </summary>
    public string Endpoint { get; set; } = "*";

    /// <summary>Periyotta izin verilen istek sayısı.</summary>
    public int Limit { get; set; }

    /// <summary>Periyot (ör. <c>"00:00:01"</c>, <c>"01:00:00"</c>).</summary>
    public TimeSpan Period { get; set; }

    /// <summary>Algoritma (varsayılan: sabit pencere, AspNetCoreRateLimit ve WebApiThrottle ile aynı).</summary>
    public DistributedRateLimitAlgorithm Algorithm { get; set; } = DistributedRateLimitAlgorithm.FixedWindow;
}
