using System.Net.Http.Json;
using Aegis.Resilience.AspNetCore;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.Fallback;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.Telemetry;
using Grpc.Core;
using Grpc.Net.ClientFactory;
using Microsoft.Extensions.Http.Diagnostics;
using Shop.Contracts;

namespace Shop.Api;

/// <summary>Uygulamanın uç noktaları. Her biri gerçek bir iş akışı; dayanıklılık kurulumu <see cref="ShopResilience"/>'tadır.</summary>
public static class ShopEndpoints
{
    public static void Map(WebApplication app)
    {
        MapOrders(app);
        MapCatalog(app);
        MapInventory(app);
        MapPlatform(app);
        MapAdmin(app);
    }

    // Sipariş: stok ayır (gRPC) → fiyat (hedging) → ödeme (standart işleyici, Idempotency-Key) → SQL'e yaz (geçici hata retry).
    private static void MapOrders(WebApplication app)
    {
        app.MapPost("/orders", async (OrderRequest order, HttpRequest request, GrpcClientFactory grpc, IHttpClientFactory http, OrderStore store, CancellationToken ct) =>
        {
            var orderId = Guid.NewGuid().ToString("N");
            var reserved = await grpc.CreateClient<Inventory.InventoryClient>("inventory")
                .ReserveAsync(new ReserveRequest { Sku = order.Sku, Quantity = order.Quantity }, cancellationToken: ct);
            var price = await http.CreateClient("pricing").GetFromJsonAsync($"/pricing/{order.Sku}", ShopJsonContext.Default.Priced, ct);

            using var charge = new HttpRequestMessage(HttpMethod.Post, "/payment/charge") { Content = JsonContent.Create(order) };
            // Ödeme yalnızca idempotency anahtarıyla yeniden denenir (çift çekim olmasın). ?noKey=true: anahtarsız gerçek POST.
            if (request.Query["noKey"] != "true")
            {
                charge.Headers.Add("Idempotency-Key", request.Headers["Idempotency-Key"].FirstOrDefault() ?? orderId);
            }

            using var paid = await http.CreateClient("payment").SendAsync(charge, ct);
            paid.EnsureSuccessStatusCode();
            var payment = await paid.Content.ReadFromJsonAsync(ShopJsonContext.Default.Payment, ct);

            var dbAttempts = await store.SaveAsync(orderId, order, payment!.PaymentId, ct);
            return Results.Ok(new { orderId, payment.PaymentId, price!.Price, reservedBy = reserved.ServedBy, pricedBy = price.ServedBy, dbAttempts });
        });

        app.MapGet("/orders/count/{sku}", async (string sku, OrderStore store, CancellationToken ct) => new { count = await store.CountOrdersAsync(sku, ct) });
    }

    private static void MapCatalog(WebApplication app)
    {
        app.MapGet("/products/{sku}", async (string sku, IAegisPipelineRegistry registry, IHttpClientFactory http, CancellationToken ct) =>
        {
            var context = new AegisContext(ct) { OperationKey = "GetProduct" };
            context.SetProperty(ShopResilience.Sku, sku);
            context.SetRequestMetadata(new RequestMetadata { RequestName = "GetProduct", DependencyName = "ProductService" });
            return await registry.GetPipeline("products").ExecuteAsync(async ValueTask<Product> (ctx) =>
            {
                var product = await http.CreateClient("products").GetFromJsonAsync($"/products/{sku}", ShopJsonContext.Default.Product, ctx.CancellationToken);
                return product! with { Source = "backend" };
            }, context);
        });

        // Tipli boru hattı (IAegisPipeline<FxRate>) + bayat veri bilgisi bağlamdan.
        app.MapGet("/fx/{symbol}", async (string symbol, IAegisPipelineRegistry registry, IHttpClientFactory http, CancellationToken ct) =>
        {
            var context = new AegisContext(ct);
            context.SetProperty(ShopResilience.Symbol, symbol);
            var rate = await registry.GetPipeline("fx").AsTyped<FxRate>().ExecuteAsync(async ValueTask<FxRate> (ctx) =>
                (await http.CreateClient("fx").GetFromJsonAsync($"/fx/{symbol}", ShopJsonContext.Default.FxRate, ctx.CancellationToken))!, context);
            var stale = context.TryGetProperty(StaleFallbackOptions.IsStaleDataKey, out bool isStale) && isStale;
            return new FxReply(rate.Symbol, rate.Rate, rate.Version, stale);
        });

        app.MapGet("/pricing/{sku}", (string sku, IHttpClientFactory http, CancellationToken ct) =>
            http.CreateClient("pricing").GetFromJsonAsync($"/pricing/{sku}", ShopJsonContext.Default.Priced, ct));

        app.MapGet("/catalog/{sku}", (string sku, IHttpClientFactory http, CancellationToken ct) =>
            http.CreateClient("catalog").GetFromJsonAsync($"/catalog/{sku}", ShopJsonContext.Default.CatalogItem, ct));

        app.MapGet("/recommend", async (HttpRequest request, IHttpClientFactory http, CancellationToken ct) =>
        {
            using var outgoing = new HttpRequestMessage(HttpMethod.Get, "/recommend");
            if (request.Headers.TryGetValue("X-User-Id", out var user))
            {
                outgoing.Headers.Add("X-User-Id", user.ToString());
            }

            using var response = await http.CreateClient("recommend").SendAsync(outgoing, ct);
            return await response.Content.ReadFromJsonAsync(ShopJsonContext.Default.Recommendation, ct);
        });

        // Teklif: çekirdek hedging; birincil AB, gecikirse ActionGenerator ile ABD.
        app.MapGet("/quote/{sku}", async (string sku, IAegisPipelineRegistry registry, IHttpClientFactory http, CancellationToken ct) =>
        {
            var context = new AegisContext(ct);
            context.SetProperty(ShopResilience.Sku, sku);
            return await registry.GetPipeline("quote").AsTyped<Priced>().ExecuteAsync(async ValueTask<Priced> (ctx) =>
                (await http.CreateClient("quote-eu").GetFromJsonAsync($"/pricing/{sku}", ShopJsonContext.Default.Priced, ctx.CancellationToken))!, context);
        });

        // Arama: senkron çalıştırma biçimi (eski senkron kod tabanları) + kayan pencere / .NET token bucket kotası.
        app.MapGet("/search", (string q, IAegisPipelineRegistry registry) => registry.GetPipeline("search").Execute(() => new { q, hits = 3 }));
        app.MapGet("/search-bcl", (string q, IAegisPipelineRegistry registry) => registry.GetPipeline("search-bcl").Execute(() => new { q, hits = 3 }));
    }

