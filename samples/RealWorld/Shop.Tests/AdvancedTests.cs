using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shop.Api;
using Shop.Backends;

namespace Shop.Tests;

/// <summary>İleri özellikler: HTTP işleyici seçenekleri, strateji seçenekleri, DI/telemetri biçimleri. Hepsi gerçek ağ üzerinden.</summary>
public sealed class AdvancedTests(ShopFixture shop) : IClassFixture<ShopFixture>
{
    private static FaultStep[] Fail(int status, int times) => [new FaultStep { Status = status, Times = times }];

    // SelectPipelineByAuthority: AB kargo firması çökünce yalnızca onun devresi açılır, ABD etkilenmez.
    [Fact]
    public async Task Shipping_CircuitPerCarrierAuthority()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("ship-eu", Fail(500, 100));

        // AB firmasının devresi bu sınıftaki diğer AB çağrılarının hatalarını da sayar: açılana kadar dene.
        var opened = false;
        for (var i = 0; i < 4 && !opened; i++)
        {
            opened = (await shop.Client.GetAsync("/shipping/eu")).StatusCode == HttpStatusCode.ServiceUnavailable;
        }

        Assert.True(opened, "AB kargo devresi açılmadı");
        var callsWhenOpen = shop.Eu.Faults.Calls("ship-eu").Count;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await shop.Client.GetAsync("/shipping/eu")).StatusCode);
        Assert.Equal(callsWhenOpen, shop.Eu.Faults.Calls("ship-eu").Count);                                  // istek gitmedi
        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/shipping/us")).StatusCode);            // diğer firma sağlam
    }

    // DisableRetryFor(DELETE): iptal isteği asla yeniden gönderilmez; aynı istemcide GET yeniden denenir (senkron Send dahil).
    [Fact]
    public async Task Shipping_DeleteNeverRetried_SyncGetRetried()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("cancel-eu", Fail(503, 5));
        shop.Eu.Faults.SetPlan("ship-sync", Fail(503, 1));

        Assert.Equal(HttpStatusCode.BadGateway, (await shop.Client.DeleteAsync("/shipping/eu")).StatusCode);
        Assert.Single(shop.Eu.Faults.Calls("cancel-eu"));

        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/shipping-sync")).StatusCode); // HttpClient.Send aynı çekirdekten
        Assert.Equal(2, shop.Eu.Faults.Calls("ship-sync").Count);
    }

    // ReturnFinalResponse: denemeler tükenince istisna yerine son yanıt (503) döner.
    [Fact]
    public async Task ShippingQuote_ReturnFinalResponse_GivesLast503()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("quote", Fail(503, 10));

        using var response = await shop.Client.GetAsync("/shipping-quote");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(503, (await ShopFixture.JsonOf(response)).GetProperty("carrierStatus").GetInt32());
        Assert.Equal(3, shop.Eu.Faults.Calls("quote").Count);
    }

    // İstek bazlı (dinamik) seçim: yüksek öncelik yeniden denenir, düşük öncelik denenmez.
    [Fact]
    public async Task Priority_DynamicHandler_SelectsPipelinePerRequest()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("priority", Fail(503, 1));
        using (var high = new HttpRequestMessage(HttpMethod.Get, "/priority"))
        {
            high.Headers.Add("X-Priority", "high");
            Assert.Equal(HttpStatusCode.OK, (await shop.Client.SendAsync(high)).StatusCode);
        }

        Assert.Equal(2, shop.Eu.Faults.Calls("priority").Count);

        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("priority", Fail(503, 1));
        using (var low = new HttpRequestMessage(HttpMethod.Get, "/priority"))
        {
            low.Headers.Add("X-Priority", "low");
            Assert.Equal(HttpStatusCode.BadGateway, (await shop.Client.SendAsync(low)).StatusCode);
        }

        Assert.Single(shop.Eu.Faults.Calls("priority"));
    }

    // Host adına göre boru hattı: "localhost" adlı boru hattı uygulanır.
    [Fact]
    public async Task ByHost_PipelineNamedAfterHost()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("by-host", Fail(503, 2));

        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/by-host")).StatusCode);
        Assert.Equal(3, shop.Eu.Faults.Calls("by-host").Count);
    }

    // Tek başına gövde tekrar oynatma: ekibin kendi retry işleyicisi geri sarılamayan gövdeyi eksiksiz yeniden gönderebilir.
    [Fact]
    public async Task Upload_ReplayHandler_ResendsNonSeekableBody()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("upload", Fail(503, 1));
        var payload = new string('x', 5000);

        using var response = await shop.Client.PostAsync("/upload", new StringContent(payload));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([5000, 5000], shop.Eu.Faults.Calls("upload").Select(c => c.Body!.Length));
    }

    // 5'i 1 arada standart zincir: hata yeniden denenir; takılan deneme 500 ms'de kesilir.
    [Fact]
    public async Task InventorySync_StandardResilienceFiveInOne()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("inventory-sync", [new FaultStep { Status = 500 }]);

        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/inventory-sync")).StatusCode);
        Assert.Equal(2, shop.Eu.Faults.Calls("inventory-sync").Count);

        shop.Eu.Faults.SetPlan("inventory-sync", [new FaultStep { DelayMs = 3000 }]);
        var watch = Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/inventory-sync")).StatusCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2.5), $"deneme zaman aşımı kesmedi: {watch.Elapsed}");
    }

    // Bellek içi önbellek: süre içinde arka uca gidilmez, süre dolunca yeniden alınır.
    [Fact]
    public async Task RecommendCache_InMemoryTtl()
    {
        shop.ResetFaults();
        var sku = ShopFixture.NewSku("rc");

        await shop.Client.GetAsync($"/recommend-cached/{sku}");
        await shop.Client.GetAsync($"/recommend-cached/{sku}");
        Assert.Single(shop.Eu.Faults.Calls("catalog"));

        await Task.Delay(1000);
        await shop.Client.GetAsync($"/recommend-cached/{sku}");
        Assert.Equal(2, shop.Eu.Faults.Calls("catalog").Count);
    }

    // Kuyruklu eşzamanlılık: 1 çalışan + 2 bekleyen sırayla tamamlanır; kuyruk doluysa red.
    [Fact]
    public async Task Notifications_BoundedQueue()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("notify", [new FaultStep { DelayMs = 300, Times = 10 }]);

        var watch = Stopwatch.StartNew();
        var queued = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => shop.Client.PostAsync("/notify", null)));
        Assert.All(queued, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(850), $"sırayla çalışmadı: {watch.Elapsed}");

        var burst = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => shop.Client.PostAsync("/notify", null)));
        Assert.Contains(burst, r => r.StatusCode == HttpStatusCode.TooManyRequests);
    }

    // Çekirdek token bucket: 2 izin, sonra 429 + Retry-After.
    [Fact]
    public async Task Sms_CoreTokenBucket()
    {
        shop.ResetFaults();
        Assert.Equal(HttpStatusCode.OK, (await shop.Client.PostAsync("/sms", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await shop.Client.PostAsync("/sms", null)).StatusCode);
        using var rejected = await shop.Client.PostAsync("/sms", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
    }

    // Gölge kip: devre "açık" kararı verir ama istek reddedilmez (üretime almadan önce izleme).
    [Fact]
    public async Task SearchV2_ShadowCircuit_OpensButNeverRejects()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("search-v2", Fail(500, 10));

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.BadGateway, (await shop.Client.GetAsync("/search-v2")).StatusCode);
        }

        Assert.Equal(5, shop.Eu.Faults.Calls("search-v2").Count); // hepsi arka uca gitti
        Assert.Equal("Open", (await ShopFixture.JsonOf(await shop.Client.GetAsync("/search-v2/state"))).GetProperty("state").GetString());
    }

    // Sayı penceresi (son 4 çağrı) + yarı açıkta art arda 2 başarı + açılma süresi üreticisi (400 → başarısız denemede 800 ms).
    [Fact]
    public async Task Warehouse_CountWindow_HalfOpenThreshold_BreakDurationGenerator()
    {
        shop.ResetFaults();
        async Task<string> State() => (await ShopFixture.JsonOf(await shop.Client.GetAsync("/warehouse/state"))).GetProperty("state").GetString()!;

        shop.Eu.Faults.SetPlan("warehouse", [new FaultStep { Times = 2 }, new FaultStep { Status = 500, Times = 2 }]);
        for (var i = 0; i < 4; i++)
        {
            await shop.Client.GetAsync("/warehouse");
        }

        Assert.Equal("Open", await State()); // son 4 çağrının yarısı hatalı
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await shop.Client.GetAsync("/warehouse")).StatusCode);

        await Task.Delay(500);
        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/warehouse")).StatusCode);
        Assert.Equal("HalfOpen", await State());                 // tek başarı yetmez
        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/warehouse")).StatusCode);
        Assert.Equal("Closed", await State());                   // art arda 2 başarı

        shop.Eu.Faults.SetPlan("warehouse", Fail(500, 10));
        for (var i = 0; i < 4; i++)
        {
            await shop.Client.GetAsync("/warehouse");
        }

        await Task.Delay(500);
        await shop.Client.GetAsync("/warehouse");                // deneme isteği başarısız → yeniden açılır, süre iki katı

        var breaks = (await ShopFixture.JsonOf(await shop.Client.GetAsync("/warehouse/state"))).GetProperty("breaks").EnumerateArray().Select(b => b.GetInt32());
        Assert.Equal([400, 400, 800], breaks);
    }

    // Yavaş çağrı oranı: hata vermeyen ama yavaşlayan bağımlılık devreyi açar.
    [Fact]
    public async Task Reporting_SlowCallRate_OpensCircuitWithoutErrors()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("reporting", [new FaultStep { DelayMs = 200, Times = 4 }]);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/reporting")).StatusCode);
        }

        Assert.Equal("Open", (await ShopFixture.JsonOf(await shop.Client.GetAsync("/reporting/state"))).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await shop.Client.GetAsync("/reporting")).StatusCode);
    }

    // Retry bütçesi: bağımlılık tamamen çökmüşken yeniden denemeler hızla kesilir (5 çağrı × 4 deneme = 20 yerine).
    [Fact]
    public async Task Budget_RetryThrottling_LimitsRetryStorm()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("budget", Fail(500, 100));

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.BadGateway, (await shop.Client.GetAsync("/budget")).StatusCode);
        }

        Assert.InRange(shop.Eu.Faults.Calls("budget").Count, 5, 8);
    }

    // Ayrık kaos stratejileri: gecikme, sahte sonuç, yan etki — çağrı bazında açılır.
    [Fact]
    public async Task ChaosLab_LatencyOutcomeBehavior()
    {
        var watch = Stopwatch.StartNew();
        Assert.Equal("backend", (await ShopFixture.JsonOf(await shop.Client.GetAsync("/chaos-lab/latency"))).GetProperty("source").GetString());
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(280), $"gecikme enjekte edilmedi: {watch.Elapsed}");

        Assert.Equal("chaos", (await ShopFixture.JsonOf(await shop.Client.GetAsync("/chaos-lab/outcome"))).GetProperty("source").GetString());

        var before = (await ShopFixture.JsonOf(await shop.Client.GetAsync("/chaos-lab/behaviors"))).GetProperty("count").GetInt32();
        await shop.Client.GetAsync("/chaos-lab/behavior");
        Assert.Equal(before + 1, (await ShopFixture.JsonOf(await shop.Client.GetAsync("/chaos-lab/behaviors"))).GetProperty("count").GetInt32());

        Assert.Equal("backend", (await ShopFixture.JsonOf(await shop.Client.GetAsync("/chaos-lab/none"))).GetProperty("source").GetString());
    }

    // AddAegisPipeline<TOptions>: seçenek değişince boru hattı yeniden kurulur (zaman aşımı canlı değişir).
    [Fact]
    public async Task Fees_PipelineRebuiltWhenOptionsChange()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("fees", [new FaultStep { DelayMs = 300, Times = 10 }]);
        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/fees")).StatusCode);

        shop.Api.Admin.Put(new Dictionary<string, string?> { ["Fees:TimeoutMs"] = "100" });
        Assert.Equal(HttpStatusCode.GatewayTimeout, (await shop.Client.GetAsync("/fees")).StatusCode);

        shop.Api.Admin.Put(new Dictionary<string, string?> { ["Fees:TimeoutMs"] = "1000" });
        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/fees")).StatusCode);
    }

    // Anahtarlı boru hattı + durumlu (closure'suz) çalıştırma biçimi.
    [Fact]
    public async Task Carrier_KeyedPipeline_StatefulExecution()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("ups", Fail(503, 1));

        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/carrier/ups")).StatusCode);
        Assert.Equal(2, shop.Eu.Faults.Calls("ups").Count);
    }

    // Tipli boru hattı (Build<Product>).
    [Fact]
    public async Task Typed_PipelineBuildOfT()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("products", Fail(500, 1));

        var body = await ShopFixture.JsonOf(await shop.Client.GetAsync($"/typed/{ShopFixture.NewSku("ty")}"));

        Assert.Equal("typed", body.GetProperty("source").GetString());
        Assert.Equal(2, shop.Eu.Faults.Calls("products").Count);
    }

    // Örnek adı (pipeline.instance) iz etiketinde + ConfigureAegisTelemetry ile otomatik günlük (Polly biçimi).
    [Fact]
    public async Task InstanceName_InTrace_AndAutomaticLogging()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("tenant-api", Fail(500, 1));
        var spans = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Aegis",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add
        };
        ActivitySource.AddActivityListener(listener);

        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/tenant-api")).StatusCode);

        var span = Assert.Single(spans, s => s.DisplayName == "Aegis tenant-api");
        Assert.Equal("blue", span.GetTagItem("pipeline.instance"));

        var logs = shop.Api.Services.GetRequiredService<LogSink>().Entries;
        Assert.Contains(logs, l => l.Category.StartsWith("Aegis", StringComparison.Ordinal) && l.Message.Contains("OnRetry") && l.Message.Contains("tenant-api"));
    }
}
