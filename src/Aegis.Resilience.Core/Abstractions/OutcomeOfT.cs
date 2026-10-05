using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Abstractions;

/// <summary>
/// Bir yürütmenin sonucu: ya değer ya istisna. Stratejiler başarısızlığı katmanlar arasında FIRLATMAK yerine bu yapıyla
/// taşır; istisna yalnızca boru hattının en dışında bir kez fırlatılır. Her async katmanda yeniden fırlatma (ve her
/// seferinde yığın izinin güncellenmesi) böylece önlenir (Polly v8 ile aynı yaklaşım).
/// </summary>
public readonly struct Outcome<TResult>
{
    private Outcome(TResult? result, Exception? exception)
    {
        Result = result;
        Exception = exception;
    }

    /// <summary>Başarılı sonuç değeri (<see cref="Exception"/> null ise geçerlidir).</summary>
    public TResult? Result { get; }

    /// <summary>Başarısızlık istisnası; başarılıysa null.</summary>
    public Exception? Exception { get; }

    /// <summary>İstisna yoksa true.</summary>
    public bool IsSuccess => Exception is null;

#pragma warning disable CA1000 // Generic tipte statik fabrika bilinçli: Outcome<T>.FromResult(...) okunaklı ve tip çıkarımı gerektirmez (Polly ile aynı desen).
    public static Outcome<TResult> FromResult(TResult result) => new(result, null);

    public static Outcome<TResult> FromException(Exception exception) =>
        new(default, exception ?? throw new ArgumentNullException(nameof(exception)));
#pragma warning restore CA1000

    /// <summary>Sonucu döner ya da istisnayı (daha önce fırlatılmışsa özgün yığın iziyle) fırlatır.</summary>
    public TResult GetResultOrThrow()
    {
        if (Exception is { } exception)
        {
            ExceptionDispatchInfo.Throw(exception);
        }

        return Result!;
    }

    /// <summary>İstisna varsa (özgün yığın iziyle) fırlatır; başarılıysa hiçbir şey yapmaz (Polly: <c>ThrowIfException</c>).</summary>
    public void ThrowIfException()
    {
        if (Exception is { } exception)
        {
            ExceptionDispatchInfo.Throw(exception);
        }
    }
}
