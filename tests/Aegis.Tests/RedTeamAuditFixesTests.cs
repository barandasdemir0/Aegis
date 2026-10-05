using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.Collapser;
using Aegis.Resilience.Core.Strategies.Fallback;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Core.Strategies.Timeout;
using Aegis.Resilience.Distributed.Redis;
using Aegis.Resilience.Extensions.Dashboard;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.DependencyInjection.Aop;
using Aegis.Resilience.Extensions.HealthChecks;
using Aegis.Resilience.Extensions.Http;
using Xunit;

namespace Aegis.Tests;

public interface ITestAopService
{
    ValueTask DoVoidValueTaskAsync();
    ValueTask<string> GetStringValueTaskAsync(string input);
    Task<int> GetIntTaskAsync(int number);
    string GetSyncString(string input);
}

public class TestAopService : ITestAopService
{
    public int InvocationCount { get; private set; }

    public ValueTask DoVoidValueTaskAsync()
    {
        InvocationCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask<string> GetStringValueTaskAsync(string input)
    {
        InvocationCount++;
        return ValueTask.FromResult("Processed: " + input);
    }

    public Task<int> GetIntTaskAsync(int number)
    {
        InvocationCount++;
        return Task.FromResult(number * 2);
    }

    public string GetSyncString(string input)
    {
        InvocationCount++;
        return "Sync: " + input;
    }
}

public class RedTeamAuditFixesTests
{
    [Fact]
    public async Task AEGIS_003_DispatchProxy_SupportsValueTask_And_GenericValueTask()
    {
        var services = new ServiceCollection();
        services.AddAegisPipeline("Default", p => p.AddTimeout(TimeSpan.FromSeconds(5)));

        var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IAegisPipelineRegistry>();

        var target = new TestAopService();
        var proxy = AegisDispatchProxy<ITestAopService>.Create(target, registry, "Default");

        // 1. ValueTask (void)
        await proxy.DoVoidValueTaskAsync();
        Assert.Equal(1, target.InvocationCount);

        // 2. ValueTask<string>
        var val = await proxy.GetStringValueTaskAsync("Aegis");
        Assert.Equal("Processed: Aegis", val);
        Assert.Equal(2, target.InvocationCount);

        // 3. Task<int>
        var num = await proxy.GetIntTaskAsync(21);
        Assert.Equal(42, num);
        Assert.Equal(3, target.InvocationCount);

        // 4. Sync string
        var sync = proxy.GetSyncString("Direct");
        Assert.Equal("Sync: Direct", sync);
        Assert.Equal(4, target.InvocationCount);
    }

    [Fact]
    public async Task AEGIS_004_PartitionedRateLimiter_PrunesExcessPartitions_WhenCapacityExceeded()
    {
        var options = new PartitionedRateLimiterOptions
        {
            MaxPartitions = 5,
            PartitionKeySelector = ctx => (string)ctx.Properties["Tenant"]!,
            DefaultOptions = new RateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromSeconds(10) }
        };

        var strategy = new PartitionedRateLimiterStrategy(options);

        // 10 farklı tenant için istek çalıştır (MaxPartitions = 5)
        for (int i = 0; i < 10; i++)
        {
            var ctx = new AegisContext();
            ctx.Properties["Tenant"] = $"tenant_{i}";
            await strategy.ExecuteAsync(_ => ValueTask.FromResult(true), ctx);
        }

        // Kapasite temizliği tetiklenmiş ve sayaç asla 10'da kalmamış olmalı
        Assert.True(strategy.PartitionCount <= 8, $"Partition count ({strategy.PartitionCount}) did not prune excess items.");
    }

