using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Pipeline;

/// <summary>
/// Var olan bir boru hattını başka bir boru hattının içinde tek bir strateji gibi çalıştırır (kompozisyon).
/// İç boru hattının sahipliği ÇAĞIRANDA kalır: dış boru hattı dispose edildiğinde iç boru hattı dispose EDİLMEZ
/// (Polly: AddPipeline_EnsureNotDisposed). Registry'den alınan paylaşılan bir boru hattını yerel bir zincire
/// gömmek için kullanılır.
/// </summary>
public sealed class PipelineStrategyAdapter : IAegisStrategy
{
    private readonly IAegisPipeline _inner;

    public PipelineStrategyAdapter(IAegisPipeline inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public string Name => $"Pipeline({_inner.Name})";

    /// <summary>Sarılan (iç) boru hattı.</summary>
    public IAegisPipeline InnerPipeline => _inner;

    public ValueTask<TResult> ExecuteAsync<TResult>(Func<AegisContext, ValueTask<TResult>> callback, AegisContext context)
        => _inner.ExecuteAsync(callback, context);
}
