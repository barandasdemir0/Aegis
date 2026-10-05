using System.Net.Http;
using Aegis.Resilience.Extensions.DependencyInjection;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// HTTP isteklerini inceleyerek (Host, Route veya Header) IAegisPipelineRegistry üzerinden
/// ilgili boru hattını çalışma zamanında dinamik olarak seçen ve yürüten gelişmiş DelegatingHandler.
/// Gövde tamponlama, yeniden denemede istek klonlama ve idempotency koruması
/// <see cref="AegisResilienceHandler"/> ile birebir aynıdır.
/// </summary>
public sealed class AegisDynamicResilienceHandler : AegisDelegatingHandler
{
    private readonly IAegisPipelineRegistry _registry;
    private readonly Func<HttpRequestMessage, string> _pipelineSelector;
    private readonly HttpHandlerRules _rules;
    private readonly long _maxRequestBodySize;

    public AegisDynamicResilienceHandler(
        IAegisPipelineRegistry registry,
        Func<HttpRequestMessage, string> pipelineSelector,
        bool handleHttpFailureStatuses = true,
        bool allowNonIdempotentRetry = false,
        long maxRequestBodySize = HttpRequestReplayHandler.DefaultMaxRequestBodySize)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _pipelineSelector = pipelineSelector ?? throw new ArgumentNullException(nameof(pipelineSelector));
        _rules = HttpHandlerRules.Create(handleHttpFailureStatuses, allowNonIdempotentRetry);
        _maxRequestBodySize = maxRequestBodySize > 0 ? maxRequestBodySize : HttpRequestReplayHandler.DefaultMaxRequestBodySize;
    }

    protected override Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, AegisHttpSender innerSend, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pipelineName = _pipelineSelector(request);
        var pipeline = _registry.GetPipeline(pipelineName);

        return HttpResilienceExecutor.ExecuteAsync(
            pipeline,
            request,
            cancellationToken,
            innerSend,
            _rules,
            _maxRequestBodySize);
    }
}
