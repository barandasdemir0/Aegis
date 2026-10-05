using System.Net;
using System.Threading.RateLimiting;
using AspNetCoreRateLimit;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Caching.Memory;
using Aegis.Resilience.AspNetCore;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Distributed.Abstractions;
using PollyContext = Polly.Context;

/// <summary>
/// NuGet'teki ilk 20 resilience paketinden, Aegis'in bu turda eşdeğerini eklediği yetenekler için doğrudan karşılaştırma.
/// Her kategoride rakip referanstır (Ratio = Aegis / rakip). İki taraf da genel API'siyle, aynı kuralla ve aynı işle ölçülür;
/// sınırlar hiç reddetmeyecek kadar yüksektir (ölçülen: kütüphanenin başarı yolundaki kendi ek yükü).
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class EcosystemBenchmarks
{
    private static readonly Func<PollyContext, Task<int>> PollyV7Work = static _ => Task.FromResult(42);
    private static readonly Func<Task<int>> PollyV7Plain = static () => Task.FromResult(42);
    private static readonly Func<AegisContext, ValueTask<int>> AegisWork = static _ => new ValueTask<int>(42);
    private static readonly Func<CancellationToken, ValueTask<int>> PollyV8Work = static _ => new ValueTask<int>(42);

    private readonly PollyContext _pollyCacheContext = new("anahtar");

    private IAsyncPolicy<int> _pollyV7Cache = null!;
    private IAsyncPolicy _pollyV7Breaker = null!;
    private ResiliencePipeline _pollyTokenBucket = null!;
    private IAegisPipeline _aegisCache = null!, _aegisBreaker = null!, _aegisTokenBucket = null!, _aegisDistributedLimiter = null!;
    private MemoryCache _memoryCache = null!;
    private RequestDelegate _aspNetCoreRateLimit = null!, _aegisInbound = null!;
    private ServiceProvider _aspNetCoreRateLimitServices = null!, _aegisInboundServices = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Cache: Polly v7 + Polly.Caching.Memory (IMemoryCache) — Aegis yerleşik bellek içi depo. İkisi de 5 dk sabit süre.
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        _pollyV7Cache = Policy.CacheAsync<int>(new MemoryCacheProvider(_memoryCache), TimeSpan.FromMinutes(5));
        _aegisCache = new AegisPipelineBuilder("cache").AddCache(o => { o.KeySelector = _ => "anahtar"; o.Ttl = TimeSpan.FromMinutes(5); }).Build();

        // Art arda hata devresi: Polly v7 CircuitBreakerAsync(5, 30 sn) — Aegis ConsecutiveFailureThreshold = 5.
        _pollyV7Breaker = Policy.Handle<InvalidOperationException>().CircuitBreakerAsync(5, TimeSpan.FromSeconds(30));
        _aegisBreaker = new AegisPipelineBuilder("cb")
            .AddCircuitBreaker(o => { o.ConsecutiveFailureThreshold = 5; o.ShouldHandle = static ex => ex is InvalidOperationException; })
            .Build();

        // Token bucket: Polly.RateLimiting + System.Threading.RateLimiting — Aegis yerleşik kova ve dağıtık sınırlayıcı (bellek içi depo).
        _pollyTokenBucket = new ResiliencePipelineBuilder()
            .AddRateLimiter(new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = int.MaxValue, TokensPerPeriod = int.MaxValue, ReplenishmentPeriod = TimeSpan.FromSeconds(1), QueueLimit = 0
            }))
            .Build();
        _aegisTokenBucket = new AegisPipelineBuilder("kova").AddRateLimiter(int.MaxValue, TimeSpan.FromSeconds(1)).Build();
        _aegisDistributedLimiter = new AegisPipelineBuilder("dagitik")
            .AddDistributedRateLimiter(new InMemoryDistributedRateLimitStore(), o => { o.PermitLimit = int.MaxValue; o.Window = TimeSpan.FromSeconds(1); })
            .Build();

        // Sunucu tarafı hız sınırı: AspNetCoreRateLimit (IP kuralı) — Aegis gelen istek sınırlayıcı. İkisi de "*" / saatte 1 milyar.
        _aspNetCoreRateLimitServices = BuildServices(services =>
        {
            services.AddMemoryCache();
            services.Configure<IpRateLimitOptions>(o => o.GeneralRules = [new RateLimitRule { Endpoint = "*", Limit = 1_000_000_000, Period = "1h" }]);
            services.AddInMemoryRateLimiting();
            services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();
        });
        _aspNetCoreRateLimit = BuildApp(_aspNetCoreRateLimitServices, static app => app.UseIpRateLimiting());

        _aegisInboundServices = BuildServices(services =>
            services.AddAegisInboundRateLimiting(o => o.AddRule("*", 1_000_000_000, TimeSpan.FromHours(1))));
        _aegisInbound = BuildApp(_aegisInboundServices, static app => app.UseAegisInboundRateLimiting());
    }

    private static ServiceProvider BuildServices(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddOptions();
        configure(services);
        return services.BuildServiceProvider();
    }

    private static RequestDelegate BuildApp(IServiceProvider services, Action<IApplicationBuilder> middleware)
    {
        var app = new ApplicationBuilder(services);
        middleware(app);
        app.Run(static _ => Task.CompletedTask);
        return app.Build();
    }

    private static DefaultHttpContext NewRequest()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/urun";
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        return context;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _aegisCache.Dispose();
        _aegisBreaker.Dispose();
        _aegisTokenBucket.Dispose();
        _aegisDistributedLimiter.Dispose();
        _memoryCache.Dispose();
        _aspNetCoreRateLimitServices.Dispose();
        _aegisInboundServices.Dispose();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("E1-Cache-Isabet")]
    public Task<int> PollyV7_CacheHit() => _pollyV7Cache.ExecuteAsync(PollyV7Work, _pollyCacheContext);

    [Benchmark, BenchmarkCategory("E1-Cache-Isabet")]
    public ValueTask<int> Aegis_CacheHit() => _aegisCache.ExecuteAsync(AegisWork);

    [Benchmark(Baseline = true), BenchmarkCategory("E2-ArtArda-Devre")]
    public Task<int> PollyV7_ConsecutiveBreaker() => _pollyV7Breaker.ExecuteAsync(PollyV7Plain);

    [Benchmark, BenchmarkCategory("E2-ArtArda-Devre")]
    public ValueTask<int> Aegis_ConsecutiveBreaker() => _aegisBreaker.ExecuteAsync(AegisWork);

    [Benchmark(Baseline = true), BenchmarkCategory("E3-TokenBucket")]
    public ValueTask<int> PollyRateLimiting_TokenBucket() => _pollyTokenBucket.ExecuteAsync(PollyV8Work, CancellationToken.None);

    [Benchmark, BenchmarkCategory("E3-TokenBucket")]
    public ValueTask<int> Aegis_TokenBucket() => _aegisTokenBucket.ExecuteAsync(AegisWork);

    [Benchmark, BenchmarkCategory("E3-TokenBucket")]
    public ValueTask<int> Aegis_DistributedLimiter_InMemory() => _aegisDistributedLimiter.ExecuteAsync(AegisWork);

    [Benchmark(Baseline = true), BenchmarkCategory("E4-Sunucu-HizSiniri")]
    public Task AspNetCoreRateLimit_Request() => _aspNetCoreRateLimit(NewRequest());

    [Benchmark, BenchmarkCategory("E4-Sunucu-HizSiniri")]
    public Task Aegis_Inbound_Request() => _aegisInbound(NewRequest());
}
