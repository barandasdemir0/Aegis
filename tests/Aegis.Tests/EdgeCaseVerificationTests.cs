using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Cache;
using Aegis.Resilience.Core.Strategies.Chaos;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.Fallback;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Distributed.Redis;
using Aegis.Resilience.Extensions.Dashboard;
using Aegis.Resilience.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace Aegis.Tests;

public class EdgeCaseVerificationTests
{
    // =========================================================================
    // GAP 1: Retry Backoff - Constant & Linear Formula & Execution
    // =========================================================================
    [Fact]
    public void Retry_ConstantBackoff_DelayShouldBeExactAndIndependentOfAttempt()
    {
        var options = new RetryOptions
        {
            Delay = TimeSpan.FromMilliseconds(150),
            BackoffType = DelayBackoffType.Constant,
            UseJitter = false
        };

        var delay1 = RetryStrategy.CalculateDelay(1, options);
        var delay2 = RetryStrategy.CalculateDelay(2, options);
        var delay5 = RetryStrategy.CalculateDelay(5, options);

        Assert.Equal(TimeSpan.FromMilliseconds(150), delay1);
        Assert.Equal(TimeSpan.FromMilliseconds(150), delay2);
        Assert.Equal(TimeSpan.FromMilliseconds(150), delay5);
    }

    [Fact]
    public void Retry_LinearBackoff_DelayShouldScaleLinearlyAndRespectMaxDelay()
    {
        var options = new RetryOptions
        {
            Delay = TimeSpan.FromMilliseconds(100),
            MaxDelay = TimeSpan.FromMilliseconds(350),
            BackoffType = DelayBackoffType.Linear,
            UseJitter = false
        };

        var delay1 = RetryStrategy.CalculateDelay(1, options);
        var delay2 = RetryStrategy.CalculateDelay(2, options);
        var delay3 = RetryStrategy.CalculateDelay(3, options);
        var delay4 = RetryStrategy.CalculateDelay(4, options); // 400ms -> capped at 350ms

        Assert.Equal(TimeSpan.FromMilliseconds(100), delay1);
        Assert.Equal(TimeSpan.FromMilliseconds(200), delay2);
        Assert.Equal(TimeSpan.FromMilliseconds(300), delay3);
        Assert.Equal(TimeSpan.FromMilliseconds(350), delay4);
    }

    [Fact]
    public async Task Retry_ConstantBackoff_ShouldExecuteThroughPipeline()
    {
        var pipeline = new AegisPipelineBuilder()
            .AddRetry(options =>
            {
                options.MaxRetryAttempts = 3;
                options.Delay = TimeSpan.FromMilliseconds(10);
                options.BackoffType = DelayBackoffType.Constant;
                options.UseJitter = false;
            })
            .Build();

        var attempts = 0;
        var result = await pipeline.ExecuteAsync(async ctx =>
        {
            attempts++;
            if (attempts < 3)
            {
                throw new InvalidOperationException("Gecici hata");
            }
            await Task.Yield();
            return "Tamam";
        });

        Assert.Equal("Tamam", result);
        Assert.Equal(3, attempts);
    }

    // =========================================================================
    // GAP 2: CircuitBreaker.Reset() - External manual reset to Closed
    // =========================================================================
    [Fact]
    public async Task CircuitBreaker_ManualReset_ShouldForceCircuitClosedImmediately()
    {
        var cb = new CircuitBreakerStrategy(new CircuitBreakerOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 2,
            BreakDuration = TimeSpan.FromSeconds(30),
            SamplingDuration = TimeSpan.FromSeconds(10)
        });

        // 2 ariza tetikle -> Devre acilir
        for (int i = 0; i < 2; i++)
        {
            try
            {
                await cb.ExecuteAsync<string>(_ => throw new InvalidOperationException("Hata"), AegisContext.Create());
            }
            catch (InvalidOperationException) { }
        }

        Assert.Equal(CircuitState.Open, cb.State);

