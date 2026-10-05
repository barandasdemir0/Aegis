using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Telemetry;
using Aegis.Resilience.Extensions.Dashboard;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.DependencyInjection.Aop;
using Aegis.Resilience.Extensions.HealthChecks;
using Aegis.Resilience.Extensions.Http;
using Aegis.Resilience.Extensions.Telemetry;
using Aegis.Resilience.Grpc;
using Aegis.Resilience.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Diagnostics;
using Shop.Contracts;

namespace Shop.Api;

/// <summary>Katalog ağ geçidi (singleton proxy) ve denetim kaydı (transient proxy, ValueTask dönüşü).</summary>
public interface ICatalogGateway
{
    [AegisPolicy("legacy")]
    Task<string> GetAsync(string name, CancellationToken cancellationToken);
}

public interface IAuditGateway
{
    [AegisPolicy("legacy")]
    ValueTask<string> SendAsync(string name, CancellationToken cancellationToken);
}

public sealed class CatalogGateway(IHttpClientFactory clients) : ICatalogGateway
{
    public async Task<string> GetAsync(string name, CancellationToken cancellationToken)
    {
        using var response = await clients.CreateClient("products").GetAsync($"/svc/{name}", cancellationToken);
        response.EnsureSuccessStatusCode();
        return name;
    }
}

public sealed class AuditGateway(IHttpClientFactory clients) : IAuditGateway
{
    public async ValueTask<string> SendAsync(string name, CancellationToken cancellationToken)
    {
        using var response = await clients.CreateClient("products").PostAsync($"/svc/{name}", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return name;
    }
}

/// <summary>Ekibin işleyicisi: Aegis bağlamının korelasyon kimliğini her denemeye başlık olarak yazar (dağıtık izleme).</summary>
public sealed class CorrelationHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove("X-Correlation-Id");
        request.Headers.Add("X-Correlation-Id", request.GetOrCreateAegisContext().CorrelationId);
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Özel telemetri dinleyicisi (ör. denetim kaydına yazan şirket içi bileşen).</summary>
public sealed class EventRecorder : AegisTelemetryListener
{
    public ConcurrentQueue<string> Events { get; } = new();

    public override void Write(in AegisTelemetryEvent telemetryEvent) => Events.Enqueue($"{telemetryEvent.PipelineName}:{telemetryEvent.EventName}");
}

/// <summary>
/// Mutabakat politikası: saat dışarıdan verilir (WithTimeProvider). Uygulama sistem saatini, birim testi sahte saati kullanır;
/// 1 saatlik açılma süresi testte beklenmeden ilerletilir.
/// </summary>
public static class SettlementPolicy
{
    public static IAegisPipelineBuilder Configure(IAegisPipelineBuilder builder, TimeProvider clock) => builder
        .WithTimeProvider(clock)
        .AddCircuitBreaker(o => { o.ConsecutiveFailureThreshold = 2; o.BreakDuration = TimeSpan.FromHours(1); });
}

/// <summary>Genel API'nin geri kalanı: proxy yaşam süreleri, özel gRPC boru hattı, kaos hatası, sabit pencere, birleştirme,
/// HTTP isteğiyle bağlam, geçici HTTP koşulu, durum JSON'u, ek sağlık kontrolü, telemetri dinleyicisi, sahte saat.</summary>
public static class CoverageFeatures
{
    public static void AddCoverageFeatures(this IServiceCollection services, ShopSettings settings)
    {
        services.AddAegisProxiedSingleton<ICatalogGateway, CatalogGateway>();
        services.AddAegisProxiedTransient<IAuditGateway, AuditGateway>();

        // Özel gRPC boru hattı: stok ayırma yanıtı gecikirse 150 ms sonra paralel yedek istek (gRPC hedging).
        services.AddGrpcClient<Inventory.InventoryClient>("inventory-hedged", o => o.Address = settings.Eu.Grpc)
            .AddAegisGrpcResilience(p => p.AddHedging(o => { o.MaxHedgedAttempts = 1; o.HedgingDelay = TimeSpan.FromMilliseconds(150); }));

        // Kupon: .NET sabit pencere sınırlayıcısı (System.Threading.RateLimiting köprüsü).
        services.AddAegisPipeline("coupons", p => p.AddFixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 2,
            Window = TimeSpan.FromSeconds(30),
            QueueLimit = 0
        }));

        // Ödeme sayfası: kendi toplam süresi + ortak "priority-high" boru hattı (kompozisyon; ortak olan dispose edilmez).
        services.AddAegisPipeline("checkout", (p, sp) => p
            .AddTimeout(TimeSpan.FromSeconds(3))
            .AddPipeline(sp.GetRequiredService<IAegisPipelineRegistry>().GetPipeline("priority-high")));

