using System.Diagnostics;
using System.Net;
using Shop.Backends;

namespace Shop.Tests;

/// <summary>Okuma yolu: önbellek (Redis), istek birleştirme, yedek değer, bayat veri, hedging, kanarya, kota.</summary>
public sealed class CatalogTests(ShopFixture shop) : IClassFixture<ShopFixture>
{
    // Aynı anda gelen 20 istek tek arka uç çağrısına iner (cache stampede yok).
    [Fact]
    public async Task Products_ConcurrentRequests_CollapsedIntoSingleBackendCall()
    {
        shop.ResetFaults();
        var sku = ShopFixture.NewSku("hot");
        shop.Eu.Faults.SetPlan("products", [new FaultStep { DelayMs = 300 }]);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => shop.Client.GetAsync($"/products/{sku}")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Single(shop.Eu.Faults.Calls("products"));
    }

    // Redis önbelleği: ikinci okuma arka uca gitmez; aynı kümedeki BAŞKA bir pod da aynı önbelleği görür.
    [Fact]
    public async Task Products_CachedInRedis_SharedAcrossPods()
    {
        shop.ResetFaults();
        var sku = ShopFixture.NewSku("cache");
        var pod2 = await shop.SecondPodAsync();

        Assert.Equal("backend", (await ShopFixture.JsonOf(await shop.Client.GetAsync($"/products/{sku}"))).GetProperty("source").GetString());
        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync($"/products/{sku}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pod2.GetAsync($"/products/{sku}")).StatusCode);

        Assert.Single(shop.Eu.Faults.Calls("products"));
    }

    // Arka uç çöktü: yeniden denemeler tükenir, en dıştaki Fallback yedek değeri döner (kullanıcı hata görmez).
    [Fact]
    public async Task Products_BackendDown_FallbackValueAfterRetries()
    {
        shop.ResetFaults();
        var sku = ShopFixture.NewSku("down");
        shop.Eu.Faults.SetPlan("products", [new FaultStep { Status = 500, Times = 3 }]);

        using var response = await shop.Client.GetAsync($"/products/{sku}");
        var body = await ShopFixture.JsonOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("fallback", body.GetProperty("source").GetString());
        Assert.Equal(3, shop.Eu.Faults.Calls("products").Count); // 1 + 2 yeniden deneme
    }

    // Stale-While-Revalidate: taze → arka uca gidilmez; bayat → eski değer anında döner + arkada tek yenileme;
    // arka uç çökse de bayat değer sunulur.
    [Fact]
    public async Task Fx_StaleWhileRevalidate_ServesStaleInstantly_RefreshesInBackground()
    {
        shop.ResetFaults();
        var symbol = ShopFixture.NewSku("USD");

        var first = await ShopFixture.JsonOf(await shop.Client.GetAsync($"/fx/{symbol}"));
        var fresh = await ShopFixture.JsonOf(await shop.Client.GetAsync($"/fx/{symbol}"));
        Assert.False(first.GetProperty("stale").GetBoolean());
        Assert.Equal(first.GetProperty("version").GetInt32(), fresh.GetProperty("version").GetInt32());
        Assert.Single(shop.Eu.Faults.Calls("fx"));

        await Task.Delay(450); // taze süre (300 ms) doldu
        var stale = await ShopFixture.JsonOf(await shop.Client.GetAsync($"/fx/{symbol}"));
        Assert.True(stale.GetProperty("stale").GetBoolean());
        Assert.Equal(first.GetProperty("version").GetInt32(), stale.GetProperty("version").GetInt32());

        await Task.Delay(100); // arka plan yenilemesi bitti (taze süre 300 ms içinde okunur)
        var refreshed = await ShopFixture.JsonOf(await shop.Client.GetAsync($"/fx/{symbol}"));
        Assert.True(refreshed.GetProperty("version").GetInt32() > first.GetProperty("version").GetInt32());
        Assert.False(refreshed.GetProperty("stale").GetBoolean());

        shop.Eu.Faults.SetPlan("fx", [new FaultStep { Status = 500, Times = 50 }]);
        await Task.Delay(450);
        using var whileDown = await shop.Client.GetAsync($"/fx/{symbol}");
        Assert.Equal(HttpStatusCode.OK, whileDown.StatusCode);
        Assert.True((await ShopFixture.JsonOf(whileDown)).GetProperty("stale").GetBoolean());
    }

    // Standart hedging: AB yavaş → 150 ms sonra ABD'ye paralel istek, hızlı olan kazanır.
    [Fact]
    public async Task Pricing_SlowPrimary_HedgedToSecondRegion()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("pricing", [new FaultStep { DelayMs = 3000 }]);

        var watch = Stopwatch.StartNew();
        var body = await ShopFixture.JsonOf(await shop.Client.GetAsync($"/pricing/{ShopFixture.NewSku("p")}"));

        Assert.Equal("us", body.GetProperty("servedBy").GetString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"hedging beklemedi: {watch.Elapsed}");
    }

