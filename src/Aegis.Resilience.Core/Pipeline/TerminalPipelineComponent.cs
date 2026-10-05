using System.Diagnostics;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Pipeline;

internal sealed class TerminalPipelineComponent : PipelineComponent
{
    public static readonly TerminalPipelineComponent Instance = new();

    public override ValueTask<Outcome<TResult>> ExecuteAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        // Korumasız: boru hattına verilen geri çağrılar her zaman fırlatmayan iç sarmalayıcılardır
        // (OutcomeInvoker.Invoke/InvokeVoid/InvokeOutcome, InvokeSync). Katman başına ikinci try/catch ve Outcome kopyası kalkar.
        return callback(context, state);
    }
}
