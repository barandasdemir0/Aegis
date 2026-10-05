using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Pipeline;

public static class PipelineCompositionExtensions
{
    /// <summary>
    /// Var olan bir boru hattını bu zincire tek bir strateji olarak ekler. İç boru hattı dispose edilmez.
    /// </summary>
    public static IAegisPipelineBuilder AddPipeline(this IAegisPipelineBuilder builder, IAegisPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddStrategy(new PipelineStrategyAdapter(pipeline));
    }
}
