using System.Net.Http;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// Ağırlıklı Kanarya / Dağıtık Trafik Yönlendirme (Weighted Canary Routing) seçenekleri.
/// Yeni sürümlerin aşamalı devreye alınması (Canary Releases) veya mavi-yeşil dağıtımlar için kullanılır.
/// </summary>
public sealed class WeightedCanaryOptions
{
    /// <summary>
    /// Ağırlıklarıyla birlikte yapılandırılmış uç noktalar listesi.
    /// </summary>
    public IReadOnlyList<WeightedEndpoint> Endpoints { get; set; } = [];

    /// <summary>
    /// İsteğe göre dinamik uç nokta listesi sağlayan delegasyon (opsiyonel).
    /// </summary>
    public Func<HttpRequestMessage, IReadOnlyList<WeightedEndpoint>>? EndpointsProvider { get; set; }

    /// <summary>
    /// Aynı kullanıcının sürekli aynı sürüme gitmesi için oturum anahtarı (Sticky Session Key) seçici.
    /// Örneğin request.Headers["X-User-Id"] veya cookie.
    /// </summary>
    public Func<HttpRequestMessage, string?>? StickySessionKeySelector { get; set; }
}
