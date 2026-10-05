using System.Collections.Concurrent;

namespace Aegis.Resilience.Core.Context;

/// <summary>
/// Yüksek hacimli çağrılarda GC Gen0 bellek tahsisatını önlemek için yüksek performanslı thread-safe AegisContext havuzu.
/// </summary>
public static class AegisContextPool
{
    private static readonly ConcurrentQueue<AegisContext> Pool = new();
    private static int _poolCount;
    private const int MaxPoolSize = 1024;

    // İş parçacığı başına tek yuva: eşzamanlı yükte her çağrının paylaşılan kuyruğa ve sayaca (atomik işlemler, aynı önbellek
    // satırı) gitmesi çekişme yaratıyordu (64 iş parçacığında Retry, Polly'den ~2 kat yavaştı; benchmark ile ölçüldü).
    // Kiralama ve iade çoğunlukla aynı iş parçacığında olur; paylaşılan kuyruk yalnızca yuva doluysa kullanılır.
    [ThreadStatic]
    private static AegisContext? t_cached;

    /// <summary>
    /// Havuzdan temiz bir AegisContext kiralar.
    /// </summary>
    public static AegisContext Rent(CancellationToken cancellationToken = default, string? pipelineName = null)
    {
        var local = t_cached;
        if (local is not null)
        {
            t_cached = null;
            local.IsPooled = false;
            local.Reset(cancellationToken, pipelineName);
            return local;
        }

        if (Pool.TryDequeue(out var context))
        {
            Interlocked.Decrement(ref _poolCount);
            context.IsPooled = false;
            context.Reset(cancellationToken, pipelineName);
            return context;
        }

        return new AegisContext(cancellationToken, pipelineName);
    }

    /// <summary>
    /// <paramref name="parent"/>'ın izole alt bağlamını havuzdan kiralar (<see cref="AegisContext.CreateChild"/> eşdeğeri,
    /// tahsissiz). Çağrı bitmeden iade edilecek alt bağlamlar içindir; çağrıdan uzun yaşayabilecekse
    /// <c>DetachFromParent</c> çağrılmalıdır.
    /// </summary>
    internal static AegisContext RentChild(AegisContext parent, CancellationToken cancellationToken)
    {
        var child = Rent();
        child.InitializeAsChildOf(parent, cancellationToken);
        return child;
    }

    /// <summary>
    /// Kullanımı biten AegisContext'i havuza geri iade eder.
    /// Aynı bağlamın mükerrer iadesi yok sayılır (AEGIS-111).
    /// </summary>
    public static void Return(AegisContext? context)
    {
        if (context == null || context.IsPooled) return;

        if (t_cached is null)
        {
            context.Reset();
            context.IsPooled = true;
            t_cached = context;
            return;
        }

        if (Interlocked.Increment(ref _poolCount) <= MaxPoolSize)
        {
            context.Reset();
            context.IsPooled = true;
            Pool.Enqueue(context);
        }
        else
        {
            Interlocked.Decrement(ref _poolCount);
        }
    }
}
