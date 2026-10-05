using System.Collections.Concurrent;
using System.Net.Http.Json;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.Http;

namespace Shop.Api;

public sealed record FeeSettings
{
    public int TimeoutMs { get; init; } = 1000;
}

public sealed record CarrierKey(string Carrier, string Version);

/// <summary>Gözlem: devre açılma süreleri (BreakDurationGenerator) ve kaos yan etkisi sayacı.</summary>
public sealed class ResilienceProbe
{
    public ConcurrentQueue<TimeSpan> WarehouseBreaks { get; } = new();

    private int _behaviors;

    public int ChaosBehaviors => Volatile.Read(ref _behaviors);

    public void RecordBehavior() => Interlocked.Increment(ref _behaviors);
}

/// <summary>Bellek içi günlük toplayıcı (gerçekte Seq/ELK); testler Aegis'in otomatik günlüğünü doğrular.</summary>
public sealed class LogSink : ILoggerProvider
{
    public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Sink(LogSink owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Enqueue((category, logLevel, formatter(state, exception)));
    }
}

/// <summary>
/// İleri özellikler: kargo (host başına devre, son yanıt, DELETE'i yeniden denememe, senkron Send), öncelik/host'a göre işleyici,
/// tek başına gövde tekrar oynatma, 5'i 1 arada zincir, bellek içi önbellek, kuyruklu eşzamanlılık, çekirdek token bucket, devre
/// kesici seçenekleri (gölge, sayı penceresi, yarı açıkta art arda başarı, yavaş çağrı, açılma süresi üreticisi), retry bütçesi,
/// ayrık kaos stratejileri, seçeneklerle yeniden yükleme, anahtarlı/tipli boru hattı, örnek adı, otomatik günlük.
/// </summary>
public static class AdvancedFeatures
{
    public static readonly AegisPropertyKey<string> ChaosMode = new("chaos-mode");

    public static void AddAdvancedFeatures(this IServiceCollection services, ShopSettings settings, IConfiguration configuration)
    {
        services.AddSingleton<ResilienceProbe>();
        services.AddSingleton<LogSink>();
        services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<LogSink>());
        services.ConfigureAegisTelemetry(t =>
        {
            t.EnableLogging = true;
            t.ResultFormatter = (_, result) => result is Payment ? "***" : result; // ödeme kimliği günlüğe yazılmaz
        });
        services.Configure<FeeSettings>(configuration.GetSection("Fees"));

