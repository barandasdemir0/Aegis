using System.Diagnostics;
using System.Diagnostics.Metrics;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Telemetry;

/// <summary>Standart metrik etiket adları (Polly/OpenTelemetry resilience semantiğiyle aynı).</summary>
public static class AegisTelemetryTags
{
    public const string EventName = "event.name";
    public const string EventSeverity = "event.severity";
    public const string PipelineName = "pipeline.name";
    public const string PipelineInstance = "pipeline.instance";
    public const string StrategyName = "strategy.name";
    public const string OperationKey = "operation.key";
    public const string ExceptionType = "exception.type";
    public const string AttemptNumber = "attempt.number";
    public const string AttemptHandled = "attempt.handled";
}
