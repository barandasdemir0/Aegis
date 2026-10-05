using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Abstractions;

/// <summary>Tipli boru hattı oluşturma.</summary>
public static class AegisTypedPipelineExtensions
{
    /// <summary>Boru hattını <typeparamref name="TResult"/> tipine bağlı olarak kurar (Polly: <c>ResiliencePipelineBuilder&lt;T&gt;.Build()</c>).</summary>
    public static IAegisPipeline<TResult> Build<TResult>(this IAegisPipelineBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return new TypedAegisPipeline<TResult>(builder.Build());
    }

    /// <summary>Var olan tipsiz boru hattını tipli arayüzle sunar (aynı örnek; stratejiler paylaşılır).</summary>
    public static IAegisPipeline<TResult> AsTyped<TResult>(this IAegisPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return new TypedAegisPipeline<TResult>(pipeline);
    }
}
