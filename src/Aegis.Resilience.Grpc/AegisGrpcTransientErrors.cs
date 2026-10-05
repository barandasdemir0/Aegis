using System.Globalization;
using Grpc.Core;
using Aegis.Resilience.Core.Exceptions;

namespace Aegis.Resilience.Grpc;

/// <summary>
/// gRPC durum kodu sınıflandırması (gRPC "Status codes and their use in gRPC" rehberi ve A6). Yeniden deneme ile devre kesici
/// farklı soruları yanıtlar: yeniden deneme "bu çağrı tekrar gönderilirse geçebilir mi", devre kesici "hedef sağlıklı mı".
/// </summary>
public static class AegisGrpcTransientErrors
{
    /// <summary>
    /// Yeniden denenebilir durum: yalnızca <see cref="StatusCode.Unavailable"/> (gRPC ve Grpc.Net.Client varsayılanı; sunucu isteği
    /// işlemediğini garanti eder). <c>DeadlineExceeded</c> deadline tüm denemeleri kapsadığı için yeniden denenmez; <c>Internal</c>,
    /// <c>Aborted</c>, <c>ResourceExhausted</c> işlem kısmen yapılmış olabileceğinden yalnızca açıkça istenirse denenir.
    /// </summary>
    public static bool IsTransient(StatusCode statusCode) => statusCode == StatusCode.Unavailable;

    /// <summary>
    /// Hedefin sağlıksız olduğunu gösteren durum (devre kesici için hata): sunucu tarafı ve altyapı hataları. İstemci hataları
    /// (<c>InvalidArgument</c>, <c>NotFound</c>, <c>PermissionDenied</c>...) ve iptal sayılmaz.
    /// </summary>
    public static bool IsServerFault(StatusCode statusCode) => statusCode is
        StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Internal or StatusCode.Unknown or
        StatusCode.ResourceExhausted or StatusCode.DataLoss;

    /// <summary>
    /// Standart yeniden deneme koşulu: yeniden denenebilir durum ve sunucu "deneme" demiyorsa (negatif pushback), ya da deneme
    /// zaman aşımı (Aegis'in deneme başına süresi; tüm çağrının deadline'ı değil).
    /// </summary>
    public static bool ShouldRetry(Exception exception) => exception switch
    {
        RpcException rpc => (IsTransient(rpc.StatusCode) && !ServerForbidsRetry(rpc.Trailers)) || IsThrottledWithPushback(rpc),
        AegisTimeoutException => true,
        _ => false
    };

    /// <summary>
    /// Sunucu kotası dolu ama ne zaman deneneceğini bildirdi: <see cref="StatusCode.ResourceExhausted"/> + pozitif pushback (gRPC A6).
    /// Pushback'siz <c>ResourceExhausted</c> yeniden denenmez (kota hemen dolmayabilir). Aegis sunucusunun hız sınırı reddi bu
    /// biçimdedir; Aegis istemcisi bildirilen süre kadar bekleyip yeniden dener.
    /// </summary>
    public static bool IsThrottledWithPushback(RpcException exception) =>
        exception.StatusCode == StatusCode.ResourceExhausted && TryGetPushback(exception.Trailers, out var delay) && delay is not null;

    /// <summary>Sunucu negatif ya da geçersiz pushback ile yeniden denemeyi yasakladı mı.</summary>
    public static bool ServerForbidsRetry(Metadata? trailers) => TryGetPushback(trailers, out var delay) && delay is null;

    /// <summary>Standart devre kesici koşulu: sunucu tarafı hata ya da deneme zaman aşımı.</summary>
    public static bool IsCircuitFailure(Exception exception) => exception switch
    {
        RpcException rpc => IsServerFault(rpc.StatusCode),
        AegisTimeoutException => true,
        _ => false
    };

    /// <summary>
    /// Trailer'daki <see cref="AegisGrpcMetadata.RetryPushback"/>'i okur. Yoksa false. Varsa true ve <paramref name="delay"/>:
    /// bekleme süresi; null ise sunucu yeniden denemeyi yasaklamış demektir (negatif ya da geçersiz değer).
    /// </summary>
    public static bool TryGetPushback(Metadata? trailers, out TimeSpan? delay)
    {
        delay = null;
        var value = trailers?.GetValue(AegisGrpcMetadata.RetryPushback);
        if (value is null)
        {
            return false;
        }

        if (int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var milliseconds) && milliseconds >= 0)
        {
            delay = TimeSpan.FromMilliseconds(milliseconds);
        }

        return true;
    }
}
