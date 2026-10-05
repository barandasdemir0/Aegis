using System.Net.Http;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// Ağırlıklı yönlendirme yapılacak uç nokta ve ağırlık katsayısı.
/// </summary>
public sealed class WeightedEndpoint
{
    public Uri Uri { get; set; } = default!;

    /// <summary>
    /// Trafik ağırlığı (Örn: 90 ve 10, veya 0.9 ve 0.1).
    /// </summary>
    public double Weight { get; set; } = 1.0;

    public WeightedEndpoint() { }

    public WeightedEndpoint(Uri uri, double weight)
    {
        Uri = uri;
        Weight = weight;
    }
}
