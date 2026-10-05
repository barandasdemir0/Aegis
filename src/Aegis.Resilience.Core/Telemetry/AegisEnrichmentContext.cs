using System.Diagnostics;
using System.Diagnostics.Metrics;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Telemetry;

/// <summary>Metrik etiketlerini zenginleştirme bağlamı (Polly: <c>EnrichmentContext</c>).</summary>
public sealed class AegisEnrichmentContext
{
    internal AegisEnrichmentContext()
    {
    }

    /// <summary>Kaydedilen olay.</summary>
    public AegisTelemetryEvent TelemetryEvent { get; internal set; }

    /// <summary>Metriğe eklenecek etiketler. Standart etiketler zaten eklenmiştir; buraya ek etiket yazılabilir.</summary>
    public List<KeyValuePair<string, object?>> Tags { get; } = new(10);
}
