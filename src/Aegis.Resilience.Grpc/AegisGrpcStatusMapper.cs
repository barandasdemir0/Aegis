using System.Globalization;
using Grpc.Core;
using Aegis.Resilience.Core.Exceptions;

namespace Aegis.Resilience.Grpc;

/// <summary>
/// Aegis retlerini gRPC durumlarına çevirir; gRPC çağıranları (ve gRPC istemcisinin kendi retry'ı) <see cref="RpcException"/>
/// bekler. Öneri süresi varsa <see cref="AegisGrpcMetadata.RetryPushback"/> trailer'ı eklenir.
/// </summary>
public static class AegisGrpcStatusMapper
{
    /// <summary>
    /// Açık devre → <see cref="StatusCode.Unavailable"/> (hedef şu an hizmet veremez; yeniden denenebilir), hız sınırı ya da
    /// eşzamanlılık reddi → <see cref="StatusCode.ResourceExhausted"/>, Aegis zaman aşımı → <see cref="StatusCode.DeadlineExceeded"/>.
    /// Diğer istisnalar için null.
    /// </summary>
    public static RpcException? ToRpcException(Exception exception) => exception switch
    {
        BrokenCircuitException broken => Create(StatusCode.Unavailable, broken, broken.RetryAfter),
        RateLimitRejectedException rejected => Create(StatusCode.ResourceExhausted, rejected, rejected.RetryAfter),
        AegisTimeoutException timeout => Create(StatusCode.DeadlineExceeded, timeout, retryAfter: null),
        _ => null
    };

    private static RpcException Create(StatusCode code, Exception source, TimeSpan? retryAfter)
    {
        var trailers = new Metadata();
        if (retryAfter is { } wait)
        {
            var milliseconds = (long)Math.Ceiling(Math.Max(0, wait.TotalMilliseconds));
            trailers.Add(AegisGrpcMetadata.RetryPushback, Math.Min(milliseconds, int.MaxValue).ToString(CultureInfo.InvariantCulture));
        }

        return new RpcException(new Status(code, source.Message, source), trailers);
    }
}
