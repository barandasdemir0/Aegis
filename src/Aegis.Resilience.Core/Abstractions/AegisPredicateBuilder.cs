using System.Runtime.CompilerServices;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Abstractions;

/// <summary>
/// Akıcı koşul kurucusu (Polly: <c>PredicateBuilder</c>):
/// <code>
/// new AegisPredicateBuilder()
///     .Handle&lt;HttpRequestException&gt;()
///     .Handle&lt;TimeoutException&gt;(ex =&gt; ex.Message.Contains("db"))
///     .HandleInner&lt;SocketException&gt;()
///     .HandleResult&lt;HttpResponseMessage&gt;(r =&gt; (int)r.StatusCode &gt;= 500)
/// </code>
/// Kural yoksa hiçbir sonuç ele alınmaz. İstisna kuralı yoksa (yalnızca sonuç kuralı varsa) istisnalar ele alınmaz;
/// açık olması için istisnaları da istiyorsanız <see cref="HandleAnyException"/> ekleyin.
/// </summary>
public sealed class AegisPredicateBuilder : AegisPredicate
{
    private readonly List<Func<Exception, bool>> _exceptionRules = [];
    private readonly List<IResultRule> _resultRules = [];

    /// <summary><typeparamref name="TException"/> (ve türevleri) tipindeki istisnaları ele alır.</summary>
    public AegisPredicateBuilder Handle<TException>()
        where TException : Exception
    {
        _exceptionRules.Add(static ex => ex is TException);
        return this;
    }

    /// <summary>Koşulu sağlayan <typeparamref name="TException"/> istisnalarını ele alır.</summary>
    public AegisPredicateBuilder Handle<TException>(Func<TException, bool> predicate)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _exceptionRules.Add(ex => ex is TException typed && predicate(typed));
        return this;
    }

    /// <summary>İç istisnası (<see cref="Exception.InnerException"/>, AggregateException içindekiler dahil) eşleşenleri ele alır.</summary>
    public AegisPredicateBuilder HandleInner<TException>(Func<TException, bool>? predicate = null)
        where TException : Exception
    {
        _exceptionRules.Add(ex => HasInner(ex, predicate));
        return this;
    }

    /// <summary>Tüm istisnaları ele alır (iptal ve devre kesici kontrol akışı istisnaları stratejilerce yine hariç tutulur).</summary>
    public AegisPredicateBuilder HandleAnyException()
    {
        _exceptionRules.Add(static _ => true);
        return this;
    }

    /// <summary>Koşulu sağlayan <typeparamref name="TResult"/> sonuçlarını ele alır. Değer tipleri kutulanmaz.</summary>
    public AegisPredicateBuilder HandleResult<TResult>(Func<TResult, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _resultRules.Add(new ResultRule<TResult>(predicate));
        return this;
    }

    /// <summary><paramref name="value"/>'ya eşit sonuçları ele alır.</summary>
    public AegisPredicateBuilder HandleResult<TResult>(TResult value, IEqualityComparer<TResult>? comparer = null)
    {
        var equality = comparer ?? EqualityComparer<TResult>.Default;
        return HandleResult<TResult>(r => equality.Equals(r, value));
    }

    /// <inheritdoc />
    public override ValueTask<bool> ShouldHandleAsync<TResult>(OutcomeArguments<TResult> args) => new(ShouldHandle(args.Outcome));

    /// <summary>Senkron değerlendirme (kurucunun kuralları hep senkrondur).</summary>
    public bool ShouldHandle<TResult>(in Outcome<TResult> outcome)
    {
        if (outcome.Exception is { } exception)
        {
            foreach (var rule in _exceptionRules)
            {
                if (rule(exception))
                {
                    return true;
                }
            }

            return false;
        }

        foreach (var rule in _resultRules)
        {
            if (rule.Matches(outcome.Result))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasInner<TException>(Exception exception, Func<TException, bool>? predicate)
        where TException : Exception
    {
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                if (Matches(inner, predicate) || HasInner(inner, predicate))
                {
                    return true;
                }
            }

            return false;
        }

        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (Matches(inner, predicate))
            {
                return true;
            }
        }

        return false;

        static bool Matches(Exception candidate, Func<TException, bool>? filter) =>
            candidate is TException typed && (filter is null || filter(typed));
    }

    private interface IResultRule
    {
        bool Matches<TValue>(TValue? value);
    }

    private sealed class ResultRule<TRule>(Func<TRule, bool> predicate) : IResultRule
    {
        public bool Matches<TValue>(TValue? value)
        {
            // Aynı tip: JIT bu dalı derleme anında seçer; değer tipi için kutulama yoktur.
            if (typeof(TValue) == typeof(TRule))
            {
                return predicate(Unsafe.As<TValue?, TRule>(ref value));
            }

            // Farklı tip (ör. object pipeline sonucu, türetilmiş tip): tip testiyle eşleştir.
            return value is TRule typed && predicate(typed);
        }
    }
}