        // Kupon doğrulama: Microsoft/Polly ile aynı geçici HTTP hata tanımı (5xx, 408, 429, bağlantı hatası).
        services.AddHttpClient("coupons-http", c => c.BaseAddress = settings.Eu.Http)
            .RemoveAllAegisHandlers()
            .AddAegisResilienceHandler(p => p.AddRetry(o =>
            {
                o.MaxRetryAttempts = 2;
                o.Delay = TimeSpan.FromMilliseconds(10);
                o.ShouldHandleOutcome = new AegisPredicateBuilder().HandleTransientHttpErrors();
            }));

        // İzlenen istemci: çağıranın bağlamı istekle taşınır, tüm denemeler aynı korelasyon kimliğini gönderir.
        services.AddTransient<CorrelationHandler>();
        services.AddHttpClient("tracked", c => c.BaseAddress = settings.Eu.Http)
            .RemoveAllAegisHandlers()
            .AddStandardAegisHandler(o => { o.Retry.MaxRetryAttempts = 3; o.Retry.Delay = TimeSpan.FromMilliseconds(10); })
            .AddHttpMessageHandler<CorrelationHandler>();

        // Hazır olma kontrolü: açık devre burada Unhealthy (trafik alma), /health'te Degraded (yeniden başlatma).
        services.AddHealthChecks().AddAegisCheck("aegis_ready", tags: ["ready"], configureOptions: o => o.OpenCircuitStatus = HealthStatus.Unhealthy);

        services.AddSingleton<EventRecorder>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddAegisPipeline("settlement", (p, sp) => SettlementPolicy.Configure(p, sp.GetRequiredService<TimeProvider>()));
    }

    public static void MapCoverageFeatures(this WebApplication app)
    {
        app.MapAegisStatus("/ops/status");

        app.MapGet("/gateways/catalog/{name}", (string name, ICatalogGateway gateway, CancellationToken ct) => gateway.GetAsync(name, ct));
        app.MapPost("/gateways/audit/{name}", async (string name, IAuditGateway gateway, CancellationToken ct) => await gateway.SendAsync(name, ct));

        app.MapGet("/inventory/hedged/{sku}", async (string sku, Grpc.Net.ClientFactory.GrpcClientFactory grpc, CancellationToken ct) =>
        {
            var reply = await grpc.CreateClient<Inventory.InventoryClient>("inventory-hedged")
                .ReserveAsync(new ReserveRequest { Sku = sku, Quantity = 1 }, cancellationToken: ct);
            return new { reply.ServedBy };
        });

        app.MapPost("/coupons", (IAegisPipelineRegistry registry) => registry.GetPipeline("coupons").Execute(() => new { issued = true }));

        app.MapGet("/checkout", async (IAegisPipelineRegistry registry, IHttpClientFactory http, CancellationToken ct) =>
        {
            await registry.GetPipeline("checkout").ExecuteAsync(async ValueTask<bool> (ctx) =>
            {
                using var response = await http.CreateClient("products").GetAsync("/svc/checkout", ctx.CancellationToken);
                response.EnsureSuccessStatusCode();
                return true;
            }, new AegisContext(ct));
            return Results.Ok();
        });

        app.MapGet("/coupons/validate/{code}", async (string code, IHttpClientFactory http, CancellationToken ct) =>
        {
            using var response = await http.CreateClient("coupons-http").GetAsync($"/svc/coupon-{code}", ct);
            response.EnsureSuccessStatusCode();
            return Results.Ok();
        });

        app.MapGet("/tracked", async (IHttpClientFactory http, CancellationToken ct) =>
        {
            var context = new AegisContext(ct) { OperationKey = "Tracked" };
            context.SetRequestMetadata(new RequestMetadata { RequestName = "Tracked", DependencyName = "TrackedService" });
            using var request = new HttpRequestMessage(HttpMethod.Get, "/svc/tracked");
            request.SetAegisContext(context);
            using var response = await http.CreateClient("tracked").SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return Results.Ok(new
            {
                correlationId = context.CorrelationId,
                sameContext = ReferenceEquals(request.GetAegisContext(), context), // çağıranın bağlamı istekte kalır
                requestName = context.GetRequestMetadata()?.RequestName
            });
        });

        app.MapGet("/settlement", async (IAegisPipelineRegistry registry, IHttpClientFactory http, CancellationToken ct) =>
        {
            await registry.GetPipeline("settlement").ExecuteAsync(async ValueTask<bool> (ctx) =>
            {
                using var response = await http.CreateClient("products").GetAsync("/svc/settlement", ctx.CancellationToken);
                response.EnsureSuccessStatusCode();
                return true;
            }, new AegisContext(ct));
            return Results.Ok();
        });

        app.MapGet("/telemetry/recorded", (EventRecorder recorder) => recorder.Events.ToArray());
    }
}
