using System.Net.Http;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// Bir HTTP işleyicisinin kuralları; tek kaynak olarak <see cref="HttpResilienceExecutor"/>'a verilir.
/// <list type="bullet">
/// <item>Geçici durum kodları (5xx, 408, 429) hata sayılır mı.</item>
/// <item>Denemeler tükenince son geçici yanıt mı döner (Microsoft davranışı), istisna mı (Aegis varsayılanı).</item>
/// <item>İstek yeniden gönderilebilir mi: açıkça kapatılan yöntemler (<c>DisableRetryFor</c>) hiçbir koşulda
/// gönderilmez; diğerleri idempotent ise (RFC 9110 veya Idempotency-Key) ya da idempotent olmayanlara izin verildiyse gönderilir.</item>
/// </list>
/// </summary>
internal sealed class HttpHandlerRules
{
    /// <summary>Microsoft <c>DisableForUnsafeHttpMethods</c> ile aynı liste: POST, PATCH, PUT, DELETE, CONNECT.</summary>
    internal static readonly HttpMethod[] UnsafeMethods =
        [HttpMethod.Post, new HttpMethod("PATCH"), HttpMethod.Put, HttpMethod.Delete, new HttpMethod("CONNECT")];

    private readonly bool _allowNonIdempotentRetry;
    private readonly HashSet<HttpMethod> _retryDisabledMethods;

    private HttpHandlerRules(
        bool handleHttpFailureStatuses, bool allowNonIdempotentRetry, HashSet<HttpMethod> retryDisabledMethods, bool returnFinalResponse)
    {
        HandleHttpFailureStatuses = handleHttpFailureStatuses;
        ReturnFinalResponse = returnFinalResponse;
        _allowNonIdempotentRetry = allowNonIdempotentRetry;
        _retryDisabledMethods = retryDisabledMethods;
    }

    public bool HandleHttpFailureStatuses { get; }

    public bool ReturnFinalResponse { get; }

    public static HttpHandlerRules Create(
        bool handleHttpFailureStatuses,
        bool allowNonIdempotentRetry,
        IReadOnlyCollection<HttpMethod>? retryDisabledMethods = null,
        bool returnFinalResponse = false) =>
        new(handleHttpFailureStatuses, allowNonIdempotentRetry, [.. retryDisabledMethods ?? []], returnFinalResponse);

    /// <summary><c>DisableRetryFor</c> uygulamalarının ortak gövdesi: yöntemleri doğrulayıp kümeye ekler.</summary>
    internal static void AddDisabled(HashSet<HttpMethod> target, HttpMethod[] methods)
    {
        ArgumentNullException.ThrowIfNull(methods);
        foreach (var method in methods)
        {
            target.Add(method ?? throw new ArgumentException("Yöntem listesi null öğe içeremez.", nameof(methods)));
        }
    }

    public bool AllowsResend(HttpRequestMessage request) =>
        !_retryDisabledMethods.Contains(request.Method) &&
        (_allowNonIdempotentRetry || HttpResilienceExecutor.IsIdempotent(request));
}