        AddHttp(services, settings);
        AddPipelines(services);
    }

    private static void AddHttp(IServiceCollection services, ShopSettings settings)
    {
        // Kargo: tek istemci iki firmaya (AB ve ABD adresleri) gider; firma başına ayrı devre. İptal (DELETE) asla yeniden denenmez.
        services.AddHttpClient("shipping")
            .RemoveAllAegisHandlers()
            .AddStandardAegisHandler(o =>
            {
                o.SelectPipelineByAuthority();
                o.DisableRetryFor(HttpMethod.Delete);
                o.Retry.MaxRetryAttempts = 1;
                o.Retry.Delay = TimeSpan.FromMilliseconds(10);
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(1);
                o.CircuitBreaker.MinimumThroughput = 4;
                o.CircuitBreaker.FailureRatio = 0.5;
                o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(5);
            });

        // Kargo teklifi: denemeler tükenince istisna yerine son yanıt döner (Microsoft davranışı; çağıran durum koduna bakar).
        services.AddHttpClient("shipping-quote", c => c.BaseAddress = settings.Eu.Http)
            .RemoveAllAegisHandlers()
            .AddStandardAegisHandler(o =>
            {
                o.ReturnFinalResponse = true;
                o.Retry.MaxRetryAttempts = 2;
                o.Retry.Delay = TimeSpan.FromMilliseconds(10);
            });

        // Öncelik başlığına göre boru hattı (istek bazlı seçim) ve host adına göre boru hattı.
        services.AddAegisPipeline("priority-high", p => p.AddRetry(o => { o.MaxRetryAttempts = 3; o.Delay = TimeSpan.FromMilliseconds(10); }));
        services.AddAegisPipeline("priority-low", p => p.AddTimeout(TimeSpan.FromSeconds(5)));
        services.AddHttpClient("priority", c => c.BaseAddress = settings.Eu.Http)
            .RemoveAllAegisHandlers()
            .AddAegisDynamicHandler(r => r.Headers.TryGetValues("X-Priority", out var p) && p.First() == "high" ? "priority-high" : "priority-low");

        services.AddAegisPipeline("localhost", p => p.AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.FromMilliseconds(10); }));
        services.AddHttpClient("by-host", c => c.BaseAddress = new Uri($"http://localhost:{settings.Eu.Http.Port}"))
            .RemoveAllAegisHandlers()
            .AddAegisHandlerByHost(fallbackPipelineName: "priority-low");

        // Dosya yükleme: ekibin kendi yeniden deneme işleyicisi + Aegis gövde tekrar oynatma (geri sarılamayan akış güvenle tekrar gider).
        services.AddTransient<SimpleRetryHandler>();
        services.AddHttpClient("upload", c => c.BaseAddress = settings.Eu.Http)
            .RemoveAllAegisHandlers()
            .AddHttpMessageHandler<SimpleRetryHandler>()
            .AddHttpRequestReplayHandler();
    }

    private static void AddPipelines(IServiceCollection services)
    {
        // Stok içe aktarma: tek satırda önerilen 5'li zincir (toplam süre → eşzamanlılık → retry → devre → deneme süresi).
        services.AddAegisPipeline("inventory-sync", p => p.AddStandardResilience(
            totalTimeout: TimeSpan.FromSeconds(4), maxConcurrency: 2, retryAttempts: 2, attemptTimeout: TimeSpan.FromMilliseconds(500)));

        // Öneriler: bellek içi önbellek (kısa ömürlü, pod başına).
        services.AddAegisPipeline("recommend-cache", p => p.AddCache(TimeSpan.FromMilliseconds(800)));

        // Bildirim: aynı anda tek gönderim, en fazla 2 bekleyen (sınırlı kuyruk).
        services.AddAegisPipeline("notifications", p => p.AddConcurrencyLimiter(1, o => { o.QueueLimit = 2; o.QueueTimeout = TimeSpan.FromSeconds(3); }));

        // SMS sağlayıcı kotası: Aegis'in kendi token bucket'ı.
        services.AddAegisPipeline("sms", p => p.AddRateLimiter(2, TimeSpan.FromSeconds(30)));

        // Yeni arama servisi: devre kesici önce gölge kipte (karar izlenir, istek reddedilmez).
        services.AddAegisPipeline("search-shadow", p => p.AddCircuitBreaker(o =>
        {
            o.Mode = CircuitBreakerMode.Shadow;
            o.MinimumThroughput = 3;
            o.FailureRatio = 0.5;
            o.BreakDuration = TimeSpan.FromSeconds(30);
        }));

        // Depo: son 4 çağrıya bakan pencere, yarı açıkta art arda 2 başarı, her başarısız denemede açılma süresi ikiye katlanır.
        services.AddAegisPipeline("warehouse", (p, sp) => p.AddCircuitBreaker(o =>
        {
            var probe = sp.GetRequiredService<ResilienceProbe>();
            o.SamplingCount = 4;
            o.MinimumThroughput = 4;
            o.FailureRatio = 0.5;
            o.HalfOpenSuccessThreshold = 2;
            o.BreakDuration = TimeSpan.FromMilliseconds(400);
            o.BreakDurationGenerator = e => TimeSpan.FromMilliseconds(400 * Math.Pow(2, e.HalfOpenAttempts));
            o.OnOpened = e => { probe.WarehouseBreaks.Enqueue(e.BreakDuration); return default; };
        }));

        // Raporlama servisi: hata vermeden yavaşlayan bağımlılık (yavaş çağrı oranı devreyi açar).
        services.AddAegisPipeline("slow-calls", p => p.AddCircuitBreaker(o =>
        {
            o.SlowCallDurationThreshold = TimeSpan.FromMilliseconds(100);
            o.SlowCallRateThreshold = 0.5;
            o.MinimumThroughput = 4;
            o.BreakDuration = TimeSpan.FromSeconds(5);
        }));

        // Retry bütçesi (gRPC throttling): bağımlılık çökmüşken yeniden denemeler trafiği katlamaz.
        services.AddAegisPipeline("budget", p => p.AddRetry(o =>
        {
            o.MaxRetryAttempts = 3;
            o.Delay = TimeSpan.FromMilliseconds(5);
            o.Budget = new RetryBudget(maxTokens: 4, tokenRatio: 0.1);
        }));

        // Kaos laboratuvarı: ayrık gecikme / sonuç / yan etki stratejileri, çağrı bazında açılır.
        services.AddAegisPipeline("chaos-lab", (p, sp) =>
        {
            var probe = sp.GetRequiredService<ResilienceProbe>();
            p.AddChaosLatency(1.0, TimeSpan.FromMilliseconds(300), o => o.EnabledGenerator = Mode("latency"))
             .AddChaosOutcome(1.0, ctx => new Product("chaos", "kaos ürünü", 0, "chaos"), o => o.EnabledGenerator = Mode("outcome"))
             .AddChaosBehavior(1.0, _ => { probe.RecordBehavior(); return default; }, o => o.EnabledGenerator = Mode("behavior"))
             .AddChaosFault(1.0, () => new HttpRequestException("kaos-hata"), o => o.EnabledGenerator = Mode("fault"));
        });

        // Ücret kuralları: seçenek değişince boru hattı yeniden kurulur (Polly EnableReloads eşdeğeri).
        services.AddAegisPipeline<FeeSettings>("fees", (b, fees, _) => b.AddTimeout(TimeSpan.FromMilliseconds(fees.TimeoutMs)));

        // Anahtarlı boru hattı: kargo firması + API sürümü.
        services.AddAegisPipeline(new CarrierKey("ups", "v2"), (b, key, _) => b.AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.Zero; }));

        // Örnek adı: aynı boru hattının kiracı örneği telemetride ayrışır (pipeline.instance).
        services.AddAegisPipeline("tenant-api", p => p.WithInstanceName("blue").AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.Zero; }));

        // Tipli boru hattı: yalnızca Product dönen işlemleri kabul eder (yanlış tip derleme anında yakalanır).
        services.AddSingleton(sp => new AegisPipelineBuilder("typed-products")
            .AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.Zero; })
            .WithTelemetry(t => t.Listeners.Add(sp.GetRequiredService<EventRecorder>())) // DI dışı kurulumda özel dinleyici
            .Build<Product>());
    }

    private static Func<AegisContext, ValueTask<bool>> Mode(string mode) =>
        ctx => ValueTask.FromResult(ctx.TryGetProperty(ChaosMode, out var m) && m == mode);

    public static void MapAdvancedFeatures(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<ShopSettings>();

        // --- HTTP
        app.MapGet("/shipping/{region}", async (string region, IHttpClientFactory http, CancellationToken ct) =>
        {
            var target = region == "us" ? settings.Us.Http : settings.Eu.Http;
            using var response = await http.CreateClient("shipping").GetAsync(new Uri(target, $"/svc/ship-{region}"), ct);
            response.EnsureSuccessStatusCode();
            return Results.Ok(new { region });
        });
        app.MapDelete("/shipping/{region}", async (string region, IHttpClientFactory http, CancellationToken ct) =>
        {
            using var response = await http.CreateClient("shipping").DeleteAsync(new Uri(settings.Eu.Http, $"/svc/cancel-{region}"), ct);
            response.EnsureSuccessStatusCode();
            return Results.NoContent();
        });
        app.MapGet("/shipping-sync", (IHttpClientFactory http) =>
        {
            using var response = http.CreateClient("shipping").Send(new HttpRequestMessage(HttpMethod.Get, new Uri(settings.Eu.Http, "/svc/ship-sync")));
            response.EnsureSuccessStatusCode();
            return Results.Ok(new { sync = true });
        });
        app.MapGet("/shipping-quote", async (IHttpClientFactory http, CancellationToken ct) =>
        {
            using var response = await http.CreateClient("shipping-quote").GetAsync("/svc/quote", ct);
            return Results.Ok(new { carrierStatus = (int)response.StatusCode });
        });
        app.MapGet("/priority", async (HttpRequest request, IHttpClientFactory http, CancellationToken ct) =>
        {
            using var outgoing = new HttpRequestMessage(HttpMethod.Get, "/svc/priority");
            outgoing.Headers.Add("X-Priority", request.Headers["X-Priority"].ToString());
            using var response = await http.CreateClient("priority").SendAsync(outgoing, ct);
            response.EnsureSuccessStatusCode();
            return Results.Ok();
        });
        app.MapGet("/by-host", async (IHttpClientFactory http, CancellationToken ct) =>
        {
            using var response = await http.CreateClient("by-host").GetAsync("/svc/by-host", ct);
            response.EnsureSuccessStatusCode();
            return Results.Ok();
        });
        app.MapPost("/upload", async (HttpRequest request, IHttpClientFactory http, CancellationToken ct) =>
        {
            // Gelen gövde geri sarılamayan bir akıştır; olduğu gibi iletilir.
            using var outgoing = new HttpRequestMessage(HttpMethod.Post, "/svc/upload") { Content = new StreamContent(request.Body) };
            using var response = await http.CreateClient("upload").SendAsync(outgoing, ct);
            response.EnsureSuccessStatusCode();
            return Results.Ok();
        });

        // --- Stratejiler
        app.MapGet("/inventory-sync", (IAegisPipelineRegistry r, IHttpClientFactory http, CancellationToken ct) => CallAsync(r, "inventory-sync", http, "inventory-sync", ct));
        app.MapGet("/recommend-cached/{sku}", async (string sku, IAegisPipelineRegistry registry, IHttpClientFactory http, CancellationToken ct) =>
        {
            var context = new AegisContext(ct);
            context.SetProperty(AegisContextKeys.CacheKey, $"rec:{sku}");
            return await registry.GetPipeline("recommend-cache").ExecuteAsync(async ValueTask<CatalogItem> (ctx) =>
                (await http.CreateClient("products").GetFromJsonAsync($"/catalog/{sku}", ShopJsonContext.Default.CatalogItem, ctx.CancellationToken))!, context);
        });
        app.MapPost("/notify", (IAegisPipelineRegistry r, IHttpClientFactory http, CancellationToken ct) => CallAsync(r, "notifications", http, "notify", ct));
        app.MapPost("/sms", (IAegisPipelineRegistry r, IHttpClientFactory http, CancellationToken ct) => CallAsync(r, "sms", http, "sms", ct));
        app.MapGet("/search-v2", (IAegisPipelineRegistry r, IHttpClientFactory http, CancellationToken ct) => CallAsync(r, "search-shadow", http, "search-v2", ct));
        app.MapGet("/search-v2/state", (IAegisPipelineRegistry r) => new { state = State(r, "search-shadow") });
        app.MapGet("/warehouse", (IAegisPipelineRegistry r, IHttpClientFactory http, CancellationToken ct) => CallAsync(r, "warehouse", http, "warehouse", ct));
        app.MapGet("/warehouse/state", (IAegisPipelineRegistry r, ResilienceProbe probe) =>
            new { state = State(r, "warehouse"), breaks = probe.WarehouseBreaks.Select(b => (int)b.TotalMilliseconds).ToArray() });
        app.MapGet("/reporting", (IAegisPipelineRegistry r, IHttpClientFactory http, CancellationToken ct) => CallAsync(r, "slow-calls", http, "reporting", ct));
        app.MapGet("/reporting/state", (IAegisPipelineRegistry r) => new { state = State(r, "slow-calls") });

        // Yalnızca token alan çalıştırma biçimi (Polly'den geçişte birebir).
        app.MapGet("/budget", async (IAegisPipelineRegistry registry, IHttpClientFactory http, CancellationToken ct) =>
        {
            await registry.GetPipeline("budget").ExecuteAsync(async token =>
            {
                using var response = await http.CreateClient("products").GetAsync("/svc/budget", token);
                response.EnsureSuccessStatusCode();
            }, ct);
            return Results.Ok();
        });

        app.MapGet("/chaos-lab/{mode}", async (string mode, IAegisPipelineRegistry registry, CancellationToken ct) =>
        {
            var context = new AegisContext(ct);
            context.SetProperty(ChaosMode, mode);
            return await registry.GetPipeline("chaos-lab").ExecuteAsync(_ => ValueTask.FromResult(new Product("gercek", "gerçek ürün", 1, "backend")), context);
        });
        app.MapGet("/chaos-lab/behaviors", (ResilienceProbe probe) => new { count = probe.ChaosBehaviors });

        app.MapGet("/fees", (IAegisPipelineRegistry r, IHttpClientFactory http, CancellationToken ct) => CallAsync(r, "fees", http, "fees", ct));

        // Durumlu (closure'suz) çalıştırma biçimi + anahtarlı boru hattı.
        app.MapGet("/carrier/ups", async (IAegisPipelineProvider<CarrierKey> pipelines, IHttpClientFactory http, CancellationToken ct) =>
        {
            await pipelines.GetPipeline(new CarrierKey("ups", "v2")).ExecuteAsync(static async (ctx, client) =>
            {
                using var response = await client.GetAsync("/svc/ups", ctx.CancellationToken);
                response.EnsureSuccessStatusCode();
                return true;
            }, http.CreateClient("products"), new AegisContext(ct));
            return Results.Ok();
        });

        app.MapGet("/tenant-api", (IAegisPipelineRegistry r, IHttpClientFactory http, CancellationToken ct) => CallAsync(r, "tenant-api", http, "tenant-api", ct));

        app.MapGet("/typed/{sku}", async (string sku, IAegisPipeline<Product> pipeline, IHttpClientFactory http, CancellationToken ct) =>
            await pipeline.ExecuteAsync(async ValueTask<Product> (ctx) =>
                (await http.CreateClient("products").GetFromJsonAsync($"/products/{sku}", ShopJsonContext.Default.Product, ctx.CancellationToken))! with { Source = "typed" },
                new AegisContext(ct)));
    }

    // Kayıt defterindeki boru hattıyla genel servise (/svc/{ad}) çağrı; başarısız durum kodu istisnaya çevrilir.
    private static async Task<IResult> CallAsync(IAegisPipelineRegistry registry, string pipeline, IHttpClientFactory http, string service, CancellationToken ct)
    {
        await registry.GetPipeline(pipeline).ExecuteAsync(async ValueTask<bool> (ctx) =>
        {
            using var response = await http.CreateClient("products").GetAsync($"/svc/{service}", ctx.CancellationToken);
            response.EnsureSuccessStatusCode();
            return true;
        }, new AegisContext(ct));
        return Results.Ok();
    }

    private static string State(IAegisPipelineRegistry registry, string pipeline) =>
        registry.GetPipeline(pipeline).Strategies.OfType<CircuitBreakerStrategy>().Single().State.ToString();
}

/// <summary>Ekibin kendi yazdığı basit yeniden deneme işleyicisi: 503'te aynı isteği bir kez daha gönderir.</summary>
public sealed class SimpleRetryHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable)
        {
            return response;
        }

        response.Dispose();
        return await base.SendAsync(request, cancellationToken);
    }
}
