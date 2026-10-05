using System.Diagnostics;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Pipeline;

/// <summary>
/// Yalnızca <see cref="IAegisStrategy"/> uygulayan (özel) stratejiler için uyumluluk bileşeni: her çağrıda bir closure
/// ayırır ve sonucu klasik istisna akışına çevirir. Davranış eski sürümlerle birebir aynıdır.
/// </summary>
internal sealed class LegacyStrategyComponent : PipelineComponent
{
    private readonly IAegisStrategy _strategy;
    private readonly PipelineComponent _next;

    public LegacyStrategyComponent(IAegisStrategy strategy, PipelineComponent next)
    {
        _strategy = strategy;
        _next = next;
    }

    public override ValueTask<Outcome<TResult>> ExecuteAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        return OutcomeInvoker.Invoke(
            ctx => _strategy.ExecuteAsync(c => OutcomeInvoker.Unwrap(_next.ExecuteAsync(callback, c, state)), ctx),
            context);
    }
}
