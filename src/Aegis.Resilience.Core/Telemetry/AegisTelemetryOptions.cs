using System.Diagnostics;
using System.Diagnostics.Metrics;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Telemetry;

/// <summary>
/// Boru hattının telemetri seçenekleri (Polly: <c>TelemetryOptions</c>). <c>WithTelemetry(...)</c> ile boru hattına verilir.
/// </summary>
public sealed class AegisTelemetryOptions
{
    /// <summary>Olay dinleyicileri (ör. <c>ILogger</c> dinleyicisi, özel analitik).</summary>
    public IList<AegisTelemetryListener> Listeners { get; } = new List<AegisTelemetryListener>();

    /// <summary>Standart metriklere (<c>aegis.strategy.events</c> ...) etiket ekleyen zenginleştiriciler.</summary>
    public IList<Action<AegisEnrichmentContext>> MeteringEnrichers { get; } = new List<Action<AegisEnrichmentContext>>();

    /// <summary>Olayın önem düzeyini değiştirir (ör. belirli bir işlemde retry'ı Debug'a düşürmek).</summary>
    public Func<AegisTelemetryEvent, AegisEventSeverity>? SeverityProvider { get; set; }
}
