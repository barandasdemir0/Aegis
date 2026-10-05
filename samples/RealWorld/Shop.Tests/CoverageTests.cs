using System.Net.Http.Json;
using System.Diagnostics;
using System.Net;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Microsoft.Extensions.Time.Testing;
using Shop.Api;
using Shop.Backends;

namespace Shop.Tests;

/// <summary>Genel API'nin geri kalanı: her genişletme metodu gerçek bir senaryoda.</summary>
public sealed class CoverageTests(ShopFixture shop) : IClassFixture<ShopFixture>
{
    private static FaultStep[] Fail(int status, int times) => [new FaultStep { Status = status, Times = times }];

    // AOP proxy yaşam süreleri: singleton (Task) ve transient (ValueTask) — ikisi de yeniden dener.
    [Fact]
    public async Task Proxies_SingletonAndTransient_Retry()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("cat-x", Fail(500, 1));
        shop.Eu.Faults.SetPlan("audit-x", Fail(500, 1));

        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/gateways/catalog/cat-x")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await shop.Client.PostAsync("/gateways/audit/audit-x", null)).StatusCode);
        Assert.Equal(2, shop.Eu.Faults.Calls("cat-x").Count);
        Assert.Equal(2, shop.Eu.Faults.Calls("audit-x").Count);
    }

    // Özel gRPC boru hattı (AddAegisGrpcResilience): gRPC hedging — yavaş ilk deneme beklenmez.
    [Fact]
    public async Task Grpc_CustomPipeline_Hedging()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("grpc.reserve", [new FaultStep { DelayMs = 3000 }]);

        var watch = Stopwatch.StartNew();
        using var response = await shop.Client.GetAsync("/inventory/hedged/h1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"hedging beklemedi: {watch.Elapsed}");
        Assert.Equal(2, shop.Eu.Faults.Calls("grpc.reserve").Count);
    }

    // Kaos hatası (AddChaosFault).
    [Fact]
    public async Task ChaosFault_Injected()
    {
        using var response = await shop.Client.GetAsync("/chaos-lab/fault");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("kaos-hata", (await ShopFixture.JsonOf(response)).GetProperty("message").GetString());
    }

    // .NET sabit pencere sınırlayıcısı (AddFixedWindowRateLimiter).
    [Fact]
    public async Task Coupons_FixedWindow()
    {
        Assert.Equal(HttpStatusCode.OK, (await shop.Client.PostAsync("/coupons", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await shop.Client.PostAsync("/coupons", null)).StatusCode);
        using var rejected = await shop.Client.PostAsync("/coupons", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
    }

    // Kompozisyon (AddPipeline): ödeme sayfası ortak boru hattının yeniden denemesini kullanır.
    [Fact]
    public async Task Checkout_ComposedPipeline()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("checkout", Fail(503, 2));

        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/checkout")).StatusCode);
        Assert.Equal(3, shop.Eu.Faults.Calls("checkout").Count);
    }

    // HandleTransientHttpErrors: 503 yeniden denenir, 404 denenmez.
    [Fact]
    public async Task Coupons_TransientHttpErrorPredicate()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("coupon-a", Fail(503, 1));
        shop.Eu.Faults.SetPlan("coupon-b", Fail(404, 5));

        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/coupons/validate/a")).StatusCode);
        Assert.Equal(2, shop.Eu.Faults.Calls("coupon-a").Count);
        Assert.Equal(HttpStatusCode.BadGateway, (await shop.Client.GetAsync("/coupons/validate/b")).StatusCode);
        Assert.Single(shop.Eu.Faults.Calls("coupon-b"));
    }

    // SetAegisContext / GetAegisContext / GetOrCreateAegisContext / GetRequestMetadata: çağıranın bağlamı istekle taşınır,
    // her deneme aynı korelasyon kimliğini gönderir; istek bitince bağlam çağıranda kalır.
    [Fact]
    public async Task Tracked_CallerContextFlowsThroughAllAttempts()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("tracked", Fail(503, 2));

        var body = await ShopFixture.JsonOf(await shop.Client.GetAsync("/tracked"));
        var correlationId = body.GetProperty("correlationId").GetString();

        var calls = shop.Eu.Faults.Calls("tracked");
        Assert.Equal(3, calls.Count);
        Assert.All(calls, c => Assert.Equal(correlationId, c.CorrelationId));
        Assert.True(body.GetProperty("sameContext").GetBoolean());
        Assert.Equal("Tracked", body.GetProperty("requestName").GetString());
    }

    // MapAegisStatus + ek sağlık kontrolü (AddAegisCheck): hazır olma uç noktası açık devrede Unhealthy, canlılık Degraded.
    [Fact]
    public async Task OpsStatus_AndReadinessCheck()
    {
        Assert.Contains("settlement", (await ShopFixture.JsonOf(await shop.Client.GetAsync("/ops/status"))).ToString());

        using (var isolate = new HttpRequestMessage(HttpMethod.Post, "/aegis/circuits/products/isolate"))
        {
            isolate.Headers.Add("X-Aegis-Action", "true");
            await shop.Client.SendAsync(isolate);
        }

        using (var ready = await shop.Client.GetAsync("/health/ready"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Equal("Unhealthy", (await ShopFixture.JsonOf(ready)).GetProperty("status").GetString());
        }

        Assert.Equal("Degraded", (await ShopFixture.JsonOf(await shop.Client.GetAsync("/health"))).GetProperty("status").GetString());

        using (var reset = new HttpRequestMessage(HttpMethod.Post, "/aegis/circuits/products/reset"))
        {
            reset.Headers.Add("X-Aegis-Action", "true");
            await shop.Client.SendAsync(reset);
        }

        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/health/ready")).StatusCode);
    }

    // WithTelemetry: DI dışı kurulan boru hattına bağlanan özel dinleyici olayları alır.
    [Fact]
    public async Task CustomTelemetryListener_ReceivesEvents()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("products", Fail(500, 1));

        await shop.Client.GetAsync($"/typed/{ShopFixture.NewSku("tl")}");

        var events = await shop.Client.GetFromJsonAsync<string[]>("/telemetry/recorded");
        Assert.Contains("typed-products:OnRetry", events!);
    }

    // WithTimeProvider: uygulamanın gerçek politikası sahte saatle birim testinde; 1 saatlik açılma süresi beklenmeden geçer.
    [Fact]
    public async Task SettlementPolicy_FakeClock_HalfOpenAfterAnHourWithoutWaiting()
    {
        var clock = new FakeTimeProvider();
        var pipeline = SettlementPolicy.Configure(new AegisPipelineBuilder("settlement-test"), clock).Build();
        ValueTask<int> Fail(Aegis.Resilience.Core.Context.AegisContext _) => throw new HttpRequestException("banka kapalı");

        await Assert.ThrowsAsync<HttpRequestException>(() => pipeline.ExecuteAsync(Fail).AsTask());
        await Assert.ThrowsAsync<HttpRequestException>(() => pipeline.ExecuteAsync(Fail).AsTask());
        await Assert.ThrowsAsync<BrokenCircuitException>(() => pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)).AsTask());

        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));

        // Aynı politika uygulamada sistem saatiyle çalışır.
        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/settlement")).StatusCode);
    }
}
