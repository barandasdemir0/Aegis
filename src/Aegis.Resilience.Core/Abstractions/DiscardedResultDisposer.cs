namespace Aegis.Resilience.Core.Abstractions;

/// <summary>
/// Bir stratejinin ÇAĞIRANA HİÇ ULAŞTIRMAYACAĞI sonuçları (yeniden denenen sonuç, hedging'de kaybeden
/// denemenin sonucu) güvenli biçimde serbest bırakır (AEGIS-137).
/// <para>
/// <c>HttpResponseMessage</c>, <c>Stream</c>, <c>DbConnection</c> gibi sonuçlar atıldığında dispose edilmezse
/// her retry/hedging turu bir soket ya da bağlantı sızdırır. Polly aynı korumayı <c>DisposeHelper</c> ile uygular.
/// Dispose sırasında oluşan istisnalar yutulur: asıl işin sonucu/hatası, temizlik hatasıyla gölgelenmemelidir.
/// </para>
/// </summary>
public static class DiscardedResultDisposer
{
    public static void Dispose(object? result)
    {
        switch (result)
        {
            case null:
                return;
            case IDisposable disposable:
                try { disposable.Dispose(); } catch { /* temizlik hatası asıl akışı bozmamalı */ }
                return;
            case IAsyncDisposable asyncDisposable:
                // Senkron bağlamda en iyi çaba: ValueTask'ı gözlemleyip hataları yut
                try
                {
                    var vt = asyncDisposable.DisposeAsync();
                    if (!vt.IsCompleted)
                    {
                        _ = vt.AsTask().ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                    }
                }
                catch { /* yut */ }
                return;
        }
    }

    public static async ValueTask DisposeAsync(object? result)
    {
        switch (result)
        {
            case null:
                return;
            case IAsyncDisposable asyncDisposable:
                try { await asyncDisposable.DisposeAsync().ConfigureAwait(false); } catch { /* yut */ }
                return;
            case IDisposable disposable:
                try { disposable.Dispose(); } catch { /* yut */ }
                return;
        }
    }
}