    private static void MapInventory(WebApplication app)
    {
        app.MapGet("/inventory/reserve/{sku}", async (string sku, GrpcClientFactory grpc, CancellationToken ct) =>
        {
            var reply = await Inventory(grpc).ReserveAsync(new ReserveRequest { Sku = sku, Quantity = 1 }, cancellationToken: ct);
            return new { reply.Sku, reply.Reserved, reply.ServedBy };
        });

        // Sunucu akışı: ilk mesaja kadar yeniden denenir, ilk mesajla commit.
        app.MapGet("/inventory/watch/{sku}", async (string sku, int count, GrpcClientFactory grpc, CancellationToken ct) =>
        {
            using var call = Inventory(grpc).Watch(new WatchRequest { Sku = sku, Count = count }, cancellationToken: ct);
            var levels = new List<int>();
            await foreach (var level in call.ResponseStream.ReadAllAsync(ct))
            {
                levels.Add(level.Level);
            }

            return levels;
        });

        // İstemci akışı: gönderilen mesajlar tamponlanır, yeniden denemede baştan oynatılır.
        app.MapPost("/inventory/bulk", async (OrderRequest[] items, GrpcClientFactory grpc, CancellationToken ct) =>
        {
            using var call = Inventory(grpc).BulkReserve(cancellationToken: ct);
            foreach (var item in items)
            {
                await call.RequestStream.WriteAsync(new ReserveRequest { Sku = item.Sku, Quantity = item.Quantity }, ct);
            }

            await call.RequestStream.CompleteAsync();
            var reply = await call;
            return new { reply.Items, reply.Total, reply.ServedBy };
        });

        // Çift yönlü akış: ilk yanıta kadar yeniden oynatılır.
        app.MapPost("/inventory/sync", async (OrderRequest[] items, GrpcClientFactory grpc, CancellationToken ct) =>
        {
            using var call = Inventory(grpc).Sync(cancellationToken: ct);
            var reading = Task.Run(async () =>
            {
                var replies = new List<string>();
                await foreach (var reply in call.ResponseStream.ReadAllAsync(ct))
                {
                    replies.Add(reply.Sku);
                }

                return replies;
            }, ct);
            foreach (var item in items)
            {
                await call.RequestStream.WriteAsync(new ReserveRequest { Sku = item.Sku, Quantity = item.Quantity }, ct);
            }

            await call.RequestStream.CompleteAsync();
            return await reading;
        });

        // İki sunuculu havuz: art arda hata veren sunucu ayıklanır.
        app.MapGet("/inventory/pool/{sku}", async (string sku, GrpcClientFactory grpc, CancellationToken ct) =>
        {
            var reply = await grpc.CreateClient<Inventory.InventoryClient>("inventory-pool")
                .ReserveAsync(new ReserveRequest { Sku = sku, Quantity = 1 }, cancellationToken: ct);
            return new { reply.ServedBy };
        });
    }

