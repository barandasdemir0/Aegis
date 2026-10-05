using System.Net.Http.Json;
using System.Threading.RateLimiting;
using Aegis.Resilience.AspNetCore;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Cache;
using Aegis.Resilience.Core.Strategies.Chaos;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Core.Strategies.Timeout;
using Aegis.Resilience.Data.SqlClient;
using Aegis.Resilience.Distributed.Abstractions;
using Aegis.Resilience.Distributed.Redis;
using Aegis.Resilience.Extensions.Caching;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.DependencyInjection.Aop;
using Aegis.Resilience.Extensions.Http;
using Aegis.Resilience.Extensions.Telemetry;
using Aegis.Resilience.Grpc;
using Aegis.Resilience.RateLimiting;
using Grpc.Core;
using Grpc.Net.Client.Balancer;
using Microsoft.Extensions.Diagnostics.ExceptionSummarization;
using Microsoft.Extensions.Options;
using Shop.Contracts;

namespace Shop.Api;

/// <summary>Uygulamanın tüm dayanıklılık kurulumu: boru hatları, HTTP ve gRPC istemcileri, dağıtık durum, gelen istek koruması.</summary>
public static class ShopResilience
{
    public static readonly AegisPropertyKey<string> Sku = new("sku");
    public static readonly AegisPropertyKey<string> Symbol = new("symbol");


    public static void AddShopResilience(this IServiceCollection services, ShopSettings settings)
    {
        var prefix = settings.KeyPrefix;

        // --- Dağıtık durum (Redis): devre kesici, hız sınırı, önbellek. Aynı önekle açılan örnekler aynı durumu paylaşır.
        services.AddAegisRedisStateStore(settings.Redis, keyPrefix: $"{prefix}:cb:");
        services.AddAegisRedisRateLimitStore(settings.Redis, keyPrefix: $"{prefix}:rl:");
        services.AddStackExchangeRedisCache(o => o.Configuration = settings.Redis);
        services.AddAegisDistributedCacheStore(new SystemTextJsonCacheSerializer(ShopJsonContext.Default.Options), keyPrefix: $"{prefix}:cache:");

        // --- Telemetri: Polly ile aynı etiketler + error.type / request.name (Microsoft AddResilienceEnricher eşdeğeri).
        services.AddExceptionSummarizer(b => b.AddHttpProvider());
        services.AddAegisResilienceEnricher();

        services.AddSingleton<OrderStore>();
        services.AddSingleton<CircuitBreakerStateProvider>();   // ödeme devresinin durumu (yönetim uç noktası okur)
        services.AddSingleton<CircuitBreakerManualControl>();   // bakım modu: bağlı devreleri tek anahtarla izole eder
        services.AddAegisProxiedScoped<ILegacyGateway, LegacyGateway>();

        AddPipelines(services, prefix);
        AddHttpClients(services, settings);
        AddGrpcClients(services, settings);

        // --- Gelen istek koruması: istemci başına kota (Redis ile tüm örneklerde ortak).
        services.AddAegisInboundRateLimiting(o =>
        {
            o.PartitionByHeader("X-ClientId");
            o.AddRule("GET:/limited", 3, TimeSpan.FromSeconds(30));
            o.KeyPrefix = $"{prefix}:inbound";
        });
    }