    [Fact]
    public async Task AEGIS_005_HealthCheck_Options_And_PipelineFilter()
    {
        var services = new ServiceCollection();
        services.AddAegisPipeline("CriticalDb", p => p.AddCircuitBreaker(new CircuitBreakerOptions { MinimumThroughput = 10 }));
        var metricsControl = new CircuitBreakerManualControl();
        services.AddAegisPipeline("NonCriticalMetrics", p => p.AddCircuitBreaker(new CircuitBreakerOptions { MinimumThroughput = 10, ManualControl = metricsControl }));

        var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IAegisPipelineRegistry>();

        // Non-critical pipeline devresini manuel aç
        _ = registry.GetPipeline("NonCriticalMetrics"); // boru hattı kurulunca devre kontrole kaydolur
        await metricsControl.IsolateAsync();

        // Filtre ile sadece CriticalDb denetlensin
        var options = Options.Create(new AegisHealthCheckOptions
        {
            OpenCircuitStatus = HealthStatus.Degraded,
            PipelineFilter = name => name == "CriticalDb"
        });

        var healthCheck = new AegisHealthCheck(registry, options);
        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        // Non-critical açık olsa bile filtrelendiği için Healthy dönmeli
        Assert.Equal(HealthStatus.Healthy, result.Status);

        // Filtreyi kaldırınca Degraded dönmeli (asla Unhealthy değil)
        var allOptions = Options.Create(new AegisHealthCheckOptions
        {
            OpenCircuitStatus = HealthStatus.Degraded
        });
        var healthCheckAll = new AegisHealthCheck(registry, allOptions);
        var degradedResult = await healthCheckAll.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, degradedResult.Status);
    }

    [Fact]
    public async Task AEGIS_006_RequestCollapser_CancellationIsolation_And_AtomicRemoval()
    {
        var strategy = new RequestCollapserStrategy(new RequestCollapserOptions
        {
            KeySelector = _ => "singleflight_key"
        });

        var executionCount = 0;
        var tcs = new TaskCompletionSource<string>();

        // 1. İstek (Kendi cancellation token'ı iptal edilecek)
        using var cts1 = new CancellationTokenSource();
        var ctx1 = new AegisContext(cts1.Token);

        var task1 = strategy.ExecuteAsync(async _ =>
        {
            Interlocked.Increment(ref executionCount);
            return await tcs.Task;
        }, ctx1);

        // 2. İstek (Aktif bekleyen)
        var ctx2 = new AegisContext(CancellationToken.None);
        var task2 = strategy.ExecuteAsync(async _ =>
        {
            Interlocked.Increment(ref executionCount);
            return await tcs.Task;
        }, ctx2);

        // İlk çağıran iptal ediyor
        cts1.Cancel();

        // task1 OperationCanceledException fırlatmalı
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task1.AsTask());

        // Ortak iş arka planda tamamlanıyor
        tcs.SetResult("SuccessPayload");

        // İkinci çağıran sorunsuz sonucu almalı (Singleflight kırılmamalı!)
        var res2 = await task2;
        Assert.Equal("SuccessPayload", res2);
        Assert.Equal(1, executionCount);
    }

    [Fact]
    public async Task AEGIS_008_AdaptiveConcurrency_RespectsQueueTimeout()
    {
        var options = new AdaptiveConcurrencyOptions
        {
            MinConcurrency = 1,
            MaxConcurrency = 1,
            InitialConcurrency = 1,
            QueueTimeout = TimeSpan.FromMilliseconds(80)
        };

        var strategy = new AdaptiveConcurrencyStrategy(options);
        var tcs = new TaskCompletionSource();
        var startedTcs = new TaskCompletionSource();

        // 1. Kapasiteyi doldur
        var task1 = Task.Run(() => strategy.ExecuteAsync(async _ =>
        {
            startedTcs.SetResult();
            await tcs.Task;
            return 1;
        }, new AegisContext()));

        await startedTcs.Task;

        // 2. Kapasite doluyken ikinci istek gelsin; QueueTimeout sonrasında reddedilmeli
        var task2 = strategy.ExecuteAsync(_ => ValueTask.FromResult(2), new AegisContext());

        await Assert.ThrowsAsync<RateLimitRejectedException>(() => task2.AsTask());

        // Temizle
        tcs.SetResult();
        await task1;
    }

    [Fact]
    public async Task AEGIS_009_StaleFallback_BackgroundRefresh_ReturnsStaleImmediately()
    {
        var options = new StaleFallbackOptions
        {
            MaxStaleAge = TimeSpan.FromMinutes(10),
            FreshnessDuration = TimeSpan.FromMilliseconds(50),
            KeyGenerator = _ => "test_cache"
        };

        var strategy = new StaleFallbackStrategy(options);
        var callCounter = 0;

        // İlk çağrı: Taze veri üretir
        var res1 = await strategy.ExecuteAsync(_ =>
        {
            callCounter++;
            return ValueTask.FromResult("V1");
        }, new AegisContext());

        Assert.Equal("V1", res1);
        Assert.Equal(1, callCounter);

        // FreshnessDuration'ı bekle (veri bayat hale gelsin ama MaxStaleAge içinde kalsın)
        await Task.Delay(80);

        // İkinci çağrı: Kullanıcı anında bayat veriyi (V1) almalı ve bağlamda IsStaleData=true olmalı
        var ctx2 = new AegisContext();
        var res2 = await strategy.ExecuteAsync(async _ =>
        {
            await Task.Delay(40);
            callCounter++;
            return "V2";
        }, ctx2);

        Assert.Equal("V1", res2);
        Assert.True(ctx2.Properties.TryGetValue(StaleFallbackOptions.IsStaleDataKey, out var isStale) && (bool)isStale!);

        // Arka plandaki yenileme görevinin tamamlanmasını bekle
        await Task.Delay(100);

        // 3. Çağrı taze güncellenmiş V2'yi görmeli
        var ctx3 = new AegisContext();
        var res3 = await strategy.ExecuteAsync(_ => ValueTask.FromResult("V3"), ctx3);
        Assert.Equal("V2", res3);
    }

    [Fact]
    public async Task AEGIS_068_HttpRequestReplayHandler_MaxRequestBodySize_ThrowsOnExcess()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/upload")
        {
            Content = new StringContent(new string('X', 5000), Encoding.UTF8, "text/plain")
        };

        // 1KB max sınır belirle, içerik 5000 byte
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HttpRequestReplayHandler.CloneRequestAsync(req, maxRequestBodySize: 1024));
    }

    [Fact]
    public void AEGIS_001_Dashboard_ActionAuthorizationPolicy_IsMapped()
    {
        // CreateBuilder appsettings*.json için FileSystemWatcher kurar; yoğun paralel test yükünde bu izleyici
        // zaman zaman IOException üretebiliyor. Test yalnızca haritalamayı doğruladığı için Slim builder yeterli.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Services.AddAegis();
        builder.Services.AddAuthorization();
        var app = builder.Build();

        // Haritalama sırasında hata almamalı ve action authorization policy kabul etmeli
        app.MapAegisDashboard("/aegis", authorizationPolicy: "ReadOnlyPolicy", actionAuthorizationPolicy: "AdminPolicy");
    }

    [Fact]
    public async Task AEGIS_002_RedisStateStore_FailOpen_WhenRedisUnreachable()
    {
        // Simüle edilmiş Redis arızası (DispatchProxy ile sıfır gecikmeli hata üretimi)
        var redis = DispatchProxy.Create<StackExchange.Redis.IConnectionMultiplexer, FailingRedisProxy>();
        var store = new RedisCircuitBreakerStateStore(redis);

        // Redis çökmüş olsa dahi GetStateAsync exception fırlatmamalı (Fail-open: Closed)
        var state = await store.GetStateAsync("payment-circuit");
        Assert.Equal(CircuitState.Closed, state);

        // SetStateAsync exception fırlatmamalı
        await store.SetStateAsync("payment-circuit", CircuitState.Open, TimeSpan.FromSeconds(10));

        // Yerel hafızaya kaydedilen durum okunabilmeli
        var stateAfterSet = await store.GetStateAsync("payment-circuit");
        Assert.Equal(CircuitState.Open, stateAfterSet);

        // RecordResultAsync exception fırlatmamalı ve yerel sayaçları tutmalı
        var (s1, f1) = await store.RecordResultAsync("payment-circuit", isSuccess: true, TimeSpan.FromSeconds(10));
        Assert.Equal(1, s1);
        Assert.Equal(0, f1);
    }
}

public class FailingRedisProxy : DispatchProxy
{
    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
    {
        throw new TimeoutException("Simüle edilmiş Redis zaman aşımı");
    }
}
