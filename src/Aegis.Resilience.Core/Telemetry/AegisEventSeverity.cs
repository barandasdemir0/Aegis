using System.Diagnostics;
using System.Diagnostics.Metrics;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Telemetry;

/// <summary>Olay önem düzeyi (Polly: <c>ResilienceEventSeverity</c>). Günlükte log düzeyine, metrikte etikete dönüşür.</summary>
public enum AegisEventSeverity
{
    None = 0,
    Debug,
    Information,
    Warning,
    Error,
    Critical
}
