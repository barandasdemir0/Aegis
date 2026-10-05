using System.Runtime.CompilerServices;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Abstractions;

/// <summary>
/// Bağlam, deneme numarası ve sonuç/istisna görebilen, gerektiğinde asenkron çalışabilen koşul
/// (Polly: <c>Func&lt;XPredicateArguments&lt;T&gt;, ValueTask&lt;bool&gt;&gt; ShouldHandle</c>).
/// <para>
/// Polly'deki gibi tipli pipeline gerektirmez: aynı koşul her sonuç tipiyle çalışır ve değer tipi sonuçlar kutulanmaz
/// (<see cref="AegisPredicateBuilder.HandleResult{T}(Func{T, bool})"/>). Tek bir koşul Retry, Circuit Breaker ve diğer
/// stratejilerde ortak kullanılabilir.
/// </para>
/// </summary>
public abstract class AegisPredicate
{
    /// <summary>Sonucun ele alınıp alınmayacağını (ör. yeniden denenip denenmeyeceğini) belirler.</summary>
    public abstract ValueTask<bool> ShouldHandleAsync<TResult>(OutcomeArguments<TResult> args);

    /// <summary>Asenkron, bağlam farkındalıklı koşul oluşturur (sonuç <see cref="object"/> olarak verilir).</summary>
    public static AegisPredicate Create(Func<PredicateArguments, ValueTask<bool>> predicate) =>
        new DelegatePredicate(predicate ?? throw new ArgumentNullException(nameof(predicate)));

    /// <summary>Senkron, bağlam farkındalıklı koşul oluşturur.</summary>
    public static AegisPredicate Create(Func<PredicateArguments, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new DelegatePredicate(args => new ValueTask<bool>(predicate(args)));
    }

    /// <summary>
    /// Stratejilerin eski koşullarından (<c>Predicate&lt;Exception&gt;</c> + <c>Func&lt;object?, bool&gt;</c>) koşul üretir.
    /// Davranış eskisiyle aynıdır: istisna koşulu yoksa her istisna ele alınır, sonuç koşulu yoksa hiçbir sonuç ele alınmaz.
    /// </summary>
    internal static bool EvaluateLegacy<TResult>(in Outcome<TResult> outcome, Predicate<Exception>? shouldHandle, Func<object?, bool>? shouldHandleResult) =>
        outcome.Exception is { } exception
            ? shouldHandle?.Invoke(exception) ?? true
            : shouldHandleResult?.Invoke(outcome.Result) ?? false;

    private sealed class DelegatePredicate(Func<PredicateArguments, ValueTask<bool>> predicate) : AegisPredicate
    {
        public override ValueTask<bool> ShouldHandleAsync<TResult>(OutcomeArguments<TResult> args) =>
            predicate(new PredicateArguments(args.Outcome.Exception, args.Outcome.Exception is null ? args.Outcome.Result : null, args.Context, args.AttemptNumber));
    }
}
