using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Abstractions;

/// <summary>
/// Tip çıkarımlı <see cref="Outcome{TResult}"/> fabrikaları (Polly: <c>Outcome.FromResult</c>, <c>FromResultAsValueTask</c> ...):
/// <c>Outcome.FromResult(42)</c>; <c>Outcome&lt;T&gt;</c> döndüren geri çağrılarda <c>Outcome.FromExceptionAsValueTask&lt;T&gt;(ex)</c>.
/// </summary>
public static class Outcome
{
    public static Outcome<TResult> FromResult<TResult>(TResult result) => Outcome<TResult>.FromResult(result);

    public static Outcome<TResult> FromException<TResult>(Exception exception) => Outcome<TResult>.FromException(exception);

    public static ValueTask<Outcome<TResult>> FromResultAsValueTask<TResult>(TResult result) => new(Outcome<TResult>.FromResult(result));

    public static ValueTask<Outcome<TResult>> FromExceptionAsValueTask<TResult>(Exception exception) =>
        new(Outcome<TResult>.FromException(exception));
}
