using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Shop.Api;
using Shop.Backends;

namespace Shop.Tests;

/// <summary>Sipariş akışı: gRPC stok → hedging fiyat → standart işleyiciyle ödeme → SQL. Uçtan uca, gerçek ağ ve gerçek SQL Server.</summary>
public sealed class OrdersTests(ShopFixture shop) : IClassFixture<ShopFixture>
{
    private Task<HttpResponseMessage> PlaceAsync(string sku, string? idempotencyKey = null, bool noKey = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, noKey ? "/orders?noKey=true" : "/orders")
        {
            Content = JsonContent.Create(new OrderRequest(sku, 2, "ayse"))
        };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return shop.Client.SendAsync(request);
    }

    [Fact]
    public async Task Order_HappyPath_ReservesPricesChargesAndWritesToSql()
    {
        shop.ResetFaults();
        var sku = ShopFixture.NewSku("ok");

        using var response = await PlaceAsync(sku);
        var body = await ShopFixture.JsonOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("eu", body.GetProperty("reservedBy").GetString());
        Assert.Equal("eu", body.GetProperty("pricedBy").GetString());
        Assert.Equal(1, body.GetProperty("dbAttempts").GetInt32());
        Assert.Equal(1, (await ShopFixture.JsonOf(await shop.Client.GetAsync($"/orders/count/{sku}"))).GetProperty("count").GetInt32());
    }

    // Standart işleyici: 503 iki kez → yeniden denenir; her denemede AYNI Idempotency-Key (çift çekim olmaz).
    [Fact]
    public async Task Payment_TransientFailures_RetriedWithSameIdempotencyKey()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("payment", [new FaultStep { Status = 503, Times = 2 }]);

        using var response = await PlaceAsync(ShopFixture.NewSku("pay"), idempotencyKey: "siparis-42");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var calls = shop.Eu.Faults.Calls("payment");
        Assert.Equal(3, calls.Count);
        Assert.All(calls, c => Assert.Equal("siparis-42", c.IdempotencyKey));
        Assert.All(calls, c => Assert.Contains("ayse", c.Body)); // gövde her denemede eksiksiz yeniden gönderildi
    }

    // Sunucunun Retry-After isteğine uyulur.
    [Fact]
    public async Task Payment_RetryAfterHeader_IsHonored()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("payment", [new FaultStep { Status = 429, RetryAfter = "1" }]);

        var watch = Stopwatch.StartNew();
        using var response = await PlaceAsync(ShopFixture.NewSku("ra"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(900), $"Retry-After beklenmedi: {watch.Elapsed}");
        Assert.Equal(2, shop.Eu.Faults.Calls("payment").Count);
    }

    // Idempotency-Key'siz POST asla yeniden gönderilmez: ilk denemenin gerçek hatası döner.
    [Fact]
    public async Task Payment_WithoutIdempotencyKey_IsNeverResent()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("payment", [new FaultStep { Status = 503, Times = 5 }]);

        using var response = await PlaceAsync(ShopFixture.NewSku("nokey"), noKey: true);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Single(shop.Eu.Faults.Calls("payment"));
    }

    // Deneme zaman aşımı: takılan deneme 1 sn'de kesilir, yeniden deneme başarılı olur.
    [Fact]
    public async Task Payment_HangingAttempt_CutByAttemptTimeout_ThenRetried()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("payment", [new FaultStep { DelayMs = 5000 }]);

        var watch = Stopwatch.StartNew();
        using var response = await PlaceAsync(ShopFixture.NewSku("slowpay"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"takılan deneme kesilmedi: {watch.Elapsed}");
        Assert.Equal(2, shop.Eu.Faults.Calls("payment").Count);
    }

    // Bakım modu: CircuitBreakerManualControl ödeme devresini izole eder; kapatınca akış döner.
    [Fact]
    public async Task Maintenance_IsolatesPayments_ThenRestores()
    {
        shop.ResetFaults();
        Assert.Equal(HttpStatusCode.NoContent, (await shop.Client.PostAsync("/admin/maintenance/true", null)).StatusCode);

        using var isolated = await PlaceAsync(ShopFixture.NewSku("maint"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, isolated.StatusCode);
        Assert.Equal("IsolatedCircuitException", (await ShopFixture.JsonOf(isolated)).GetProperty("error").GetString());
        Assert.Empty(shop.Eu.Faults.Calls("payment")); // istek ödeme servisine hiç gitmedi

        Assert.Equal(HttpStatusCode.NoContent, (await shop.Client.PostAsync("/admin/maintenance/false", null)).StatusCode);
        using var restored = await PlaceAsync(ShopFixture.NewSku("maint"));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }

    // Devre kesici: ödeme servisi çöker → devre açılır (istek gitmez, Retry-After) → açık kalma süresi sonunda deneme isteği → kapanır.
    [Fact]
    public async Task Payment_CircuitOpens_RejectsFast_ThenRecoversThroughHalfOpen()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("payment", [new FaultStep { Status = 500, Times = 100 }]);

        HttpResponseMessage? rejected = null;
        for (var i = 0; i < 6 && rejected is null; i++)
        {
            var response = await PlaceAsync(ShopFixture.NewSku("cb"));
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                rejected = response;
            }
        }

        Assert.NotNull(rejected);
        Assert.Equal("BrokenCircuitException", (await ShopFixture.JsonOf(rejected!)).GetProperty("error").GetString());
        Assert.True(rejected!.Headers.RetryAfter is not null, "açık devre reddi Retry-After taşımalı");
        Assert.Equal("Open", (await ShopFixture.JsonOf(await shop.Client.GetAsync("/admin/payment-circuit"))).GetProperty("state").GetString());

        var callsWhileOpen = shop.Eu.Faults.Calls("payment").Count;
        using (var stillOpen = await PlaceAsync(ShopFixture.NewSku("cb")))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, stillOpen.StatusCode);
        }

        Assert.Equal(callsWhileOpen, shop.Eu.Faults.Calls("payment").Count); // açık devre isteği iletmedi

        shop.Eu.Faults.Reset(); // servis düzeldi
        await Task.Delay(TimeSpan.FromSeconds(2.3));
        using var recovered = await PlaceAsync(ShopFixture.NewSku("cb"));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal("Closed", (await ShopFixture.JsonOf(await shop.Client.GetAsync("/admin/payment-circuit"))).GetProperty("state").GetString());
    }

    // gRPC: stok servisi iki kez Unavailable → gRPC katmanında yeniden denenir, grpc-previous-rpc-attempts gönderilir.
    [Fact]
    public async Task Inventory_Unavailable_RetriedAtGrpcLayer()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("grpc.reserve", [new FaultStep { GrpcStatus = 14, Times = 2 }]);

        using var response = await PlaceAsync(ShopFixture.NewSku("grpc"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([null, "1", "2"], shop.Eu.Faults.Calls("grpc.reserve").Select(c => c.PreviousAttempts));
    }

    // Gerçek SQL Server kilitlenmesi (1205): uygulama kurban seçilir, Aegis HandleSqlTransientErrors ile yeniden dener.
    [Fact]
    public async Task Sql_RealDeadlock_VictimIsRetriedTransparently()
    {
        shop.ResetFaults();
        var sku = ShopFixture.NewSku("dl");
        var store = shop.Api.Services.GetRequiredService<OrderStore>();

        await using var blocker = new SqlConnection(store.ConnectionString);
        await blocker.OpenAsync();
        await Exec(blocker, null, "INSERT Stock VALUES (@s, 1000)", sku);
        await using var tx = (SqlTransaction)await blocker.BeginTransactionAsync();
        await Exec(blocker, tx, "UPDATE Counters SET N = N + 1 WHERE Id = 1", sku); // B'yi kilitle

        var order = PlaceAsync(sku);                                                // uygulama: A'yı kilitler, B'yi bekler
        await Task.Delay(1500);
        await Exec(blocker, tx, "UPDATE Stock SET Qty = Qty WHERE Sku = @s", sku);  // biz A'yı bekleriz → kilitlenme
        await tx.CommitAsync();

        using var response = await order;
        var body = await ShopFixture.JsonOf(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("dbAttempts").GetInt32() >= 2, "kilitlenme kurbanı yeniden denenmedi");
        Assert.Equal(1, await store.CountOrdersAsync(sku, CancellationToken.None)); // tek sipariş, çift yazma yok
    }

    private static async Task Exec(SqlConnection db, SqlTransaction? tx, string sql, string sku)
    {
        await using var cmd = new SqlCommand(sql, db, tx) { CommandTimeout = 60 };
        cmd.Parameters.AddWithValue("@s", sku);
        await cmd.ExecuteNonQueryAsync();
    }
}
