using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;

namespace Aegis.Resilience.Core.Strategies.Chaos;

/// <summary>Enjeksiyon türü.</summary>
public enum ChaosInjectionKind
{
    Behavior,
    Latency,
    Outcome,
    Fault
}
