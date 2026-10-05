using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;

namespace Aegis.Resilience.Core.Strategies.Chaos;

/// <summary>Bir kaos enjeksiyonunun bilgisi.</summary>
public sealed class ChaosInjectionArguments
{
    public required AegisContext Context { get; init; }
    public required ChaosInjectionKind Kind { get; init; }

    /// <summary>Gecikme enjeksiyonunda süre.</summary>
    public TimeSpan? Latency { get; init; }

    /// <summary>Hata enjeksiyonunda istisna.</summary>
    public Exception? Exception { get; init; }

    /// <summary>Sonuç enjeksiyonunda sonuç.</summary>
    public object? Result { get; init; }
}
