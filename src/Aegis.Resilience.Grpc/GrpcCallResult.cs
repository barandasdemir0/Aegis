using Grpc.Core;

namespace Aegis.Resilience.Grpc;

/// <summary>Kazanan denemenin çağrısı: başlıklar, durum ve trailer'lar buradan okunur (çağrı türünden bağımsız).</summary>
internal interface IGrpcCallResult : IDisposable
{
    Task<Metadata> ResponseHeadersAsync { get; }

    Status GetStatus();

    Metadata GetTrailers();
}

/// <summary>
/// Çağırana verilen gRPC çağrı nesnesinin durum/trailer/dispose işlevleri. Grpc.Core'un durum nesnesi alan yapıcılarıyla kullanılır:
/// delegeler statiktir, çağrı başına closure ve delege tahsisi yapılmaz (.NET 10 sıcak yolu).
/// </summary>
internal static class GrpcCallResult<T>
    where T : IGrpcCallResult
{
    public static readonly Func<object, Task<Metadata>> HeadersOf = static state => Headers((Task<T>)state);

    public static readonly Func<object, Status> Status = static state => StatusOf((Task<T>)state);

    public static readonly Func<object, Metadata> Trailers = static state => TrailersOf((Task<T>)state);

    public static readonly Action<object> Dispose = static state => DisposeWhenDone((Task<T>)state);

    /// <summary>Başlıklar: çağrı eşzamanlı bittiyse kazananın görevi doğrudan (tahsissiz), aksi halde bitince.</summary>
    public static Task<Metadata> Headers(Task<T> result) =>
        result.Status == TaskStatus.RanToCompletion ? result.Result.ResponseHeadersAsync : HeadersAsync(result);

    private static async Task<Metadata> HeadersAsync(Task<T> result) =>
        await (await result.ConfigureAwait(false)).ResponseHeadersAsync.ConfigureAwait(false);

    // gRPC sözleşmesi: durum ve trailer'lar çağrı bitince okunur; başarısız çağrıda RpcException'daki değerler döner.
    private static Status StatusOf(Task<T> result) => result.Status switch
    {
        TaskStatus.RanToCompletion => result.Result.GetStatus(),
        TaskStatus.Faulted when result.Exception?.InnerException is RpcException rpc => rpc.Status,
        TaskStatus.Faulted or TaskStatus.Canceled => new Status(StatusCode.Unknown, result.Exception?.InnerException?.Message ?? "Çağrı başarısız."),
        _ => throw new InvalidOperationException("Çağrı henüz tamamlanmadı; durum okunamaz.")
    };

    private static Metadata TrailersOf(Task<T> result) => result.Status switch
    {
        TaskStatus.RanToCompletion => result.Result.GetTrailers(),
        TaskStatus.Faulted when result.Exception?.InnerException is RpcException rpc => rpc.Trailers,
        TaskStatus.Faulted or TaskStatus.Canceled => new Metadata(),
        _ => throw new InvalidOperationException("Çağrı henüz tamamlanmadı; trailer'lar okunamaz.")
    };

    public static void DisposeWhenDone(Task<T> result)
    {
        if (result.Status == TaskStatus.RanToCompletion)
        {
            result.Result.Dispose();
            return;
        }

        // Henüz bitmediyse: bitince bırak (çağıran erken dispose etti).
        _ = result.ContinueWith(static t =>
        {
            if (t.Status == TaskStatus.RanToCompletion)
            {
                t.Result.Dispose();
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
