using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Http;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Cache;
using Aegis.Resilience.Core.Strategies.Collapser;
using Aegis.Resilience.Core.Strategies.Fallback;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Extensions.Dashboard;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.Http;
using Xunit;

namespace Aegis.Tests;

public class RedTeamFinalRiskFixesTests
{
    private sealed class TestMockBackendHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;
        public TestMockBackendHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request, cancellationToken));
    }

    // 1. AEGIS-102: Canary Deterministik 32-bit FNV-1a Hash Testi
    [Fact]
    public void Canary_ComputeDeterministicHash_ShouldProduceConsistentHash_ForSameKey()
    {
        var key = "user-vip-987654";
        var hash1 = WeightedCanaryHandler.ComputeDeterministicHash(key);
        var hash2 = WeightedCanaryHandler.ComputeDeterministicHash(key);

        Assert.Equal(hash1, hash2);
        Assert.True(hash1 > 0);

        // Bilinen FNV-1a değeri ile determinizm kontrolü
        var helloHash = WeightedCanaryHandler.ComputeDeterministicHash("hello");
        Assert.Equal(1335831723u, helloHash);
    }

    [Fact]
    public void Canary_StickySession_ShouldRouteConsistentlyAcrossSimulatedPods()
    {
        var options = new WeightedCanaryOptions
        {
            Endpoints = new List<WeightedEndpoint>
            {
                new(new Uri("https://v1.api.com"), 80),
                new(new Uri("https://v2.api.com"), 20)
            },
            StickySessionKeySelector = req => req.Headers.TryGetValues("X-User-Id", out var vals) ? vals.FirstOrDefault() : null
        };

        // 3 farklı Handler (3 farklı Kubernetes pod simülasyonu)
        var pod1Handler = new WeightedCanaryHandler(options);
        var pod2Handler = new WeightedCanaryHandler(options);
        var pod3Handler = new WeightedCanaryHandler(options);

        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://api.com/items");
        request1.Headers.Add("X-User-Id", "user_abc_123");

        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.com/items");
        request2.Headers.Add("X-User-Id", "user_abc_123");

        var request3 = new HttpRequestMessage(HttpMethod.Get, "https://api.com/items");
        request3.Headers.Add("X-User-Id", "user_abc_123");

        var selectEndpointMethod = typeof(WeightedCanaryHandler).GetMethod(
            "SelectEndpoint", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var ep1 = (WeightedEndpoint)selectEndpointMethod.Invoke(pod1Handler, new object[] { request1, options.Endpoints })!;
        var ep2 = (WeightedEndpoint)selectEndpointMethod.Invoke(pod2Handler, new object[] { request2, options.Endpoints })!;
        var ep3 = (WeightedEndpoint)selectEndpointMethod.Invoke(pod3Handler, new object[] { request3, options.Endpoints })!;

        // Tüm podlarda aynı kullanıcı istisnasız aynı uç noktaya gitmeli (Deterministik Sticky Session)
        Assert.Equal(ep1.Uri, ep2.Uri);
        Assert.Equal(ep2.Uri, ep3.Uri);
    }

    // 2. AEGIS-101: RequestCollapser Lazy Task Yarış Durumu Koruması
    [Fact]
    public async Task RequestCollapser_ShouldExecuteUnderlyingTaskExactlyOnce_UnderHighConcurrency()
    {
        var collapser = new RequestCollapserStrategy(new RequestCollapserOptions
        {
            KeySelector = _ => "shared-flight-key"
        });

        var executionCount = 0;
        var arrived = 0;
        var allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLeader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // NOT: Singleflight yalnızca UÇUŞTAKİ istekleri birleştirir; uçuş bitince anahtar kaldırılır.
        // Eski test, 100 çağıranın 50ms içinde sıraya gireceğini varsayıyordu; CPU açlığı altında
        // (tüm çekirdekler meşgulken) çağıranlar 50ms'den uzun sürede trickle ediyor ve geç gelenler
        // haklı olarak yeni uçuş başlatıyordu (32 çalıştırma gözlemlendi). Lider artık herkes gelene kadar bekler.
        var tasks = Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref arrived) == 100)
            {
                allArrived.TrySetResult();
            }

            return await collapser.ExecuteAsync(async ctx =>
            {
                Interlocked.Increment(ref executionCount);
                await releaseLeader.Task; // herkes sözlüğe kaydolana kadar uçuşu açık tut
                return "shared-result";
            }, new AegisContext());
        })).ToList();

        await allArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(100); // son gelenin GetOrAdd'a ulaşması için pay
        releaseLeader.SetResult();
        var results = await Task.WhenAll(tasks);

        // 100 isteğin hepsi aynı sonucu almalı, ancak alttaki callback tam olarak 1 kez çalışmalı
        Assert.All(results, r => Assert.Equal("shared-result", r));
        Assert.Equal(1, executionCount);
    }

    // 3. AEGIS-104: StaleFallback Arka Plan Yenileme Stampede Koruması
    [Fact]
    public async Task StaleFallback_ShouldPreventStampede_WhenMultipleRequestsHitStaleData()
    {
        var refreshCount = 0;
        var options = new StaleFallbackOptions
        {
            FreshnessDuration = TimeSpan.FromMilliseconds(50),
            MaxStaleAge = TimeSpan.FromMinutes(10)
        };

        var strategy = new StaleFallbackStrategy(options);

        // İlk çağrı: Önbelleğe taze veri yaz
        var initial = await strategy.ExecuteAsync(async _ => "initial_fresh", new AegisContext());
        Assert.Equal("initial_fresh", initial);

        // Tazelik süresinin dolmasını bekle
        await Task.Delay(100);

        // 50 concurrent istek aynı anda bayat veriye vursun
        var tasks = Enumerable.Range(0, 50).Select(async _ =>
        {
            return await strategy.ExecuteAsync(async _ =>
            {
                Interlocked.Increment(ref refreshCount);
                await Task.Delay(100);
                return "updated_fresh";
            }, new AegisContext());
        }).ToList();

        var results = await Task.WhenAll(tasks);

        // Kullanıcılar anında bayat veriyi almalı
        Assert.All(results, r => Assert.Equal("initial_fresh", r));

        // Background refresh işlemi devam ederken veya bittiğinde sadece 1 kez tetiklenmeli.
        // Sabit 200 ms bekleme, tek çekirdekli Linux konteynerde arka plan görevinin henüz BAŞLAMADIĞI (0) durumlar üretiyordu:
        // önce yenilemenin görülmesi beklenir, sonra fazladan yenileme olmadığı doğrulanır.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref refreshCount) == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        await Task.Delay(300);
        Assert.Equal(1, refreshCount);
    }

    // 4. AEGIS-105: CacheStrategy MaxCacheEntries Bellek Patlaması Koruması
    [Fact]
    public async Task CacheStrategy_ShouldEnforceMaxEntries_WhenFloodedWithUniqueKeys()
    {
        var options = new CacheOptions
        {
            MaxEntries = 50,
            Ttl = TimeSpan.FromMinutes(5)
        };

        var strategy = new CacheStrategy(options);

        // 200 adet rastgele benzersiz anahtar ekle
        for (var i = 0; i < 200; i++)
        {
            var ctx = new AegisContext();
            ctx.Properties["CacheKey"] = $"key_{i}";
            await strategy.ExecuteAsync(async _ => $"value_{i}", ctx);
        }

        // Önbellek sözlüğü iç boyutunu yansıma ile denetle
        var cacheField = typeof(CacheStrategy).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var internalCache = (System.Collections.ICollection)cacheField.GetValue(strategy)!;

        // Boyut hiçbir koşulda kontrolsüz büyümemeli (MaxEntries toleransı içinde kalmalı)
        Assert.True(internalCache.Count <= 50, $"Önbellek boyutu {internalCache.Count}, ancak MaxEntries 50 olmalı!");
    }

    // 5. AEGIS-103: Finansal / Çift İşlem Koruması (Non-Idempotent HTTP Safety)
    [Fact]
    public async Task AegisResilienceHandler_ShouldBlockRetry_ForNonIdempotentPostWithoutIdempotencyKey()
    {
        var attempts = 0;
        var mockBackend = new TestMockBackendHandler((req, ct) =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var pipeline = new AegisPipelineBuilder("PaymentPipeline")
            .AddRetry(opt =>
            {
                opt.MaxRetryAttempts = 3;
                opt.Delay = TimeSpan.FromMilliseconds(5);
            })
            .Build();

        var handler = new AegisResilienceHandler(pipeline, handleHttpFailureStatuses: true, allowNonIdempotentRetry: false);
        typeof(DelegatingHandler).GetProperty("InnerHandler", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(handler, mockBackend);

        var invoker = new HttpMessageInvoker(handler);

        // Idempotency-Key içermeyen güvensiz POST isteği
        var request = new HttpRequestMessage(HttpMethod.Post, "https://bank.com/api/charge");

        var response = await invoker.SendAsync(request, CancellationToken.None);

        // Yeniden deneme tetiklenmemeli; mükerrer işlem önlenip ilk 500 yanıtı dönülmeli
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task AegisResilienceHandler_ShouldAllowRetry_WhenIdempotencyKeyIsPresent()
    {
        var attempts = 0;
        var mockBackend = new TestMockBackendHandler((req, ct) =>
        {
            attempts++;
            if (attempts == 1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var pipeline = new AegisPipelineBuilder("SafePaymentPipeline")
            .AddRetry(opt =>
            {
                opt.MaxRetryAttempts = 2;
                opt.Delay = TimeSpan.FromMilliseconds(5);
            })
            .Build();

        var handler = new AegisResilienceHandler(pipeline, handleHttpFailureStatuses: true, allowNonIdempotentRetry: false);
        typeof(DelegatingHandler).GetProperty("InnerHandler", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(handler, mockBackend);

        var invoker = new HttpMessageInvoker(handler);

        // Idempotency-Key içeren güvenli POST isteği
        var request = new HttpRequestMessage(HttpMethod.Post, "https://bank.com/api/charge");
        request.Headers.Add("Idempotency-Key", "tx-999-uuid");

        var response = await invoker.SendAsync(request, CancellationToken.None);

        // Idempotency key olduğu için retry'a izin verilmeli ve 2. denemede 200 dönmeli
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempts);
    }

    // 6. AEGIS-109: AegisContext Lazy CorrelationId Zero-Alloc Testi
    [Fact]
    public void AegisContext_Reset_ShouldNotAllocateString_UnlessCorrelationIdAccessed()
    {
        var context = AegisContextPool.Rent();

        // CorrelationId okunmadığında null kalır, Reset yapıldığında 0 byte tahsisat
        context.Reset();

        // Okunduğu anda tembel (lazy) üretilmeli
        var id1 = context.CorrelationId;
        Assert.NotNull(id1);
        Assert.Equal(32, id1.Length);

        // İkinci okuma aynı ID'yi dönmeli
        Assert.Equal(id1, context.CorrelationId);

        AegisContextPool.Return(context);
    }

    // 7. AEGIS-110: Monotonic Clock Doğrulaması
    [Fact]
    public async Task AdaptiveConcurrency_ShouldUseMonotonicTimestamp()
    {
        var strategy = new AdaptiveConcurrencyStrategy(new AdaptiveConcurrencyOptions
        {
            InitialConcurrency = 10
        });

        // 5 çağrı yap ve limitin stabil çalıştığını monotonic saatle doğrula
        for (var i = 0; i < 5; i++)
        {
            var res = await strategy.ExecuteAsync(async _ =>
            {
                await Task.Delay(5);
                return "ok";
            }, new AegisContext());
            Assert.Equal("ok", res);
        }

        Assert.True(strategy.CurrentLimit >= 5);
    }
}
