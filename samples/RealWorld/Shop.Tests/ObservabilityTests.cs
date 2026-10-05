using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Testing;
using Shop.Backends;

namespace Shop.Tests;

/// <summary>İşletme: pano (izleme + elle müdahale), sağlık kontrolü, metrikler (Polly etiketleri + zenginleştirici), iz, test tanımlayıcıları.</summary>
public sealed class ObservabilityTests(ShopFixture shop) : IClassFixture<ShopFixture>
{
    // Pano: boru hatları listelenir; devre elle izole edilince ürünler yedek değere düşer ve /health Degraded olur; sıfırlanınca düzelir.
    [Fact]
    public async Task Dashboard_IsolateCircuit_FallbackAndHealthDegraded_ThenReset()
    {
        shop.ResetFaults();
        var status = await ShopFixture.JsonOf(await shop.Client.GetAsync("/aegis/status"));
        Assert.Contains("products", status.ToString());

        using (var isolate = new HttpRequestMessage(HttpMethod.Post, "/aegis/circuits/products/isolate"))
        {
            isolate.Headers.Add("X-Aegis-Action", "true");
            Assert.Equal(HttpStatusCode.OK, (await shop.Client.SendAsync(isolate)).StatusCode);
        }

        var product = await ShopFixture.JsonOf(await shop.Client.GetAsync($"/products/{ShopFixture.NewSku("iso")}"));
        Assert.Equal("fallback", product.GetProperty("source").GetString());
        Assert.Empty(shop.Eu.Faults.Calls("products"));
        Assert.Equal("Degraded", (await ShopFixture.JsonOf(await shop.Client.GetAsync("/health"))).GetProperty("status").GetString());

        using (var reset = new HttpRequestMessage(HttpMethod.Post, "/aegis/circuits/products/reset"))
        {
            reset.Headers.Add("X-Aegis-Action", "true");
            Assert.Equal(HttpStatusCode.OK, (await shop.Client.SendAsync(reset)).StatusCode);
        }

        product = await ShopFixture.JsonOf(await shop.Client.GetAsync($"/products/{ShopFixture.NewSku("iso")}"));
        Assert.Equal("backend", product.GetProperty("source").GetString());
        Assert.Equal("Healthy", (await ShopFixture.JsonOf(await shop.Client.GetAsync("/health"))).GetProperty("status").GetString());
    }

    // CSRF koruması: özel başlık olmadan müdahale reddedilir.
    [Fact]
    public async Task Dashboard_ActionWithoutCsrfHeader_Forbidden()
    {
        using var response = await shop.Client.PostAsync("/aegis/circuits/products/isolate", null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Metrikler: Polly ile aynı adlı etiketler (event.name, pipeline.name, operation.key) + zenginleştiricinin request.name etiketi.
    [Fact]
    public async Task Metrics_RetryEvent_WithPollyCompatibleTagsAndRequestMetadata()
    {
        shop.ResetFaults();
        var events = new ConcurrentBag<Dictionary<string, object?>>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Aegis" && instrument.Name == "aegis.strategy.events")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<int>((_, _, tags, _) =>
        {
            var map = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                map[tag.Key] = tag.Value;
            }

            events.Add(map);
        });
        listener.Start();

        shop.Eu.Faults.SetPlan("products", [new FaultStep { Status = 500 }]);
        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync($"/products/{ShopFixture.NewSku("m")}")).StatusCode);

        var dump = string.Join(" | ", events.Select(e => string.Join(",", e.Select(kv => $"{kv.Key}={kv.Value}"))));
        var retry = events.FirstOrDefault(e => Equals(e.GetValueOrDefault("event.name"), "OnRetry") && Equals(e.GetValueOrDefault("pipeline.name"), "products"));
        Assert.True(retry is not null, $"{events.Count} olay: {dump}");
        Assert.Equal("GetProduct", retry.GetValueOrDefault("operation.key"));
        Assert.Equal("GetProduct", retry.GetValueOrDefault("request.name"));
        Assert.Equal("ProductService", retry.GetValueOrDefault("request.dependency.name"));
    }

    // İz: boru hattı başına span ("Aegis products"), içindeki HTTP isteği span'ın çocuğu.
    [Fact]
    public async Task Tracing_PipelineSpan_WithOperationKey_AndChildHttpSpan()
    {
        shop.ResetFaults();
        var spans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is "Aegis" or "System.Net.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add
        };
        ActivitySource.AddActivityListener(listener);

        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync($"/products/{ShopFixture.NewSku("t")}")).StatusCode);

        var pipelineSpan = Assert.Single(spans, s => s.DisplayName == "Aegis products");
        Assert.Equal("GetProduct", pipelineSpan.GetTagItem("operation.key"));
        Assert.Contains(spans, s => s.Source.Name == "System.Net.Http" && s.ParentSpanId == pipelineSpan.SpanId);
    }

    // Aegis.Resilience.Testing: DI ile kurulan boru hatlarının yapılandırması birim testte doğrulanır (Polly.Testing eşdeğeri).
    [Fact]
    public void PipelineDescriptors_MatchIntendedConfiguration()
    {
        var registry = shop.Api.Services.GetRequiredService<IAegisPipelineRegistry>();

        var products = registry.GetPipeline("products").GetPipelineDescriptor();
        Assert.Equal(["Fallback", "Cache", "CircuitBreaker", "RequestCollapser", "Retry", "Timeout"], products.Strategies.Select(s => s.Name));

        var orders = registry.GetPipeline("orders-db").GetPipelineDescriptor();
        Assert.Equal(5, orders.GetOptions<RetryOptions>().MaxRetryAttempts);
        Assert.Equal(DelayBackoffType.DecorrelatedJitter, orders.GetOptions<RetryOptions>().BackoffType);

        Assert.True(registry.GetPipeline("reloadable").GetPipelineDescriptor().IsReloadable);
    }
}
