using System.Runtime.CompilerServices;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Abstractions;

/// <summary>
/// Bir koşulun (predicate) değerlendirdiği sonuç: deneme sonucu + bağlam + deneme numarası
/// (Polly: <c>RetryPredicateArguments&lt;TResult&gt;</c>, <c>CircuitBreakerPredicateArguments&lt;TResult&gt;</c> ...).
/// Sonuç tipli taşınır; değer tipi sonuçlar kutulanmaz (boxing yok).
/// </summary>
public readonly struct OutcomeArguments<TResult>
{
    public OutcomeArguments(Outcome<TResult> outcome, AegisContext context, int attemptNumber)
    {
        Outcome = outcome;
        Context = context;
        AttemptNumber = attemptNumber;
    }

    /// <summary>Denemenin sonucu (değer ya da istisna).</summary>
    public Outcome<TResult> Outcome { get; }

    /// <summary>Çağrının bağlamı (iptal token'ı, özellikler, <see cref="AegisContext.OperationKey"/>).</summary>
    public AegisContext Context { get; }

    /// <summary>0 tabanlı deneme numarası (ilk çağrı 0). Deneme kavramı olmayan stratejilerde 0'dır.</summary>
    public int AttemptNumber { get; }
}
