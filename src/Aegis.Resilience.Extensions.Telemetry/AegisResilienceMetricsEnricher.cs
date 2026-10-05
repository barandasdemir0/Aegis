using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.ExceptionSummarization;
using Microsoft.Extensions.Http.Diagnostics;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Telemetry;
using Aegis.Resilience.Extensions.DependencyInjection;

namespace Aegis.Resilience.Extensions.Telemetry;

/// <summary>
/// Standart metriklere <c>error.type</c>, <c>request.name</c> ve <c>request.dependency.name</c> etiketlerini ekler
/// (Microsoft: <c>ResilienceMetricsEnricher</c>). Özetleyici ve istek bağlamı DI'dan isteğe bağlı alınır.
/// </summary>
internal sealed class AegisResilienceMetricsEnricher(
    IExceptionSummarizer? exceptionSummarizer = null,
    IOutgoingRequestContext? outgoingRequestContext = null)
{
    public void Enrich(AegisEnrichmentContext context)
    {
        var telemetryEvent = context.TelemetryEvent;

        if (exceptionSummarizer is not null && telemetryEvent.Exception is { } exception)
        {
            context.Tags.Add(new(AegisResilienceTagNames.ErrorType, exceptionSummarizer.Summarize(exception).Description));
        }

        if ((telemetryEvent.Context.GetRequestMetadata() ?? outgoingRequestContext?.RequestMetadata) is { } requestMetadata)
        {
            context.Tags.Add(new(AegisResilienceTagNames.RequestName, requestMetadata.RequestName));
            context.Tags.Add(new(AegisResilienceTagNames.DependencyName, requestMetadata.DependencyName));
        }
    }
}
