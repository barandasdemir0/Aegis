using System.Net;
using System.Net.Http;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Exceptions;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// Geçici (yeniden denenmesi anlamlı) HTTP hatalarının tek tanımı (Microsoft: <c>HttpClientResiliencePredicates</c>,
/// Polly: <c>HttpPolicyExtensions.HandleTransientHttpError</c>). Tüm Aegis HTTP işleyicileri bunu kullanır; özel
/// stratejilerde de aynı kuralla karar vermek için geneldir.
/// </summary>
public static class AegisHttpTransientErrors
{
    /// <summary>Durum kodu geçici mi: 5xx, 408 (Request Timeout) veya 429 (Too Many Requests).</summary>
    public static bool IsTransient(HttpStatusCode statusCode) =>
        (int)statusCode >= 500 || statusCode == HttpStatusCode.RequestTimeout || (int)statusCode == 429;

    /// <summary>Yanıt geçici bir hata mı (bkz. <see cref="IsTransient(HttpStatusCode)"/>).</summary>
    public static bool IsTransient(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return IsTransient(response.StatusCode);
    }

    /// <summary>
    /// İstisna geçici mi: ağ hatası (<see cref="HttpRequestException"/>), Aegis zaman aşımı (<see cref="AegisTimeoutException"/>)
    /// ya da bağlantı kurma zaman aşımı (bkz. <see cref="IsConnectionTimeout"/>). Çağıranın iptali geçici değildir.
    /// </summary>
    public static bool IsTransient(Exception exception, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is HttpRequestException or AegisTimeoutException || IsConnectionTimeout(exception, cancellationToken);
    }

    /// <summary>
    /// Hedging için geçici mi: <see cref="IsTransient(Exception, CancellationToken)"/> artı açık devre
    /// (<see cref="BrokenCircuitException"/>): bir uç noktanın devresi açıksa sonraki deneme başka uç noktaya gidebilir
    /// (Microsoft: <c>HttpClientHedgingResiliencePredicates.IsTransient</c>).
    /// </summary>
    public static bool IsTransientForHedging(Exception exception, CancellationToken cancellationToken = default) =>
        IsTransient(exception, cancellationToken) || exception is BrokenCircuitException;

    /// <summary>
    /// Bağlantı kurma zaman aşımı mı (<c>SocketsHttpHandler.ConnectTimeout</c>): içinde <see cref="TimeoutException"/> taşıyan
    /// <see cref="OperationCanceledException"/>, ancak <paramref name="cancellationToken"/> iptal EDİLMEMİŞKEN. Çağıran ya da
    /// <c>HttpClient.Timeout</c> iptal ettiyse token iptal olur ve bu bir zaman aşımı değil, iptal sayılır.
    /// Microsoft ayrıca <c>Source == "System.Private.CoreLib"</c> denetler; Aegis denetlemez çünkü .NET Framework'te kaynak
    /// <c>mscorlib</c>'tir ve bu koşul net462 hedefinde bağlantı zaman aşımını hiç tanımazdı.
    /// </summary>
    public static bool IsConnectionTimeout(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && exception is OperationCanceledException { InnerException: TimeoutException };

    /// <summary>
    /// Koşul oluşturucuya geçici HTTP hatalarını ekler (Microsoft varsayılan <c>ShouldHandle</c>'ı ile aynı küme): geçici
    /// yanıtlar, <see cref="HttpRequestException"/> ve <see cref="AegisTimeoutException"/>. Ör.
    /// <c>o.ShouldHandleOutcome = new AegisPredicateBuilder().HandleTransientHttpErrors()</c>.
    /// </summary>
    public static AegisPredicateBuilder HandleTransientHttpErrors(this AegisPredicateBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .Handle<HttpRequestException>()
            .Handle<AegisTimeoutException>()
            .HandleResult<HttpResponseMessage>(IsTransient);
    }
}
