namespace Aegis.Resilience.Core.Telemetry;

/// <summary>Bir olayın veya reddin kaynağı (Polly: <c>ResilienceTelemetrySource</c>).</summary>
public sealed class AegisTelemetrySource
{
    internal AegisTelemetrySource(string? pipelineName, string? pipelineInstanceName, string? strategyName)
    {
        PipelineName = pipelineName;
        PipelineInstanceName = pipelineInstanceName;
        StrategyName = strategyName;
    }

    public string? PipelineName { get; }
    public string? PipelineInstanceName { get; }
    public string? StrategyName { get; }
}
