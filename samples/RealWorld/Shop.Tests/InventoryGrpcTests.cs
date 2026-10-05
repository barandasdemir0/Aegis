using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Shop.Api;
using Shop.Backends;
using Shop.Contracts;

namespace Shop.Tests;

/// <summary>gRPC: unary, sunucu akışı, istemci akışı, çift yönlü akış; pushback, commit kuralı, uç nokta ayıklama, sunucu koruması.</summary>
public sealed class InventoryGrpcTests(ShopFixture shop) : IClassFixture<ShopFixture>
{
    [Fact]
    public async Task Reserve_Unavailable_RetriedWithPreviousAttemptsHeader()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("grpc.reserve", [new FaultStep { GrpcStatus = 14, Times = 2 }]);

        using var response = await shop.Client.GetAsync("/inventory/reserve/s1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([null, "1", "2"], shop.Eu.Faults.Calls("grpc.reserve").Select(c => c.PreviousAttempts));
    }

    // Sunucu "400 ms sonra dene" der (grpc-retry-pushback-ms): istemci tam o kadar bekler.
    [Fact]
    public async Task Reserve_ResourceExhaustedWithPushback_WaitsThenRetries()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("grpc.reserve", [new FaultStep { GrpcStatus = 8, PushbackMs = 400 }]);

        var watch = Stopwatch.StartNew();
        using var response = await shop.Client.GetAsync("/inventory/reserve/s2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(380), $"pushback beklenmedi: {watch.Elapsed}");
    }

    // İstemci hatası (InvalidArgument) yeniden denenmez.
    [Fact]
    public async Task Reserve_InvalidArgument_NotRetried()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("grpc.reserve", [new FaultStep { GrpcStatus = 3, Times = 5 }]);

        using var response = await shop.Client.GetAsync("/inventory/reserve/s3");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Single(shop.Eu.Faults.Calls("grpc.reserve"));
    }

    // Takılan deneme, deneme zaman aşımıyla (2 sn) kesilir ve yeniden denenir.
    [Fact]
    public async Task Reserve_HangingAttempt_CutAndRetried()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("grpc.reserve", [new FaultStep { DelayMs = 10_000 }]);

        var watch = Stopwatch.StartNew();
        using var response = await shop.Client.GetAsync("/inventory/reserve/s4");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"takılan deneme kesilmedi: {watch.Elapsed}");
        Assert.Equal(2, shop.Eu.Faults.Calls("grpc.reserve").Count);
    }

    // Sunucu akışı: ilk mesajdan ÖNCE hata → yeniden denenir.
    [Fact]
    public async Task Watch_FailsBeforeFirstMessage_Retried()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("grpc.watch", [new FaultStep { GrpcStatus = 14, AfterMessages = 0 }]);

        var levels = await shop.Client.GetFromJsonAsync<int[]>("/inventory/watch/s5?count=3");

        Assert.Equal([100, 99, 98], levels!);
        Assert.Equal(2, shop.Eu.Faults.Calls("grpc.watch").Count);
    }

    // Commit kuralı (gRPC A6): ilk mesaj alındıktan SONRA hata → yeniden denenmez (mesajlar tekrar teslim edilmez).
    [Fact]
    public async Task Watch_FailsAfterFirstMessage_NotRetried()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("grpc.watch", [new FaultStep { GrpcStatus = 14, AfterMessages = 1 }]);

        using var response = await shop.Client.GetAsync("/inventory/watch/s6?count=3");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Single(shop.Eu.Faults.Calls("grpc.watch"));
    }

    // İstemci akışı: gönderilen mesajlar tamponlanır, yeniden denemede baştan eksiksiz oynatılır.
    [Fact]
    public async Task BulkReserve_ClientStream_ReplayedOnRetry()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("grpc.bulk", [new FaultStep { GrpcStatus = 14 }]);
        OrderRequest[] items = [new("a", 1, "x"), new("b", 2, "x"), new("c", 3, "x")];

        using var response = await shop.Client.PostAsJsonAsync("/inventory/bulk", items);
        var body = await ShopFixture.JsonOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, body.GetProperty("items").GetInt32());
        Assert.Equal(6, body.GetProperty("total").GetInt32());
        Assert.Equal(["3:6", "3:6"], shop.Eu.Faults.Calls("grpc.bulk").Select(c => c.Body)); // iki denemede de tüm akış
    }

    // Çift yönlü akış: ilk yanıttan önceki hata yeniden denenir, istek akışı baştan oynatılır.
    [Fact]
    public async Task Sync_DuplexStream_ReplayedBeforeFirstReply()
    {
        shop.ResetFaults();
        shop.Eu.Faults.SetPlan("grpc.sync", [new FaultStep { GrpcStatus = 14 }]);
        OrderRequest[] items = [new("a", 1, "x"), new("b", 2, "x")];

        using var response = await shop.Client.PostAsJsonAsync("/inventory/sync", items);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["a", "b"], (await response.Content.ReadFromJsonAsync<string[]>())!);
        Assert.Equal(2, shop.Eu.Faults.Calls("grpc.sync").Count);
    }

    // Uç nokta ayıklama: art arda Unavailable veren ABD sunucusu havuzdan çıkarılır, trafik AB'ye akar.
    [Fact]
    public async Task Pool_OutlierDetection_EjectsFailingServer()
    {
        shop.ResetFaults();
        shop.Us.Faults.SetPlan("grpc.reserve", [new FaultStep { GrpcStatus = 14, Times = 1000 }]);

        var outcomes = new List<string>();
        for (var i = 0; i < 30; i++)
        {
            using var response = await shop.Client.GetAsync("/inventory/pool/s7");
            outcomes.Add(response.IsSuccessStatusCode ? (await ShopFixture.JsonOf(response)).GetProperty("servedBy").GetString()! : "hata");
        }

        Assert.True(outcomes.Count(o => o == "hata") <= 4, $"çok fazla hata: {string.Join(',', outcomes)}");
        Assert.All(outcomes.Skip(15), o => Assert.Equal("eu", o)); // ayıklandıktan sonra hep sağlıklı sunucu
    }

    // Sunucu koruması (Aegis.Resilience.Grpc.AspNetCore): eşzamanlılık sınırı dolunca ResourceExhausted. Bekleme süresi bilinmediği için
    // pushback yoktur: istemciler aşırı yüklü sunucuyu hemen yeniden denemez (retry fırtınası olmaz).
    [Fact]
    public async Task Server_ConcurrencyLimit_RejectsWithResourceExhausted_WithoutPushback()
    {
        await using var server = await BackendApp.StartAsync("limited", grpcServerConcurrency: 1);
        server.Faults.SetPlan("grpc.reserve", [new FaultStep { DelayMs = 800 }]);
        using var channel = GrpcChannel.ForAddress(server.GrpcAddress);
        var client = new Inventory.InventoryClient(channel);

        var slow = client.ReserveAsync(new ReserveRequest { Sku = "x", Quantity = 1 }).ResponseAsync;
        await Task.Delay(200);
        var rejected = await Assert.ThrowsAsync<RpcException>(() => client.ReserveAsync(new ReserveRequest { Sku = "y", Quantity = 1 }).ResponseAsync);

        Assert.Equal(StatusCode.ResourceExhausted, rejected.StatusCode);
        Assert.Null(rejected.Trailers.GetValue("grpc-retry-pushback-ms"));
        Assert.Equal("limited", (await slow).ServedBy);
    }
}
