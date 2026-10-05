using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Strategies.Fallback;

/// <summary>Fallback'i tetikleyen sonuç (Polly: <c>FallbackActionArguments</c> / <c>OnFallbackArguments</c>).</summary>
public sealed class FallbackArguments
{
    public required AegisContext Context { get; init; }

    /// <summary>Tetikleyen istisna (istisna tabanlı fallback'te).</summary>
    public Exception? Exception { get; init; }

    /// <summary>Tetikleyen sonuç (sonuç tabanlı, istisnasız fallback'te).</summary>
    public object? Result { get; init; }
}
