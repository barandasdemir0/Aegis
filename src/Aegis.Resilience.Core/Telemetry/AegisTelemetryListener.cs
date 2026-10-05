using System.Diagnostics;
using System.Diagnostics.Metrics;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Telemetry;

/// <summary>Olay dinleyicisi (Polly: <c>TelemetryListener</c>). Günlük, izleme veya özel analitik için.</summary>
public abstract class AegisTelemetryListener
{
    /// <summary>Olayı işler. Hızlı ve fırlatmayan olmalıdır; fırlatırsa istisna yutulur ve sayılır.</summary>
    public abstract void Write(in AegisTelemetryEvent telemetryEvent);
}
