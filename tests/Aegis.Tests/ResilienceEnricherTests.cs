using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.ExceptionSummarization;
using Microsoft.Extensions.Http.Diagnostics;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Telemetry;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.Http;
using Aegis.Resilience.Extensions.Telemetry;

namespace Aegis.Tests;

/// <summary>
/// Microsoft.Extensions.Resilience <c>AddResilienceEnricher</c> eşitliği: <c>error.type</c> (exception summarization),
/// <c>request.name</c> ve <c>request.dependency.name</c> etiketleri.
/// </summary>
public class ResilienceEnricherTests
{
    private sealed class TagCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        public ConcurrentQueue<Dictionary<string, object?>> Events { get; } = new();

        public TagCapture(string pipelineName)
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == AegisTelemetry.MeterName && instrument.Name == "aegis.strategy.events")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<int>((_, _, tags, _) =>
            {
                var dict = new Dictionary<string, object?>();
                foreach (var tag in tags)
                {
                    dict[tag.Key] = tag.Value;
                }

                if (Equals(dict.GetValueOrDefault(AegisTelemetryTags.PipelineName), pipelineName))
                {
                    Events.Enqueue(dict);
                }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public async Task Enricher_AddsSummarizedErrorType_AndRequestMetadataFromContext()
    {
        var name = $"enrich-{Guid.NewGuid():N}";
        using var capture = new TagCapture(name);
        var services = new ServiceCollection();
        services.AddExceptionSummarizer(b => b.AddHttpProvider());
        services.AddAegisResilienceEnricher();
        services.AddAegisResilienceEnricher(); // idempotent
        services.AddAegisPipeline(name, b => b.AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.Zero; }));
        await using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetRequiredService<IAegisPipelineRegistry>().GetPipeline(name);
        var context = new AegisContext();
        context.SetRequestMetadata(new RequestMetadata { RequestName = "SiparisGetir", DependencyName = "SiparisServisi" });
        var calls = 0;

        await pipeline.ExecuteAsync(_ => ++calls == 1 ? throw new TimeoutException() : ValueTask.FromResult(1), context);

        var retry = Assert.Single(capture.Events, e => Equals(e[AegisTelemetryTags.EventName], AegisEventNames.OnRetry));
        Assert.Equal("SiparisGetir", retry[AegisResilienceTagNames.RequestName]);
        Assert.Equal("SiparisServisi", retry[AegisResilienceTagNames.DependencyName]);
        Assert.False(string.IsNullOrEmpty(retry[AegisResilienceTagNames.ErrorType] as string)); // özetlenmiş hata türü
    }

    [Fact]
    public async Task Enricher_ReadsRequestMetadataAttachedToHttpRequest()
    {
        var name = $"enrich-http-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddAegisResilienceEnricher();
        services.AddAegisPipeline(name, b => b.AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.Zero; }));
        var responses = 0;
        services.AddHttpClient("enrich")
            .AddAegisResilienceHandler(name)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(() =>
                ++responses == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        await using var sp = services.BuildServiceProvider();
        using var capture = new TagCapture(name);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://stok.local/urun/5");
        request.SetRequestMetadata(new RequestMetadata { RequestName = "UrunGetir", DependencyName = "StokApi" });
        using var response = await sp.GetRequiredService<IHttpClientFactory>().CreateClient("enrich").SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var retry = Assert.Single(capture.Events, e => Equals(e[AegisTelemetryTags.EventName], AegisEventNames.OnRetry));
        Assert.Equal("UrunGetir", retry[AegisResilienceTagNames.RequestName]);
        Assert.Equal("StokApi", retry[AegisResilienceTagNames.DependencyName]);
    }

    private sealed class StubHandler(Func<HttpStatusCode> status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status()));
    }
}
