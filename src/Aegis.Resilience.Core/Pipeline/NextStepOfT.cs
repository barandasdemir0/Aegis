using System.Diagnostics;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Pipeline;

/// <summary>Sonraki bileşen + geri çağrı + durum: hızlı yolda struct olarak geçirilir (closure tahsisi yok).</summary>
internal readonly struct NextStep<TResult, TState>(
    PipelineComponent next,
    Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
    TState state)
{
    public PipelineComponent Next { get; } = next;
    public Func<AegisContext, TState, ValueTask<Outcome<TResult>>> Callback { get; } = callback;
    public TState State { get; } = state;
}
