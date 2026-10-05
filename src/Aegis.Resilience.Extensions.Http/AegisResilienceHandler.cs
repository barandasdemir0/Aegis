using System.Net;
using System.Net.Http;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// HTTP isteklerini Aegis Resilience Boru Hattından (Pipeline) geçiren DelegatingHandler sınıfı.
/// </summary>
public sealed class AegisResilienceHandler : AegisDelegatingHandler
{
    private readonly IAegisPipeline _pipeline;
    private readonly HttpHandlerRules _rules;
    private readonly long _maxRequestBodySize;

    public AegisResilienceHandler(
        IAegisPipeline pipeline,
        bool handleHttpFailureStatuses = true,
        bool allowNonIdempotentRetry = false,
        long maxRequestBodySize = HttpRequestReplayHandler.DefaultMaxRequestBodySize)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _rules = HttpHandlerRules.Create(handleHttpFailureStatuses, allowNonIdempotentRetry);
        _maxRequestBodySize = maxRequestBodySize > 0 ? maxRequestBodySize : HttpRequestReplayHandler.DefaultMaxRequestBodySize;
    }

    protected override Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, AegisHttpSender innerSend, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // İstek üzerinde taşınan bağlam varsa yeniden kullanılır (CorrelationId ve teşhis verileri korunur); yoksa havuzdan.
        return HttpResilienceExecutor.ExecuteAsync(
            _pipeline,
            request,
            cancellationToken,
            innerSend,
            _rules,
            _maxRequestBodySize);
    }

    /// <summary>Geçici HTTP hata kodu mu (5xx, 408, 429); bkz. <see cref="AegisHttpTransientErrors.IsTransient(HttpStatusCode)"/>.</summary>
    public static bool IsTransientHttpFailure(HttpStatusCode statusCode) => AegisHttpTransientErrors.IsTransient(statusCode);

    /// <summary>
    /// RFC 9110 ve Finansal Idempotency standartlarına göre HTTP isteğinin güvenli/idempotent olup olmadığını denetler (AEGIS-103).
    /// GET, HEAD, OPTIONS, TRACE, PUT, DELETE idempotent kabul edilir.
    /// POST ve PATCH isteklerinde ise 'Idempotency-Key' veya 'X-Idempotency-Key' başlığı bulunmalıdır.
    /// </summary>
    public static bool IsIdempotent(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return HttpResilienceExecutor.IsIdempotent(request);
    }
}