    private static void AddPipelines(IServiceCollection services, string prefix)
    {
        // Ürün okuma: yedek değer (en dış) → Redis önbellek → devre kesici → aynı anda gelen istekleri birleştir → yeniden dene → zaman aşımı.
        services.AddAegisPipeline("products", (p, sp) => p
            .AddFallback(o =>
            {
                o.ShouldHandle = ex => ex is not OperationCanceledException;
                o.FallbackHandler = (ctx, _) => ValueTask.FromResult<object?>(
                    new Product(ctx.TryGetProperty(Sku, out var sku) ? sku! : "?", "(geçici olarak gösterilemiyor)", 0, "fallback"));
            })
            .AddCache(TimeSpan.FromSeconds(30), o =>
            {
                o.KeySelector = ctx => ctx.TryGetProperty(Sku, out var sku) ? $"product:{sku}" : "product:?";
                o.Store = sp.GetRequiredService<IAegisCacheStore>();
            })
            .AddCircuitBreaker(o =>
            {
                o.MinimumThroughput = 4;
                o.FailureRatio = 0.5;
                o.BreakDuration = TimeSpan.FromSeconds(2);
            })
            .AddRequestCollapser(o => o.KeySelector = ctx => ctx.TryGetProperty(Sku, out var sku) ? $"product:{sku}" : null)
            .AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.FromMilliseconds(20); })
            .AddTimeout(TimeSpan.FromSeconds(1)));

        // Döviz kuru: biraz eski olsun ama gelsin. Taze süre 300 ms; sonrası bayat veri anında döner, arkada tek yenileme.
        services.AddAegisPipeline("fx", p => p
            .AddStaleFallback(o =>
            {
                o.FreshnessDuration = TimeSpan.FromMilliseconds(300);
                o.MaxStaleAge = TimeSpan.FromHours(1);
                o.KeyGenerator = ctx => ctx.TryGetProperty(Symbol, out var s) ? s! : "default";
            })
            .AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.FromMilliseconds(10); })
            .AddTimeout(TimeSpan.FromMilliseconds(500)));

        // Sipariş yazma: yalnızca geçici SQL hataları (kilitlenme, servis meşgul...) yeniden denenir.
        services.AddAegisPipeline("orders-db", p => p
            .AddTimeout(TimeSpan.FromSeconds(30))
            .AddRetry(o =>
            {
                o.MaxRetryAttempts = 5;
                o.BackoffType = DelayBackoffType.DecorrelatedJitter;
                o.Delay = TimeSpan.FromMilliseconds(50);
                o.MaxDelay = TimeSpan.FromSeconds(1);
                o.ShouldHandleOutcome = new AegisPredicateBuilder().HandleSqlTransientErrors();
            }));

        // Eski sistem (AOP proxy): token'a saygı göstermeyen kod için kötümser zaman aşımı.
        services.AddAegisPipeline("legacy", p => p
            .AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.FromMilliseconds(20); })
            .AddTimeout(TimeSpan.FromMilliseconds(400), o => o.Mode = TimeoutStrategyMode.Pessimistic));

        // Kapasitesi bilinmeyen bağımlılık: limit gecikmeye göre kendini ayarlar.
        services.AddAegisPipeline("adaptive", p => p.AddAdaptiveConcurrency(o =>
        {
            o.InitialConcurrency = 8;
            o.MinConcurrency = 1;
            o.MaxConcurrency = 16;
            o.WarmupSamples = 3;
            o.MinRttJitterToleranceMs = 5;
        }));

        // Kaos: yapılandırmadan canlı açılıp kapanır (kill switch).
        services.AddAegisPipeline("chaos", (p, sp) =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<ChaosSettings>>();
            ChaosSettings? last = null;
            ChaosOptions? current = null;
            p.AddChaos(o => o.OptionsProvider = () =>
            {
                var settings = monitor.CurrentValue;
                if (!ReferenceEquals(settings, last))
                {
                    current = new ChaosOptions
                    {
                        Enabled = settings.Enabled,
                        InjectionRate = settings.Rate,
                        FaultGenerator = () => new HttpRequestException("kaos: yapay arıza")
                    };
                    last = settings;
                }

                return current!;
            });
        });

        // Ağır rapor (gelen istek): aynı anda tek rapor, en fazla 300 ms.
        services.AddAegisPipeline("reports", p => p.AddConcurrencyLimiter(1).AddTimeout(TimeSpan.FromMilliseconds(300)));

        // Arama: kayan pencere (Aegis) ve .NET System.Threading.RateLimiting köprüsü.
        services.AddAegisPipeline("search", p => p.AddSlidingWindowRateLimiter(3, TimeSpan.FromSeconds(30), segmentsPerWindow: 6));
        services.AddAegisPipeline("search-bcl", p => p.AddTokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 2,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromSeconds(30),
            QueueLimit = 0
        }));

        // İş ortağı API'si: kiracı başına kota → tüm podlarda ortak kota → tüm podlarda ortak devre (Redis).
        services.AddAegisPipeline("partners", (p, sp) => p
            .AddPartitionedRateLimiter(o =>
            {
                o.PartitionKeySelector = ctx => ctx.GetRequestMessage()?.Headers.TryGetValues("X-Tenant", out var t) == true ? t.First() : "anonim";
                o.DefaultOptions = new RateLimiterOptions { PermitLimit = 2, Window = TimeSpan.FromMinutes(1) };
                o.OptionsFactory = tenant => tenant == "vip" ? new RateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) } : o.DefaultOptions;
            })
            .AddDistributedRateLimiter(sp.GetRequiredService<IDistributedRateLimitStore>(), o =>
            {
                o.LimiterKey = "partners-global";
                o.Algorithm = DistributedRateLimitAlgorithm.FixedWindow;
                o.PermitLimit = 10;
                o.Window = TimeSpan.FromSeconds(30);
            })
            .AddDistributedCircuitBreaker(sp.GetRequiredService<ICircuitBreakerStateStore>(), o =>
            {
                o.CircuitKey = "partners";
                o.FailureRatio = 0.5;
                o.MinimumThroughput = 3;
                o.BreakDuration = TimeSpan.FromSeconds(2);
                o.StateCacheDuration = TimeSpan.Zero;
            }));

        // Kiracı başına ayrı devre (anahtarlı, ilk erişimde kurulan boru hatları): bir kiracının çöküşü diğerini etkilemez.
        services.AddAegisPipelines<string>((b, tenant, sp) => b
            .AddCircuitBreaker(o => { o.ConsecutiveFailureThreshold = 2; o.BreakDuration = TimeSpan.FromSeconds(2); })
            .AddTimeout(TimeSpan.FromSeconds(1)),
            maxDynamicPipelines: 100);

        // Yapılandırma değişince yeniden kurulan boru hattı (yeniden deneme sayısı canlı değişir).
        services.AddAegisPipelineWithContext("reloadable", (b, ctx) =>
        {
            var retries = ctx.GetOptions<RetrySettings>();
            ctx.EnableReloads<RetrySettings>();
            b.AddRetry(o => { o.MaxRetryAttempts = retries.Attempts; o.Delay = TimeSpan.Zero; });
        });

        // Teklif: AB bölgesi 150 ms'de yanıt vermezse ABD bölgesine paralel yedek istek (çekirdek hedging + ActionGenerator).
        services.AddAegisPipeline("quote", (p, sp) => p.AddHedging(o =>
        {
            o.MaxHedgedAttempts = 1;
            o.HedgingDelay = TimeSpan.FromMilliseconds(150);
            o.ActionGenerator = _ => async ctx =>
            {
                var sku = ctx.TryGetProperty(Sku, out var s) ? s : "?";
                return await sp.GetRequiredService<IHttpClientFactory>().CreateClient("quote-us")
                    .GetFromJsonAsync($"/pricing/{sku}", ShopJsonContext.Default.Priced, ctx.CancellationToken);
            };
        }));
    }

    private static void AddHttpClients(IServiceCollection services, ShopSettings settings)
    {
        // Ödeme: Microsoft standart işleyicisinin eşdeğeri. Idempotency-Key'li POST güvenle yeniden denenir.
        services.AddHttpClient("payment", c => c.BaseAddress = settings.Eu.Http)
            .RemoveAllAegisHandlers()
            .AddStandardAegisHandler((o, sp) =>
            {
                o.Retry.MaxRetryAttempts = 3;
                o.Retry.Delay = TimeSpan.FromMilliseconds(50);
                o.Retry.MaxDelay = TimeSpan.FromSeconds(2);
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(1);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(5);
                o.CircuitBreaker.MinimumThroughput = 10;
                o.CircuitBreaker.FailureRatio = 0.5;
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(10);
                o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(2);
                o.CircuitBreaker.StateProvider = sp.GetRequiredService<CircuitBreakerStateProvider>();
                o.CircuitBreaker.ManualControl = sp.GetRequiredService<CircuitBreakerManualControl>();
            });

        // Fiyat: standart hedging; 1. deneme AB, yanıt gecikirse 2. deneme ABD. Uç nokta başına ayrı devre.
        services.AddHttpClient("pricing", c => c.BaseAddress = settings.Eu.Http)
            .RemoveAllAegisHandlers()
            .AddStandardAegisHedgingHandler(o =>
            {
                o.MaxHedgedAttempts = 1;
                o.HedgingDelay = TimeSpan.FromMilliseconds(150);
                o.OrderedGroups.Add(new UriEndpointGroup { Endpoints = { new() { Uri = settings.Eu.Http } } });
                o.OrderedGroups.Add(new UriEndpointGroup { Endpoints = { new() { Uri = settings.Us.Http } } });
            });

        // Katalog: iki veri merkezi arasında spekülatif istek (yalnızca Aegis'te).
        services.AddHttpClient("catalog", c => c.BaseAddress = settings.Eu.Http)
            .RemoveAllAegisHandlers()
            .AddMultiEndpointHedgingHandler(o =>
            {
                o.Endpoints = [settings.Eu.Http, settings.Us.Http];
                o.HedgingDelay = TimeSpan.FromMilliseconds(150);
            });

        // Öneri: %10 kanarya, aynı kullanıcı hep aynı sürüme (yapışkan oturum).
        services.AddHttpClient("recommend", c => c.BaseAddress = settings.Eu.Http)
            .RemoveAllAegisHandlers()
            .AddWeightedCanaryHandler(o =>
            {
                o.Endpoints = [new WeightedEndpoint(settings.Eu.Http, 90), new WeightedEndpoint(settings.Canary, 10)];
                o.StickySessionKeySelector = r => r.Headers.TryGetValues("X-User-Id", out var v) ? v.First() : null;
            });

        // İş ortağı: kayıt defterindeki "partners" boru hattı HTTP işleyicisi olarak.
        services.AddHttpClient("partners", c => c.BaseAddress = settings.Eu.Http)
            .RemoveAllAegisHandlers()
            .AddAegisResilienceHandler("partners");

        // Boru hattı kodda çalıştırılan istemciler: Aspire'ın varsayılan işleyicisi kaldırılır (çift yeniden deneme olmasın).
        foreach (var name in new[] { "products", "fx", "legacy", "slow", "tenant", "reload", "quote-eu" })
        {
            services.AddHttpClient(name, c => c.BaseAddress = settings.Eu.Http).RemoveAllAegisHandlers();
        }

        services.AddHttpClient("quote-us", c => c.BaseAddress = settings.Us.Http).RemoveAllAegisHandlers();

        // "status": hiçbir şey eklenmez; Aspire ServiceDefaults'un tüm istemcilere verdiği standart işleyiciyi kullanır.
        services.AddHttpClient("status", c => c.BaseAddress = settings.Eu.Http);
    }

    private static void AddGrpcClients(IServiceCollection services, ShopSettings settings)
    {
        // Stok (tek sunucu): gRPC katmanında retry, devre, zaman aşımı; akışlarda tamponlu yeniden oynatma.
        services.AddGrpcClient<Inventory.InventoryClient>("inventory", o => o.Address = settings.Eu.Grpc)
            .AddStandardAegisGrpcResilience(o =>
            {
                o.Retry.MaxRetryAttempts = 3;
                o.Retry.Delay = TimeSpan.FromMilliseconds(50);
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);
            });

        // Stok havuzu (iki sunucu): art arda hata veren sunucu yük dengelemeden geçici çıkarılır.
        services.AddSingleton<ResolverFactory>(new StaticResolverFactory(_ =>
            [new BalancerAddress(settings.Eu.Grpc.Host, settings.Eu.Grpc.Port), new BalancerAddress(settings.Us.Grpc.Host, settings.Us.Grpc.Port)]));
        services.AddGrpcClient<Inventory.InventoryClient>("inventory-pool", o => o.Address = new Uri("static:///inventory"))
            .ConfigureChannel(c => c.Credentials = ChannelCredentials.Insecure)
            .AddAegisGrpcOutlierDetection(o =>
            {
                o.ConsecutiveFailures = 3;
                o.MaxEjectionPercent = 50;
            });
    }
}

