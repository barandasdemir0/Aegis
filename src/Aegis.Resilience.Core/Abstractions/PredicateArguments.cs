using System.Runtime.CompilerServices;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Abstractions;

/// <summary>
/// Kutulanmamış (tipten bağımsız) koşul girdisi: <c>AegisPredicate.Create</c> ile yazılan basit koşullar için.
/// </summary>
public readonly struct PredicateArguments
{
    public PredicateArguments(Exception? exception, object? result, AegisContext context, int attemptNumber)
    {
        Exception = exception;
        Result = result;
        Context = context;
        AttemptNumber = attemptNumber;
    }

    public Exception? Exception { get; }
    public object? Result { get; }
    public AegisContext Context { get; }
    public int AttemptNumber { get; }
}
