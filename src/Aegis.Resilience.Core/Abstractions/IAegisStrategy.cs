using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Abstractions;

/// <summary>
/// Tüm dayanıklılık stratejilerinin (Retry, Circuit Breaker, Timeout vb.) uyguladığı temel arayüz.
/// </summary>
public interface IAegisStrategy
{
    /// <summary>
    /// Stratejinin adı (örn. "Retry", "CircuitBreaker", "Timeout").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Verilen delegasyonu strateji kurallarına göre yürütür.
    /// </summary>
    ValueTask<TResult> ExecuteAsync<TResult>(
        Func<AegisContext, ValueTask<TResult>> callback,
        AegisContext context);
}