    // Standart hedging: AB 503 → sıradaki deneme hemen ABD.
    [Fact]
    public async Task Pricing_PrimaryFailing_NextGroupServes()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("pricing", [new FaultStep { Status = 503 }]);

        var body = await ShopFixture.JsonOf(await shop.Client.GetAsync($"/pricing/{ShopFixture.NewSku("p")}"));

        Assert.Equal("us", body.GetProperty("servedBy").GetString());
    }

    // Çoklu uç nokta hedging (yalnızca Aegis): yavaş veri merkezi beklenmez.
    [Fact]
    public async Task Catalog_MultiEndpointHedging_FastDatacenterWins()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("catalog", [new FaultStep { DelayMs = 3000 }]);

        var watch = Stopwatch.StartNew();
        var body = await ShopFixture.JsonOf(await shop.Client.GetAsync($"/catalog/{ShopFixture.NewSku("c")}"));

        Assert.Equal("us", body.GetProperty("servedBy").GetString());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"hedging beklemedi: {watch.Elapsed}");
    }

    // Çekirdek hedging + ActionGenerator: yedek deneme farklı bir işlem (başka bölge) çalıştırır.
    [Fact]
    public async Task Quote_CoreHedging_ActionGeneratorUsesOtherRegion()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("pricing", [new FaultStep { DelayMs = 3000 }]);

        var body = await ShopFixture.JsonOf(await shop.Client.GetAsync($"/quote/{ShopFixture.NewSku("q")}"));

        Assert.Equal("us", body.GetProperty("servedBy").GetString());
    }

    // Ağırlıklı kanarya: trafiğin yaklaşık %10'u kanaryaya; aynı kullanıcı hep aynı sürümü görür.
    [Fact]
    public async Task Recommend_WeightedCanary_RoughlyTenPercent_AndStickyPerUser()
    {
        shop.ResetFaults();
        var variants = new List<string>();
        for (var i = 0; i < 300; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/recommend");
            request.Headers.Add("X-User-Id", $"kullanici-{i}");
            variants.Add((await ShopFixture.JsonOf(await shop.Client.SendAsync(request))).GetProperty("variant").GetString()!);
        }

        var canaryShare = variants.Count(v => v == "canary") / 300.0;
        Assert.InRange(canaryShare, 0.04, 0.18);

        var sticky = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/recommend");
            request.Headers.Add("X-User-Id", "sabit-kullanici");
            sticky.Add((await ShopFixture.JsonOf(await shop.Client.SendAsync(request))).GetProperty("variant").GetString()!);
        }

        Assert.Single(sticky.Distinct());
    }

    // Kotalar: Aegis kayan pencere (3) ve .NET System.Threading.RateLimiting token bucket köprüsü (2); red 429 + Retry-After.
    [Theory]
    [InlineData("/search", 3)]
    [InlineData("/search-bcl", 2)]
    public async Task Search_RateLimited_429WithRetryAfter(string path, int limit)
    {
        for (var i = 0; i < limit; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync($"{path}?q=telefon")).StatusCode);
        }

        using var rejected = await shop.Client.GetAsync($"{path}?q=telefon");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
    }
}
