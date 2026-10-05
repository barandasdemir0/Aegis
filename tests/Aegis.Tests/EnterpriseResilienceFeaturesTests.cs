using System.Net;
using System.Net.Http;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Core.Strategies.Timeout;
using Aegis.Resilience.Extensions.Http;
using Xunit;

namespace Aegis.Tests;

public class EnterpriseResilienceFeaturesTests
{
    // 1. Request Collapser (Singleflight Pattern)
    [Fact]
    public async Task RequestCollapser_ConcurrentDuplicateRequests_ShouldExecuteCallbackOnceAndShareResult()
    {
        var pipeline = new AegisPipelineBuilder("CollapserPipeline")
            .AddRequestCollapser(opt =>
            {
                opt.KeySelector = ctx => ctx.Properties.TryGetValue("Symbol", out var s) ? s?.ToString() : ctx.CorrelationId;
            })
            .Build();

        var executionCount = 0;
        var tcs = new TaskCompletionSource<string>();

        // 10 eşzamanlı istek başlatıyoruz
        var tasks = Enumerable.Range(0, 10).Select(_ =>
        {
            var ctx = new AegisContext();
            ctx.Properties["Symbol"] = "THYAO";
            return pipeline.ExecuteAsync(async _ =>
            {
                Interlocked.Increment(ref executionCount);
                return await tcs.Task;
            }, ctx).AsTask();
        }).ToList();

        await Task.Delay(50);
        tcs.SetResult("300.50-TRY");

        var results = await Task.WhenAll(tasks);

        // 10 paralel istek olmasına rağmen ana callback sadece 1 kez çalıştırılmalı
        Assert.Equal(1, executionCount);
        Assert.All(results, r => Assert.Equal("300.50-TRY", r));
    }

    // 2. AWS Decorrelated Jitter Backoff V2
    [Fact]
    public void DecorrelatedJitter_CalculateDelay_ShouldProduceBoundedValues()
    {
        var options = new RetryOptions
        {
            BackoffType = DelayBackoffType.DecorrelatedJitter,
            Delay = TimeSpan.FromMilliseconds(50),
            MaxDelay = TimeSpan.FromMilliseconds(500)
        };

        TimeSpan? prev = null;
        for (var i = 1; i <= 5; i++)
        {
            var delay = RetryStrategy.CalculateDelay(i, options, prev);
            Assert.True(delay >= TimeSpan.FromMilliseconds(50));
            Assert.True(delay <= TimeSpan.FromMilliseconds(500));
            prev = delay;
        }
    }

    // 3. Cache-Aside Strategy
    [Fact]
    public async Task CacheStrategy_CacheAside_ShouldReturnCachedValueOnHit_AndExpireOnTtl()
    {
        // Cache Hit senaryosu uzun TTL ile ölçülür: yük altında adımlar arasındaki gecikme
        // önbelleği erken düşüremez (deterministik hit).
        var pipeline = new AegisPipelineBuilder("CachePipeline")
            .AddCache(TimeSpan.FromSeconds(30), opt =>
            {
                opt.KeySelector = ctx => ctx.Properties.TryGetValue("Key", out var k) ? k?.ToString()! : "default";
            })
            .Build();

        var counter = 0;
        Func<AegisContext, ValueTask<string>> action = _ =>
        {
            Interlocked.Increment(ref counter);
            return ValueTask.FromResult($"Val-{counter}");
        };

        var ctx = new AegisContext();
        ctx.Properties["Key"] = "User_1";

        // 1. Çağrı: Cache Miss -> Hesaplama yapılır
        var res1 = await pipeline.ExecuteAsync(action, ctx);
        Assert.Equal("Val-1", res1);
        Assert.Equal(1, counter);

        // 2. Çağrı: Cache Hit -> Önbellekten döner, sayaç artmaz
        var res2 = await pipeline.ExecuteAsync(action, ctx);
        Assert.Equal("Val-1", res2);
        Assert.Equal(1, counter);

        // TTL sonrası tahliye (expiry) senaryosu kısa TTL'li ayrı bir boru hattında ölçülür:
        // beklemenin yük altında uzaması sonucu bozmaz.
        var expiringPipeline = new AegisPipelineBuilder("CacheExpiryPipeline")
            .AddCache(TimeSpan.FromMilliseconds(100), opt =>
            {
                opt.KeySelector = ctx => ctx.Properties.TryGetValue("Key", out var k) ? k?.ToString()! : "default";
            })
            .Build();

        var expiryCounter = 0;
        Func<AegisContext, ValueTask<string>> expiringAction = _ =>
        {
            Interlocked.Increment(ref expiryCounter);
            return ValueTask.FromResult($"Exp-{expiryCounter}");
        };

        var expCtx = new AegisContext();
        expCtx.Properties["Key"] = "User_2";

        var exp1 = await expiringPipeline.ExecuteAsync(expiringAction, expCtx);
        Assert.Equal("Exp-1", exp1);

        await Task.Delay(500);

        // Süre doldu -> Cache Miss -> Yeniden hesaplanır
        var exp2 = await expiringPipeline.ExecuteAsync(expiringAction, expCtx);
        Assert.Equal("Exp-2", exp2);
        Assert.Equal(2, expiryCounter);
    }

