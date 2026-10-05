using System.Collections.Concurrent;

namespace Aegis.Resilience.Core.Strategies.Timeout;

/// <summary>
/// Zaman aşımı stratejisinin başarı yolunda tahsisi önlemek için sınırlı boyutlu <see cref="CancellationTokenSource"/> havuzu.
/// Yalnızca <c>CancellationTokenSource.TryReset</c> ile sıfırlanabilen (iptal edilmemiş) kaynaklar geri alınır;
/// iptal edilmiş olanlar dispose edilir ve havuza asla dönmez. Polly aynı yaklaşımı kullanır.
/// <para>
/// Not: Geri çağrı tamamlandıktan SONRA bağlamdaki token'ı saklayıp kullanan kod (ör. ateşle-unut arka plan işi),
/// kaynak başka bir çağrıya verildiğinde o çağrının zaman aşımını görebilir. Token'ı geri çağrının ömrü dışında
/// kullanmayın (Polly ile aynı sözleşme).
/// </para>
/// </summary>
internal static class CancellationTokenSourcePool
{
    private const int MaxPooled = 1024;

    private static readonly ConcurrentQueue<CancellationTokenSource> Pool = new();
    private static int _count;

    // İş parçacığı başına tek yuva: en sık durumda (kirala → aynı iş parçacığında iade) atomik işlem ve kuyruk maliyeti yok.
    [ThreadStatic]
    private static CancellationTokenSource? t_cached;

    public static CancellationTokenSource Rent()
    {
        var local = t_cached;
        if (local is not null)
        {
            t_cached = null;
            return local;
        }

        if (Pool.TryDequeue(out var cts))
        {
            Interlocked.Decrement(ref _count);
            return cts;
        }

        return new CancellationTokenSource();
    }

    public static void Return(CancellationTokenSource cts)
    {
        if (!cts.TryReset()) // iptal edilmiş: yeniden kullanılamaz
        {
            cts.Dispose();
            return;
        }

        if (t_cached is null)
        {
            t_cached = cts;
            return;
        }

        if (Interlocked.Increment(ref _count) > MaxPooled) // havuz dolu
        {
            Interlocked.Decrement(ref _count);
            cts.Dispose();
            return;
        }

        Pool.Enqueue(cts);
    }
}
