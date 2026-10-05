using System.Diagnostics;
using System.Net;
using Shop.Backends;

namespace Shop.Tests;

/// <summary>Platform özellikleri: AOP, kötümser zaman aşımı, adaptif limit, kaos, canlı ayar, kiracı başına devre, gelen istek koruması, Aspire.</summary>
public sealed class PlatformTests(ShopFixture shop) : IClassFixture<ShopFixture>
{
    // AOP: arayüzdeki [AegisPolicy] — servis kodunda tek satır dayanıklılık yok, yine de yeniden denenir.
    [Fact]
    public async Task Legacy_AopProxyAsync_RetriesTransientFailures()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("legacy", [new FaultStep { Status = 500, Times = 2 }]);

        using var response = await shop.Client.GetAsync("/legacy/42");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, shop.Eu.Faults.Calls("legacy").Count);
    }

    // AOP senkron metot + kötümser zaman aşımı: token'a saygı göstermeyen senkron çağrı 400 ms'de terk edilir.
    [Fact]
    public async Task Legacy_AopProxySync_PessimisticTimeoutAbandonsHangingCall()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("legacy", [new FaultStep { DelayMs = 5000, Times = 3 }]);

        var watch = Stopwatch.StartNew();
        using var response = await shop.Client.GetAsync("/legacy-sync/7");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("AegisTimeoutException", (await ShopFixture.JsonOf(response)).GetProperty("error").GetString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"kötümser zaman aşımı terk etmedi: {watch.Elapsed}");
    }

    // Adaptif eşzamanlılık: bağımlılık yavaşlayınca limit kendiliğinden daralır, sonra fazla yük reddedilir.
    [Fact]
    public async Task Adaptive_DependencySlowsDown_LimitShrinks()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("slow", [new FaultStep { DelayMs = 5, Times = 10 }, new FaultStep { DelayMs = 150, Times = 30 }]);
        var initial = (await ShopFixture.JsonOf(await shop.Client.GetAsync("/adaptive/limit"))).GetProperty("limit").GetInt32();

        for (var i = 0; i < 25; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/adaptive")).StatusCode);
        }

        var shrunk = (await ShopFixture.JsonOf(await shop.Client.GetAsync("/adaptive/limit"))).GetProperty("limit").GetInt32();
        Assert.True(shrunk < initial, $"limit daralmadı: {initial} → {shrunk}");

        shop.Eu.Faults.SetPlan("slow", [new FaultStep { DelayMs = 400, Times = 50 }]);
        var burst = await Task.WhenAll(Enumerable.Range(0, shrunk + 6).Select(_ => shop.Client.GetAsync("/adaptive")));
        Assert.Contains(burst, r => r.StatusCode == HttpStatusCode.TooManyRequests);
    }

    // Kaos kill switch: yapılandırma değişince kaos anında açılır ve kapanır (yeniden başlatma yok).
    [Fact]
    public async Task Chaos_ToggledLiveFromConfiguration()
    {
        shop.ResetFaults();
        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/chaos/k1")).StatusCode);

        shop.Api.Admin.Put(new Dictionary<string, string?> { ["Chaos:Enabled"] = "true", ["Chaos:Rate"] = "1" });
        using (var injected = await shop.Client.GetAsync("/chaos/k1"))
        {
            Assert.Equal(HttpStatusCode.BadGateway, injected.StatusCode);
            Assert.Contains("kaos", (await ShopFixture.JsonOf(injected)).GetProperty("message").GetString());
        }

        shop.Api.Admin.Put(new Dictionary<string, string?> { ["Chaos:Enabled"] = "false" });
        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/chaos/k1")).StatusCode);
    }

    // Canlı yeniden yükleme: yapılandırmadaki yeniden deneme sayısı değişince boru hattı yeniden kurulur.
    [Fact]
    public async Task Reloadable_RetryCountChangesLive()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("reload", [new FaultStep { Status = 500 }]);
        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/reloadable")).StatusCode); // 1 yeniden deneme

        shop.Api.Admin.Put(new Dictionary<string, string?> { ["Retries:Attempts"] = "0" });
        shop.Eu.Faults.Reset();
        shop.Eu.Faults.SetPlan("reload", [new FaultStep { Status = 500 }]);
        Assert.Equal(HttpStatusCode.BadGateway, (await shop.Client.GetAsync("/reloadable")).StatusCode); // artık denemez
        Assert.Single(shop.Eu.Faults.Calls("reload"));

        shop.Api.Admin.Put(new Dictionary<string, string?> { ["Retries:Attempts"] = "1" });
    }

    // Kiracı başına ayrı devre (anahtarlı boru hatları) + fırlatmayan çalıştırma: A'nın çöküşü B'yi etkilemez.
    [Fact]
    public async Task Tenants_PerTenantCircuit_IsolatesFailures()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("tenant-acme", [new FaultStep { Status = 500, Times = 10 }]);

        Assert.Equal(HttpStatusCode.BadGateway, (await shop.Client.GetAsync("/tenants/acme/ping")).StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, (await shop.Client.GetAsync("/tenants/acme/ping")).StatusCode);
        using var open = await shop.Client.GetAsync("/tenants/acme/ping");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, open.StatusCode);
        Assert.Equal("BrokenCircuitException", (await ShopFixture.JsonOf(open)).GetProperty("error").GetString());

        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/tenants/globex/ping")).StatusCode);
    }

    // Gelen istek boru hattı: uç nokta 300 ms'de kesilir (504); aynı anda tek rapor (ikincisi 429).
    [Fact]
    public async Task Reports_InboundPipeline_TimeoutAndConcurrency()
    {
        using (var timedOut = await shop.Client.GetAsync("/reports/heavy?ms=2000"))
        {
            Assert.Equal(HttpStatusCode.GatewayTimeout, timedOut.StatusCode);
        }

        var both = await Task.WhenAll(shop.Client.GetAsync("/reports/heavy?ms=250"), shop.Client.GetAsync("/reports/heavy?ms=250"));
        Assert.Contains(both, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Contains(both, r => r.StatusCode == HttpStatusCode.TooManyRequests);
    }

    // Gelen istek kotası istemci başına ve Redis ile TÜM podlarda ortak; kota başlıkları ve 429 + Retry-After.
    [Fact]
    public async Task Inbound_RateLimitPerClient_SharedAcrossPods()
    {
        var pod2 = await shop.SecondPodAsync();
        HttpRequestMessage Request(string client)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/limited");
            request.Headers.Add("X-ClientId", client);
            return request;
        }

        using var first = await shop.Client.SendAsync(Request("mobil"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("2", first.Headers.GetValues("RateLimit-Remaining").Single());
        Assert.Equal(HttpStatusCode.OK, (await pod2.SendAsync(Request("mobil"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await shop.Client.SendAsync(Request("mobil"))).StatusCode);

        using var rejected = await pod2.SendAsync(Request("mobil"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);

        Assert.Equal(HttpStatusCode.OK, (await pod2.SendAsync(Request("web"))).StatusCode); // başka istemcinin kotası ayrı
    }

    // Aspire ServiceDefaults: hiçbir şey eklenmemiş istemci de standart işleyiciyle korunur.
    [Fact]
    public async Task AspireServiceDefaults_PlainHttpClientIsResilient()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("status", [new FaultStep { Status = 503, Times = 2 }]);

        using var response = await shop.Client.GetAsync("/upstream-status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, shop.Eu.Faults.Calls("status").Count);
    }
}