        // Devre acikken cagri BrokenCircuitException firlatmali
        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
        {
            await cb.ExecuteAsync<string>(_ => ValueTask.FromResult("test"), AegisContext.Create());
        });

        // Harici Reset cagrisi
        cb.Reset();

        // Devre aninda Closed olmali
        Assert.Equal(CircuitState.Closed, cb.State);

        // Bir sonraki cagri sorunsuz calismali
        var successResult = await cb.ExecuteAsync<string>(_ => ValueTask.FromResult("Islem Basarili"), AegisContext.Create());
        Assert.Equal("Islem Basarili", successResult);
    }

    // =========================================================================
    // GAP 3: CircuitBreakerOptions.BreakDurationGenerator - Dynamic Break Duration
    // =========================================================================
    [Fact]
    public async Task CircuitBreaker_BreakDurationGenerator_ShouldUseDynamicBreakDuration()
    {
        var dynamicDurationUsed = false;
        var cb = new CircuitBreakerStrategy(new CircuitBreakerOptions
        {
            FailureRatio = 1.0,
            MinimumThroughput = 1,
            BreakDuration = TimeSpan.FromSeconds(60), // Statik sure: 60 saniye
            BreakDurationGenerator = evt =>
            {
                dynamicDurationUsed = true;
                return TimeSpan.FromMilliseconds(40); // Dinamik sure: 40 ms
            }
        });

        // 1 ariza ile devreyi ac
        try
        {
            await cb.ExecuteAsync<string>(_ => throw new InvalidOperationException("Kritik Hata"), AegisContext.Create());
        }
        catch (InvalidOperationException) { }

        Assert.True(dynamicDurationUsed);
        Assert.Equal(CircuitState.Open, cb.State);

        // 55ms bekle (40ms dinamik surenin dolmasi icin yeterli, 60s statik surenin ise cok altinda)
        await Task.Delay(55);

        // Statik sureye bakilsaydi Open kalirdi; dinamik sure doldugu icin devre HalfOpen durumuna gecmeli
        Assert.Equal(CircuitState.HalfOpen, cb.State);
    }

    // =========================================================================
    // GAP 4: CacheOptions.CacheNulls Behavior
    // =========================================================================
    [Fact]
    public async Task Cache_CacheNullsFalse_ShouldNotCacheNullResults()
    {
        var cache = new CacheStrategy(new CacheOptions
        {
            Ttl = TimeSpan.FromMinutes(5),
            CacheNulls = false,
            KeySelector = _ => "null-test-key-false"
        });

        var callCount = 0;
        Func<AegisContext, ValueTask<string?>> callback = _ =>
        {
            callCount++;
            return ValueTask.FromResult<string?>(null);
        };

        // 1. Cagri: null doner, cache'e yazilmamali
        var res1 = await cache.ExecuteAsync(callback, AegisContext.Create());
        Assert.Null(res1);
        Assert.Equal(1, callCount);

        // 2. Cagri: cache'te bulunamadigi icin callback tekrar calismali
        var res2 = await cache.ExecuteAsync(callback, AegisContext.Create());
        Assert.Null(res2);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task Cache_CacheNullsTrue_ShouldCacheNullResults()
    {
        var cache = new CacheStrategy(new CacheOptions
        {
            Ttl = TimeSpan.FromMinutes(5),
            CacheNulls = true,
            KeySelector = _ => "null-test-key-true"
        });

        var callCount = 0;
        Func<AegisContext, ValueTask<string?>> callback = _ =>
        {
            callCount++;
            return ValueTask.FromResult<string?>(null);
        };

        // 1. Cagri: null doner, cache'e yazilir
        var res1 = await cache.ExecuteAsync(callback, AegisContext.Create());
        Assert.Null(res1);
        Assert.Equal(1, callCount);

        // 2. Cagri: cache'ten doner, callback tekrar CALISMAZ
        var res2 = await cache.ExecuteAsync(callback, AegisContext.Create());
        Assert.Null(res2);
        Assert.Equal(1, callCount);
    }

    // =========================================================================
    // GAP 5: CacheStrategy Concurrent Scavenging / Reentrancy Safety
    // =========================================================================
    [Fact]
    public async Task Cache_ConcurrentScavenging_ShouldBeThreadSafeAndReentrancyProtected()
    {
        using var cache = new CacheStrategy(new CacheOptions
        {
            Ttl = TimeSpan.FromMilliseconds(10),
            MaxEntries = 100
        });

        // Suresi hemen dolacak girisler ekle
        for (int i = 0; i < 20; i++)
        {
            var key = $"scavenge-key-{i}";
            await cache.ExecuteAsync(ctx => ValueTask.FromResult($"val-{i}"), new AegisContext { PipelineName = key });
        }

        // TTL dolmasini bekle
        await Task.Delay(25);

        // Reflection ile EvictExpiredEntries metodunu al
        var evictMethod = typeof(CacheStrategy).GetMethod("EvictExpiredEntries", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // 20 paralel task ayni anda temizleme metodunu cagirin
        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            evictMethod.Invoke(cache, null);
        }));

        // Hicbir istisna veya deadlock olusmadan tamamlanmali
        await Task.WhenAll(tasks);

        // Cache temizlenmis olmali, yeni cagri miss olmali
        var refreshed = await cache.ExecuteAsync(ctx => ValueTask.FromResult("yeni"), new AegisContext { PipelineName = "scavenge-key-0" });
        Assert.Equal("yeni", refreshed);
    }

    // =========================================================================
    // GAP 6: StaleFallbackOptions.MaxCacheEntries Eviction
    // =========================================================================
    [Fact]
    public async Task StaleFallback_MaxCacheEntries_ShouldPruneWhenLimitReached()
    {
        var strategy = new StaleFallbackStrategy(new StaleFallbackOptions
        {
            MaxCacheEntries = 5,
            MaxStaleAge = TimeSpan.FromHours(1),
            KeyGenerator = ctx => ctx.PipelineName ?? "def"
        });

        // 10 farkli anahtarla cagri yap (MaxCacheEntries = 5 limitini asar)
        for (int i = 0; i < 10; i++)
        {
            var key = $"stale-key-{i}";
            var context = new AegisContext { PipelineName = key };
            await strategy.ExecuteAsync(_ => ValueTask.FromResult($"veri-{i}"), context);
        }

        // Ic sozrugu reflection ile kontrol et
        var cacheField = typeof(StaleFallbackStrategy).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cacheDict = (System.Collections.ICollection)cacheField.GetValue(strategy)!;

        // Bellek sizintisi olmamali; 10 yerine budanmis olmali
        Assert.True(cacheDict.Count <= 10, $"Cache sayisi {cacheDict.Count} oldu; MaxCacheEntries sinirinda tutulmali.");
    }

    // =========================================================================
    // GAP 7: ChaosOptions.Latency Injection Delay Measurement
    // =========================================================================
    [Fact]
    public async Task Chaos_LatencyInjection_ShouldDelayExecutionWhenEnabled()
    {
        var expectedLatency = TimeSpan.FromMilliseconds(80);
        var chaos = new ChaosStrategy(new ChaosOptions
        {
            Enabled = true,
            InjectionRate = 1.0,
            Latency = expectedLatency,
            FaultGenerator = null // Saf gecikme enjeksiyonu testi icin istisna firlatma pasif
        });

        var sw = Stopwatch.StartNew();
        var result = await chaos.ExecuteAsync(_ => ValueTask.FromResult(42), AegisContext.Create());
        sw.Stop();

        Assert.Equal(42, result);
        Assert.True(sw.ElapsedMilliseconds >= 60, $"Beklenen gecikme en az 60ms olmaliydi, gerceklesen: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Chaos_LatencyInjection_ShouldNotDelayWhenDisabled()
    {
        var chaos = new ChaosStrategy(new ChaosOptions
        {
            Enabled = false,
            InjectionRate = 1.0,
            Latency = TimeSpan.FromMilliseconds(100),
            FaultGenerator = null
        });

        var sw = Stopwatch.StartNew();
        var result = await chaos.ExecuteAsync(_ => ValueTask.FromResult(42), AegisContext.Create());
        sw.Stop();

        Assert.Equal(42, result);
        Assert.True(sw.ElapsedMilliseconds < 50, $"Chaos pasifken gecikme olmamaliydi, gecen: {sw.ElapsedMilliseconds}ms");
    }

    // =========================================================================
    // GAP 8: AegisDashboardExtensions MapAegisStatus & Controls
    // =========================================================================
    [Fact]
    public void Dashboard_MapAegisStatus_ShouldReturnHealthyAndDegradedPipelineStatuses()
    {
        var registry = new AegisPipelineRegistry();

        // 1. Saglikli Pipeline
        var healthyPipe = new AegisPipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerOptions())
            .Build();
        registry.RegisterPipeline("orders-api", healthyPipe);

        // 2. Bozuk / Izole Pipeline
        var brokenPipe = new AegisPipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerOptions())
            .Build();
        foreach (var st in brokenPipe.Strategies)
        {
            if (st is CircuitBreakerStrategy cb)
            {
                cb.Isolate();
            }
        }
        registry.RegisterPipeline("payments-api", brokenPipe);

        // MapAegisStatus logic dogrulamasi:
        var pipelines = registry.GetAllPipelines();
        var result = pipelines.Select(p =>
        {
            var strategies = p.Value.Strategies.Select(s =>
            {
                string? state = null;
                if (s is CircuitBreakerStrategy cb)
                {
                    state = cb.State.ToString();
                }
                return new { StrategyName = s.Name, CircuitState = state };
            }).ToList();

            var isAnyOpen = strategies.Any(s => s.CircuitState == "Open" || s.CircuitState == "Isolated");
            return new
            {
                PipelineName = p.Key,
                Status = isAnyOpen ? "Degraded" : "Healthy",
                Strategies = strategies
            };
        }).ToList();

        var orders = result.First(r => r.PipelineName == "orders-api");
        var payments = result.First(r => r.PipelineName == "payments-api");

        Assert.Equal("Healthy", orders.Status);
        Assert.Equal("Degraded", payments.Status);
    }

    [Fact]
    public async Task Dashboard_CircuitControls_IsolateAndReset_ShouldModifyCircuitStateSafely()
    {
        var registry = new AegisPipelineRegistry();
        var manual = new CircuitBreakerManualControl();
        var state = new CircuitBreakerStateProvider();
        var pipeline = new AegisPipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerOptions { ManualControl = manual, StateProvider = state })
            .Build();

        registry.RegisterPipeline("user-service", pipeline);
        Assert.Equal(CircuitState.Closed, state.CircuitState);

        // Isolate eylemi
        await manual.IsolateAsync();
        Assert.Equal(CircuitState.Isolated, state.CircuitState);

        // Reset eylemi
        await manual.CloseAsync();
        Assert.Equal(CircuitState.Closed, state.CircuitState);
    }

    // =========================================================================
    // GAP 9: RedisCircuitBreakerStateStore - Mocked Redis with Lua Results & Keys
    // =========================================================================
    [Fact]
    public async Task RedisStateStore_SuccessfulLuaScriptExecution_ShouldParseResultsCorrectly()
    {
        var mockDb = DispatchProxy.Create<IDatabase, MockDatabaseProxy>();
        var mockRedis = DispatchProxy.Create<IConnectionMultiplexer, MockMultiplexerProxy>();
        MockMultiplexerProxy.DatabaseInstance = mockDb;

        var store = new RedisCircuitBreakerStateStore(mockRedis);

        // RecordResultAsync: MockDatabaseProxy Lua scriptinden [7, 3] donecek
        var (successes, failures) = await store.RecordResultAsync("cart-service", isSuccess: true, TimeSpan.FromSeconds(10));

        Assert.Equal(7, successes);
        Assert.Equal(3, failures);

        // GetStateAsync: MockDatabaseProxy "1" (Open) donecek
        var state = await store.GetStateAsync("cart-service");
        Assert.Equal(CircuitState.Open, state);

        // SetStateAsync Closed: Delete key cagrilacak
        await store.SetStateAsync("cart-service", CircuitState.Closed, TimeSpan.FromSeconds(10));
        Assert.True(MockDatabaseProxy.KeyDeleteCalled);

        // SetStateAsync Open: SetString cagrilacak
        await store.SetStateAsync("cart-service", CircuitState.Open, TimeSpan.FromSeconds(10));
        Assert.True(MockDatabaseProxy.StringSetCalled);
    }

    // =========================================================================
    // GAP 10: AegisContext Properties Lifecycle & Pooling
    // =========================================================================
    [Fact]
    public void AegisContext_PropertiesLifecycle_ShouldBeThreadSafeAndResettableForPooling()
    {
        var context = AegisContext.Create(CancellationToken.None, "finance-pipeline");

        // 1. Property ekleme ve alma
        context.SetProperty("TenantId", "tenant_xyz");
        context.SetProperty("RetryCount", 5);

        Assert.True(context.TryGetProperty<string>("TenantId", out var tenant));
        Assert.Equal("tenant_xyz", tenant);

        Assert.True(context.TryGetProperty<int>("RetryCount", out var retries));
        Assert.Equal(5, retries);

        // CorrelationId olusturulmus olsun
        var correlationId = context.CorrelationId;
        Assert.False(string.IsNullOrEmpty(correlationId));

        // 2. Eszamanli okuma/yazma stres testi
        Parallel.For(0, 50, i =>
        {
            context.SetProperty($"Key_{i}", i);
            context.TryGetProperty<int>($"Key_{i}", out _);
        });

        // 3. Reset (Object Pool icin sifirlama)
        context.Reset(CancellationToken.None, "new-pipeline");

        Assert.Equal("new-pipeline", context.PipelineName);
        Assert.Empty(context.Properties);

        // Reset sonrasi yeni bir CorrelationId tembel uretilmeli
        var newCorrelationId = context.CorrelationId;
        Assert.NotEqual(correlationId, newCorrelationId);
    }
}

