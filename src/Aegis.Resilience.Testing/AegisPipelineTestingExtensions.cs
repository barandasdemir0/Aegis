using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;

namespace Aegis.Resilience.Testing;

/// <summary>Boru hattı tanımlayıcı uzantıları (Polly: <c>GetPipelineDescriptor()</c>).</summary>
public static class AegisPipelineTestingExtensions
{
    /// <summary>Boru hattının stratejilerini ve seçeneklerini döner.</summary>
    public static AegisPipelineDescriptor GetPipelineDescriptor(this IAegisPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        var isReloadable = pipeline is ReloadableAegisPipeline;
        var effective = pipeline is ReloadableAegisPipeline reloadable ? reloadable.Current : pipeline;
        var strategies = new List<AegisStrategyDescriptor>();
        Flatten(effective, strategies);
        return new AegisPipelineDescriptor(pipeline.Name, strategies, isReloadable);
    }

    /// <summary>Tipli boru hattının tanımı.</summary>
    public static AegisPipelineDescriptor GetPipelineDescriptor<TResult>(this IAegisPipeline<TResult> pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return pipeline.Untyped.GetPipelineDescriptor();
    }

    private static void Flatten(IAegisPipeline pipeline, List<AegisStrategyDescriptor> target)
    {
        foreach (var strategy in pipeline.Strategies)
        {
            if (strategy is PipelineStrategyAdapter adapter)
            {
                var inner = adapter.InnerPipeline is ReloadableAegisPipeline reloadable ? reloadable.Current : adapter.InnerPipeline;
                Flatten(inner, target);
            }
            else
            {
                target.Add(new AegisStrategyDescriptor(strategy));
            }
        }
    }
}
