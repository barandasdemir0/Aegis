using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;

namespace Aegis.Resilience.Testing;

/// <summary>Bir stratejinin tanımı (Polly: <c>ResilienceStrategyDescriptor</c>).</summary>
public sealed class AegisStrategyDescriptor
{
    internal AegisStrategyDescriptor(IAegisStrategy strategy)
    {
        StrategyInstance = strategy;
        Name = strategy.Name;
        Options = (strategy as AegisStrategy)?.Options;
    }

    /// <summary>Strateji adı (ör. <c>Retry</c>, <c>CircuitBreaker</c>).</summary>
    public string Name { get; }

    /// <summary>Strateji örneği.</summary>
    public IAegisStrategy StrategyInstance { get; }

    /// <summary>Kurulumdaki seçenek nesnesi (ör. <c>RetryOptions</c>); özel stratejilerde null olabilir.</summary>
    public object? Options { get; }

    public override string ToString() => Name;
}
