using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Strategies.Timeout;

namespace Aegis.Resilience.Core.Strategies.Hedging;

/// <summary>
/// Bir hedging denemesi ve kaynak ömrü: görev + havuzlanmış iptal kaynağı + çağıran iptaline kayıt + izole alt bağlam.
/// Kaybeden/terk edilen deneme iptal edilir; bittiğinde sonucu (IDisposable ise) bırakılır ve kaynakları havuza döner.
/// </summary>
internal sealed class HedgedAttempt<TResult>
{
    public required Task<Outcome<TResult>> Task { get; init; }

    public required CancellationTokenSource Cts { get; init; }

    public required CancellationTokenRegistration ParentRegistration { get; init; }

    public required AegisContext Context { get; init; }

    /// <summary>Deneme BİTTİKTEN sonra çağrılır: kaydı kaldırır, kaynağı havuza iade eder (iptal edildiyse dispose).</summary>
    public void Release()
    {
        ParentRegistration.Dispose();
        CancellationTokenSourcePool.Return(Cts);
    }

    /// <summary>Denemeyi iptal eder ve bitince kaynaklarını bırakır (kaybeden ya da terk edilen deneme).</summary>
    public void Abandon()
    {
        try
        {
            Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // İptal sırasında oluşabilecek yarış durumlarını bastır
        }

        ReleaseWhenCompleted();
    }

    /// <summary>
    /// Bittiğinde istisnasını gözlemler (UnobservedTaskException koruması), sonradan ürettiği IDisposable sonucu serbest
    /// bırakır ve iptal kaynağını havuza iade eder.
    /// </summary>
    public void ReleaseWhenCompleted() =>
        Task.ContinueWith(static (t, s) =>
        {
            // AEGIS-137: Kaybeden deneme sonradan bir sonuç üretirse bu sonuç kimseye ulaşmaz; IDisposable ise
            // (HttpResponseMessage, Stream, DbConnection...) burada serbest bırakılmalıdır. Aksi halde her
            // hedging kazanımı bir bağlantı/soket sızdırır (Polly: ExecuteAsync_EnsureDiscardedResultDisposed).
            if (t.IsCompletedSuccessfully)
            {
                if (t.Result.IsSuccess)
                {
                    DiscardedResultDisposer.Dispose(t.Result.Result);
                }
                else if (t.Result.Exception is HedgingOutcome.HandledResult<TResult> handled)
                {
                    // Koşulun "kötü" saydığı ve atılan sonuç (ör. 503 HttpResponseMessage) da serbest bırakılır.
                    DiscardedResultDisposer.Dispose(handled.Original.Result);
                }
            }
            else
            {
                _ = t.Exception; // Unobserved task exception oluşmasını engelle
            }

            ((HedgedAttempt<TResult>)s!).Release();
        }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>Denemenin iptal kaynağını çağıranın iptaline bağlar (çağıran iptal ederse deneme de iptal olur).</summary>
    public static CancellationTokenRegistration RegisterParent(CancellationToken parentToken, CancellationTokenSource attemptCts) =>
        parentToken.CanBeCanceled
            ? parentToken.UnsafeRegister(static s => ((CancellationTokenSource)s!).Cancel(), attemptCts)
            : default;
}
