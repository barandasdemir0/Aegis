using System.Net;
using Shop.Backends;

namespace Shop.Tests;

/// <summary>İş ortağı API'si: kiracı başına kota ve iki pod arasında Redis üzerinden ortak devre kesici.</summary>
public sealed class PartnersTests(ShopFixture shop) : IClassFixture<ShopFixture>
{
    // Kiracı başına kota: ücretsiz kiracı dakikada 2, vip 20; bir kiracının kotası diğerini etkilemez.
    [Fact]
    public async Task PartitionedQuota_PerTenant()
    {
        shop.ResetFaults();
        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/partners/free")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/partners/free")).StatusCode);
        using var rejected = await shop.Client.GetAsync("/partners/free");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);

        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/partners/vip")).StatusCode);
    }

    // Dağıtık devre kesici: pod 1'de açılan devre pod 2'de de açık (Redis); pod 2 isteği hiç iletmez. Süre dolunca tek deneme isteğiyle kapanır.
    [Fact]
    public async Task DistributedCircuit_OpenedOnOnePod_RejectsOnTheOther()
    {
        shop.ResetFaults();
        var pod2 = await shop.SecondPodAsync();
        shop.Eu.Faults.SetPlan("partners", [new FaultStep { Status = 500, Times = 100 }]);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.BadGateway, (await shop.Client.GetAsync("/partners/vip")).StatusCode);
        }

        var callsBefore = shop.Eu.Faults.Calls("partners").Count;
        using var rejected = await pod2.GetAsync("/partners/vip");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.Equal("BrokenCircuitException", (await ShopFixture.JsonOf(rejected)).GetProperty("error").GetString());
        Assert.Equal(callsBefore, shop.Eu.Faults.Calls("partners").Count);

        shop.Eu.Faults.Reset();
        await Task.Delay(TimeSpan.FromSeconds(2.3));
        Assert.Equal(HttpStatusCode.OK, (await pod2.GetAsync("/partners/vip")).StatusCode); // deneme isteği başarılı → kapanır
        Assert.Equal(HttpStatusCode.OK, (await shop.Client.GetAsync("/partners/vip")).StatusCode);
    }
}

/// <summary>Tüm podlarda ortak kota (Redis, sabit pencere): iki pod birlikte 30 sn'de en fazla 10 istek geçirir.</summary>
public sealed class PartnersGlobalQuotaTests(ShopFixture shop) : IClassFixture<ShopFixture>
{
    [Fact]
    public async Task DistributedQuota_SharedAcrossPods()
    {
        shop.ResetFaults();
        var pod2 = await shop.SecondPodAsync();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
        {
            using var response = await (i % 2 == 0 ? shop.Client : pod2).GetAsync("/partners/vip");
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(10, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
        Assert.Equal(10, shop.Eu.Faults.Calls("partners").Count); // reddedilen istekler arka uca gitmedi
    }
}
