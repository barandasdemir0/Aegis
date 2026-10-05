using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Strategies.Hedging;

/// <summary>Bir yedek (hedged) denemenin bilgisi (Polly: <c>OnHedgingArguments</c> / <c>HedgingDelayGeneratorArguments</c>).</summary>
public sealed class HedgingAttemptArguments
{
    public required AegisContext Context { get; init; }

    /// <summary>Başlatılacak denemenin numarası (birincil deneme 0; ilk yedek 1).</summary>
    public required int AttemptNumber { get; init; }
}