    private static void MapPlatform(WebApplication app)
    {
        app.MapGet("/partners/{tenant}", async (string tenant, IHttpClientFactory http, CancellationToken ct) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/partners/{tenant}");
            request.Headers.Add("X-Tenant", tenant);
            using var response = await http.CreateClient("partners").SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return Results.Ok(new { tenant });
        });

        // Fırlatmayan çalıştırma biçimi: sonuç Outcome olarak döner, istisna maliyeti yok.
        app.MapGet("/tenants/{tenant}/ping", async (string tenant, IAegisPipelineProvider<string> pipelines, IHttpClientFactory http, CancellationToken ct) =>
        {
            var outcome = await pipelines.GetPipeline(tenant).ExecuteOutcomeAsync(async ValueTask<bool> (ctx) =>
            {
                using var response = await http.CreateClient("tenant").GetAsync($"/tenant/{tenant}", ctx.CancellationToken);
                response.EnsureSuccessStatusCode();
                return true;
            }, new AegisContext(ct));

            return outcome.IsSuccess
                ? Results.Ok(new { tenant, ok = true })
                : Results.Json(new { tenant, error = outcome.Exception!.GetType().Name }, statusCode: outcome.Exception is BrokenCircuitException ? 503 : 502);
        });

        app.MapGet("/legacy/{id}", (string id, ILegacyGateway legacy, CancellationToken ct) => legacy.GetAsync(id, ct));
        app.MapGet("/legacy-sync/{id}", (string id, ILegacyGateway legacy) => legacy.Get(id));

        app.MapGet("/adaptive", async (IAegisPipelineRegistry registry, IHttpClientFactory http, CancellationToken ct) =>
        {
            await registry.GetPipeline("adaptive").ExecuteAsync(async ValueTask<bool> (ctx) =>
            {
                using var response = await http.CreateClient("slow").GetAsync("/slow", ctx.CancellationToken);
                return response.IsSuccessStatusCode;
            }, new AegisContext(ct));
            return Results.Ok();
        });
        app.MapGet("/adaptive/limit", (IAegisPipelineRegistry registry) =>
            new { limit = registry.GetPipeline("adaptive").Strategies.OfType<AdaptiveConcurrencyStrategy>().Single().CurrentLimit });

        app.MapGet("/chaos/{sku}", async (string sku, IAegisPipelineRegistry registry, IHttpClientFactory http, CancellationToken ct) =>
            await registry.GetPipeline("chaos").ExecuteAsync(async ValueTask<Product> (ctx) =>
                (await http.CreateClient("products").GetFromJsonAsync($"/products/{sku}", ShopJsonContext.Default.Product, ctx.CancellationToken))!,
                new AegisContext(ct)));

        app.MapGet("/reloadable", async (IAegisPipelineRegistry registry, IHttpClientFactory http, CancellationToken ct) =>
        {
            await registry.GetPipeline("reloadable").ExecuteAsync(async ValueTask<bool> (ctx) =>
            {
                using var response = await http.CreateClient("reload").GetAsync("/reload", ctx.CancellationToken);
                response.EnsureSuccessStatusCode();
                return true;
            }, new AegisContext(ct));
            return Results.Ok();
        });

        // Gelen istek koruması: uç nokta başına boru hattı (aynı anda tek rapor, 300 ms) ve istemci başına kota.
        app.MapGet("/reports/heavy", async (int ms, HttpContext http) =>
        {
            await Task.Delay(ms, http.RequestAborted);
            return Results.Ok(new { ms });
        }).RequireAegisPipeline("reports");
        app.MapGet("/limited", () => Results.Ok(new { ok = true }));

        // Aspire ServiceDefaults'un varsayılan işleyicisiyle giden istek.
        app.MapGet("/upstream-status", async (IHttpClientFactory http, CancellationToken ct) =>
        {
            using var response = await http.CreateClient("status").GetAsync("/status", ct);
            response.EnsureSuccessStatusCode();
            return Results.Ok(new { ok = true });
        });
    }

    private static void MapAdmin(WebApplication app)
    {
        app.MapGet("/admin/payment-circuit", (CircuitBreakerStateProvider payment) => new { state = payment.CircuitState.ToString() });
        app.MapPost("/admin/maintenance/{on:bool}", async (bool on, CircuitBreakerManualControl maintenance, CancellationToken ct) =>
        {
            if (on)
            {
                await maintenance.IsolateAsync(ct);
            }
            else
            {
                await maintenance.CloseAsync(ct);
            }

            return Results.NoContent();
        });
    }

    private static Inventory.InventoryClient Inventory(GrpcClientFactory grpc) => grpc.CreateClient<Inventory.InventoryClient>("inventory");
}
