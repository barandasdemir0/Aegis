using System.Diagnostics;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Pipeline;

/// <summary>Yerleşik stratejiler (<see cref="AegisStrategy"/>) için sıfır tahsisli bileşen.</summary>
internal sealed class FastStrategyComponent : PipelineComponent
{
    private readonly AegisStrategy _strategy;
    private readonly PipelineComponent _next;

    public FastStrategyComponent(AegisStrategy strategy, PipelineComponent next)
    {
        _strategy = strategy;
        _next = next;
    }

    public override ValueTask<Outcome<TResult>> ExecuteAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        // Statik lambda her (TResult, TState) için bir kez oluşturulup önbelleğe alınır; durum struct olarak geçer.
        return _strategy.ExecuteOutcomeAsync(
            static (ctx, step) => step.Next.ExecuteAsync(step.Callback, ctx, step.State),
            context,
            new NextStep<TResult, TState>(_next, callback, state));
    }
}