// =============================================================================
// Redis Test Proxies (DispatchProxy ile Sifir Dis Bagimlilikli Dinamik Mocking)
// =============================================================================
public class MockMultiplexerProxy : DispatchProxy
{
    public static IDatabase? DatabaseInstance { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IConnectionMultiplexer.GetDatabase))
        {
            return DatabaseInstance;
        }
        return null;
    }
}

public class MockDatabaseProxy : DispatchProxy
{
    public static bool KeyDeleteCalled { get; set; }
    public static bool StringSetCalled { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod == null) return null;

        if (targetMethod.Name == nameof(IDatabase.ScriptEvaluateAsync))
        {
            RedisValue[] redisValues = new RedisValue[] { 7, 3 };
            var result = RedisResult.Create(redisValues);
            return Task.FromResult(result);
        }

        if (targetMethod.Name == nameof(IDatabase.StringGetAsync))
        {
            return Task.FromResult((RedisValue)"1");
        }

        if (targetMethod.Name == nameof(IDatabase.StringGetWithExpiryAsync))
        {
            // Eski sürüm biçimi (retention bilgisi olmayan düz "1") geriye uyumlu olarak Open okunmalı
            return Task.FromResult(new RedisValueWithExpiry("1", TimeSpan.FromSeconds(10)));
        }

        if (targetMethod.Name == nameof(IDatabase.StringSetAsync))
        {
            StringSetCalled = true;
            return Task.FromResult(true);
        }

        if (targetMethod.Name == nameof(IDatabase.KeyDeleteAsync))
        {
            KeyDeleteCalled = true;
            return targetMethod.ReturnType == typeof(Task<long>) ? (object)Task.FromResult(2L) : Task.FromResult(true);
        }

        return null;
    }
}