    // 4. Pessimistic / Isolated Timeout
    [Fact]
    public async Task PessimisticTimeout_UncooperativeCallback_ShouldThrowImmediatelyWithoutHanging()
    {
        var pipeline = new AegisPipelineBuilder("PessimisticTimeoutPipeline")
            .AddTimeout(TimeSpan.FromMilliseconds(100), opt =>
            {
                opt.Mode = TimeoutStrategyMode.Pessimistic;
            })
            .Build();

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Callback token'ı tamamen yok sayarak 2 saniye beklemeye çalışıyor
        await Assert.ThrowsAsync<AegisTimeoutException>(async () =>
        {
            await pipeline.ExecuteAsync(async _ =>
            {
                await Task.Delay(2000, CancellationToken.None); // Token'ı yok sayan inatçı kod
                return "completed";
            });
        });

        sw.Stop();
        // 2 saniye beklemeden, timeout fırlatarak akışı serbest bırakmalı
        // (yük altındaki thread-pool gecikmelerine tolerans için üst sınır geniş tutulur)
        Assert.True(sw.ElapsedMilliseconds < 1500, $"Pessimistic timeout beklenenden uzun sürdü: {sw.ElapsedMilliseconds}ms");
    }

    // 5. Zero-Exception Result-Based Handling on Retry
    [Fact]
    public async Task RetryStrategy_ShouldHandleResult_ShouldRetryWithoutExceptions()
    {
        var retriedAttempts = 0;
        var pipeline = new AegisPipelineBuilder("ResultRetryPipeline")
            .AddRetry(opt =>
            {
                opt.MaxRetryAttempts = 3;
                opt.Delay = TimeSpan.FromMilliseconds(10);
                opt.ShouldHandleResult = result => result is null or "";
                opt.OnRetry = ctx =>
                {
                    retriedAttempts++;
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        var callCount = 0;
        var result = await pipeline.ExecuteAsync(_ =>
        {
            callCount++;
            if (callCount < 3)
            {
                return ValueTask.FromResult<string?>(null); // Hata fırlatmıyoruz, null dönüyoruz
            }
            return ValueTask.FromResult<string?>("Success");
        });

        Assert.Equal("Success", result);
        Assert.Equal(3, callCount);
        Assert.Equal(2, retriedAttempts);
    }

    // 6. Zero-Exception Result-Based Handling on CircuitBreaker
    [Fact]
    public async Task CircuitBreaker_ShouldHandleResult_ShouldTripOpenOnFailureResult()
    {
        var pipeline = new AegisPipelineBuilder("ResultCBPipeline")
            .AddCircuitBreaker(opt =>
            {
                opt.MinimumThroughput = 2;
                opt.FailureRatio = 0.5;
                opt.BreakDuration = TimeSpan.FromSeconds(5);
                opt.ShouldHandleResult = result => result is int code && code >= 500;
            })
            .Build();

        // 2 başarısız HTTP status code dönelim (500)
        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(500));
        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(503));

        // Devre açık olmalı ve istisna fırlatmalı
        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
        {
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult(200));
        });
    }

    // 7. Partitioned / Multi-Tenant Rate Limiting
    [Fact]
    public async Task PartitionedRateLimiter_DifferentPartitions_ShouldHaveIsolatedLimits()
    {
        var pipeline = new AegisPipelineBuilder("PartitionedRateLimiterPipeline")
            .AddPartitionedRateLimiter(opt =>
            {
                opt.PartitionKeySelector = ctx => ctx.Properties.TryGetValue("TenantId", out var t) ? t?.ToString()! : "default";
                opt.DefaultOptions = new RateLimiterOptions
                {
                    PermitLimit = 1,
                    Window = TimeSpan.FromMinutes(1),
                    QueueTimeout = TimeSpan.Zero
                };
            })
            .Build();

        var tenantACtx = new AegisContext();
        tenantACtx.Properties["TenantId"] = "Tenant-A";

        var tenantBCtx = new AegisContext();
        tenantBCtx.Properties["TenantId"] = "Tenant-B";

        // Tenant A: 1. istek başarılı
        var resA1 = await pipeline.ExecuteAsync(_ => ValueTask.FromResult("A1"), tenantACtx);
        Assert.Equal("A1", resA1);

        // Tenant A: 2. istek kotaya takılır ve reddedilir
        await Assert.ThrowsAsync<RateLimitRejectedException>(async () =>
        {
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult("A2"), tenantACtx);
        });

        // Tenant B: A'nın kotası dolmuş olsa bile B tamamen izoledir ve 1. isteği başarılı olur!
        var resB1 = await pipeline.ExecuteAsync(_ => ValueTask.FromResult("B1"), tenantBCtx);
        Assert.Equal("B1", resB1);
    }

    // 8. Multi-Endpoint Hedging Handler
    private sealed class MockMultiEndpointHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host == "primary.api.com")
            {
                // Birincil yavaş: 1000ms beklet
                await Task.Delay(1000, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { ReasonPhrase = "Primary" };
            }

            if (request.RequestUri?.Host == "secondary.api.com")
            {
                // İkincil hızlı: anında cevap ver
                return new HttpResponseMessage(HttpStatusCode.OK) { ReasonPhrase = "Secondary" };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task MultiEndpointHedgingHandler_ShouldFallbackToSecondaryEndpoint_OnSlowPrimary()
    {
        var hedgingOptions = new MultiEndpointHedgingOptions
        {
            Endpoints = new List<Uri>
            {
                new("https://primary.api.com/v1/test"),
                new("https://secondary.api.com/v1/test")
            },
            HedgingDelay = TimeSpan.FromMilliseconds(50),
            MaxHedgedAttempts = 1
        };

        var hedgingHandler = new MultiEndpointHedgingHandler(hedgingOptions);
        var mockBackend = new MockMultiEndpointHandler();
        typeof(DelegatingHandler)
            .GetProperty("InnerHandler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(hedgingHandler, mockBackend);

        var invoker = new HttpMessageInvoker(hedgingHandler);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://primary.api.com/v1/test");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await invoker.SendAsync(request, CancellationToken.None);
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Secondary", response.ReasonPhrase);
        // Primary 1000ms beklerken ikincil 50ms hedging delay sonrası devreye girip hemen bitirmeli
        Assert.True(sw.ElapsedMilliseconds < 500, $"Hedging süresi çok uzun sürdü: {sw.ElapsedMilliseconds}ms");
    }

    // 9. Weighted Canary Routing Handler
    private sealed class MockCanaryHandler : HttpMessageHandler
    {
        public List<string> ReceivedHosts { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (ReceivedHosts)
            {
                ReceivedHosts.Add(request.RequestUri?.Host ?? "");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task WeightedCanaryHandler_StickySession_ShouldConsistentlyRouteToSameCanaryEndpoint()
    {
        var canaryOptions = new WeightedCanaryOptions
        {
            Endpoints = new List<WeightedEndpoint>
            {
                new(new Uri("https://v1.api.com"), 50),
                new(new Uri("https://v2.api.com"), 50)
            },
            StickySessionKeySelector = req => req.Headers.TryGetValues("X-User-Id", out var vals) ? vals.FirstOrDefault() : null
        };

        var canaryHandler = new WeightedCanaryHandler(canaryOptions);
        var mockBackend = new MockCanaryHandler();
        typeof(DelegatingHandler)
            .GetProperty("InnerHandler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(canaryHandler, mockBackend);

        var invoker = new HttpMessageInvoker(canaryHandler);

        // Belirli bir kullanıcı (User-12345) 5 kez istek atıyor
        for (var i = 0; i < 5; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://gateway.api.com/data");
            req.Headers.Add("X-User-Id", "User-12345");
            await invoker.SendAsync(req, CancellationToken.None);
        }

        // 5 isteğin hepsi aynı hedefe yönlenmiş olmalıdır
        Assert.Equal(5, mockBackend.ReceivedHosts.Count);
        var targetHost = mockBackend.ReceivedHosts[0];
        Assert.All(mockBackend.ReceivedHosts, h => Assert.Equal(targetHost, h));
    }
}
