using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace Aegis.Resilience.AspNetCore;

/// <summary>
/// Gelen isteğin reddini yazar (hız sınırı ve uç nokta boru hattı ara katmanları paylaşır). HTTP isteğine HTTP durum kodu
/// (429 / 503 / 504) ve <c>Retry-After</c>; gRPC isteğine "trailers-only" gRPC yanıtı: HTTP 200, <c>grpc-status</c>,
/// <c>grpc-message</c> ve <c>grpc-retry-pushback-ms</c>. gRPC istemcisi HTTP 429'u yalnızca anlamsız bir <c>Unavailable</c>'a
/// çevirir (HTTP→gRPC eşleme tablosu) ve öneri süresi kaybolurdu.
/// </summary>
internal static class InboundRejection
{
    // gRPC durum kodları (Grpc.Core bağımlılığı olmadan; numaralar gRPC spesifikasyonunda sabittir).
    public const int GrpcDeadlineExceeded = 4;
    public const int GrpcResourceExhausted = 8;
    public const int GrpcUnavailable = 14;

    public static bool IsGrpc(HttpRequest request) =>
        request.ContentType is { } contentType && contentType.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reddi yazar. Yanıt başlamışsa (akış yazılmaya başlandıysa) hiçbir şey yapılmaz.</summary>
    public static void Write(HttpContext context, int httpStatus, int grpcStatus, string message, TimeSpan? retryAfter)
    {
        var response = context.Response;
        if (response.HasStarted)
        {
            return;
        }

        if (!IsGrpc(context.Request))
        {
            response.StatusCode = httpStatus;
            if (retryAfter is { } wait)
            {
                response.Headers.RetryAfter = Math.Max(1, (long)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            }

            return;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/grpc";
        response.Headers["grpc-status"] = grpcStatus.ToString(CultureInfo.InvariantCulture);
        response.Headers["grpc-message"] = PercentEncode(message);
        if (retryAfter is { } pushback)
        {
            var milliseconds = Math.Min(int.MaxValue, (long)Math.Ceiling(Math.Max(0, pushback.TotalMilliseconds)));
            response.Headers["grpc-retry-pushback-ms"] = milliseconds.ToString(CultureInfo.InvariantCulture);
        }
    }

    // gRPC spesifikasyonu: grpc-message, 0x20-0x7E dışındaki baytlar ve '%' yüzde kodlanmış UTF-8'dir.
    private static string PercentEncode(string message)
    {
        var builder = new StringBuilder(message.Length);
        foreach (var b in Encoding.UTF8.GetBytes(message))
        {
            if (b is >= 0x20 and <= 0x7E && b != (byte)'%')
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }
}
