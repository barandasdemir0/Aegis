using System.Diagnostics;
using System.Net.Http.Json;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Distributed.Abstractions;
using Aegis.Resilience.Distributed.Redis;
using Aegis.Resilience.Extensions.Http;
using StackExchange.Redis;

namespace Aegis.Tests;

/// <summary>
/// GERÇEK ağ kaosu testleri: Toxiproxy (Shopify) Redis'in ve bir HTTP arka ucunun (go-httpbin) önüne konur;
/// gerçek TCP soketlerinde gecikme, bağlantı kesme ve olasılıklı bağlantı sıfırlama enjekte edilir.
/// <para>
/// ÇALIŞTIRMA (Docker):
///   docker network create aegis-test
///   docker run -d --name aegis-redis-test --network aegis-test -p 6379:6379 redis:7-alpine
///   docker run -d --name aegis-httpbin --network aegis-test mccutchen/go-httpbin
///   docker run -d --name aegis-toxiproxy --network aegis-test -p 8474:8474 -p 26379:26379 -p 28080:28080 -p 28081:28081 ghcr.io/shopify/toxiproxy:2.12.0
///   set AEGIS_TEST_TOXIPROXY=127.0.0.1
/// Değişken yoksa testler ATLANIR.
/// </para>
/// </summary>
[Collection("Toxiproxy")] // proxy'ler paylaşılan durumdur; testler sırayla koşar
public class ToxiproxyChaosTests : IAsyncLifetime
{
    private static readonly string? Host = Environment.GetEnvironmentVariable("AEGIS_TEST_TOXIPROXY");
    private readonly HttpClient _api = new() { BaseAddress = new Uri($"http://{Host ?? "127.0.0.1"}:8474/") };
    private readonly string _prefix = $"aegischaos:{Guid.NewGuid():N}:";

    private static bool Available => !string.IsNullOrWhiteSpace(Host);

    public async Task InitializeAsync()
    {
        if (!Available)
        {
            return;
        }

        await ResetProxyAsync("redis", "0.0.0.0:26379", "aegis-redis-test:6379");
        await ResetProxyAsync("http-a", "0.0.0.0:28080", "aegis-httpbin:8080");
        await ResetProxyAsync("http-b", "0.0.0.0:28081", "aegis-httpbin:8080");
    }

    public async Task DisposeAsync()
    {
        if (Available)
        {
            foreach (var proxy in new[] { "redis", "http-a", "http-b" })
            {
                await _api.DeleteAsync($"proxies/{proxy}");
            }
        }

        _api.Dispose();
    }

    // =====================================================================
    // REDIS — yavaş Redis (en sinsi arıza: çökmez, sadece yavaşlar)
    // =====================================================================
    [SkippableFact]
    public async Task SlowRedis_DistributedBreakerCall_DoesNotInheritRedisLatency()
    {
        Skip.IfNot(Available, "AEGIS_TEST_TOXIPROXY tanımlı değil - test atlandı.");

        using var mux = await ConnectViaProxyAsync();
        var breaker = Breaker(new RedisCircuitBreakerStateStore(mux, _prefix));
        Assert.Equal(1, await breaker.ExecuteAsync(_ => ValueTask.FromResult(1), new AegisContext()));

        await AddToxicAsync("redis", "yavas", "latency", new { latency = 1500, jitter = 0 });

        // Her çağrı depoya 2 kez gider (durum oku + sonuç yaz). Depo yavaşsa devre kesici tüm trafiği yavaşlatmamalı.
        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            Assert.Equal(2, await breaker.ExecuteAsync(_ => ValueTask.FromResult(2), new AegisContext()));
            Assert.True(sw.ElapsedMilliseconds < 1000, $"Redis 1,5 sn yavaşken çağrı {sw.ElapsedMilliseconds} ms sürdü");
        }
    }

    [SkippableFact]
    public async Task RedisConnectionCut_FailsOpenFast_AndRecoversWhenRestored()
    {
        Skip.IfNot(Available, "AEGIS_TEST_TOXIPROXY tanımlı değil - test atlandı.");

        using var mux = await ConnectViaProxyAsync();
        var store = new RedisCircuitBreakerStateStore(mux, _prefix);
        var breaker = Breaker(store);
        Assert.Equal(1, await breaker.ExecuteAsync(_ => ValueTask.FromResult(1), new AegisContext()));

        await SetProxyEnabledAsync("redis", false); // TCP bağlantıları kesilir, yenileri reddedilir
        await WaitUntilAsync(() => !store.IsAvailable, TimeSpan.FromSeconds(15));

        var sw = Stopwatch.StartNew();
        Assert.Equal(2, await breaker.ExecuteAsync(_ => ValueTask.FromResult(2), new AegisContext()));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Redis kesikken çağrı {sw.ElapsedMilliseconds} ms sürdü");
        Assert.False(breaker.IsSharedStateAvailable); // health check bunu Degraded raporlar

        await SetProxyEnabledAsync("redis", true);
        await WaitUntilAsync(() => store.IsAvailable, TimeSpan.FromSeconds(20)); // çoklayıcı kendiliğinden yeniden bağlanır
        Assert.True(breaker.IsSharedStateAvailable);
    }

    // =====================================================================
    // HTTP — gerçek soketlerde yavaş arka uç, bağlantı sıfırlama, hedging
    // =====================================================================
    [SkippableFact]
    public async Task SlowHttpBackend_AttemptTimeout_CutsRealSocketsOnTime()
    {
        Skip.IfNot(Available, "AEGIS_TEST_TOXIPROXY tanımlı değil - test atlandı.");

        await AddToxicAsync("http-a", "yavas", "latency", new { latency = 5000, jitter = 0 });
        var attempts = 0;
        using var pipeline = new AegisPipelineBuilder("yavas-http")
            .AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.Zero; o.OnRetry = _ => { attempts++; return default; }; })
            .AddTimeout(TimeSpan.FromMilliseconds(300))
            .Build();
        using var client = new HttpClient(new AegisResilienceHandler(pipeline) { InnerHandler = new SocketsHttpHandler() });

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<AegisTimeoutException>(() => client.GetAsync($"http://{Host}:28080/get"));
        sw.Stop();

        Assert.Equal(2, attempts); // 3 deneme, her biri 300 ms'de kesildi
        Assert.InRange(sw.ElapsedMilliseconds, 850, 2500); // 5 sn'lik gecikme BEKLENMEDİ
    }

    [SkippableFact]
    public async Task ProbabilisticConnectionResets_IdempotentGet_RetryRecovers()
    {
        Skip.IfNot(Available, "AEGIS_TEST_TOXIPROXY tanımlı değil - test atlandı.");

        // Bağlantıların %30'unda yanıt 20. bayttan sonra kesilir (başlıklar yarımken). reset_peer .NET'in kendi iç
        // yeniden denemesine takılıp Aegis'e hiç ulaşmayabiliyordu; yarım yanıtı SocketsHttpHandler gizleyemez.
        await AddToxicAsync("http-a", "kes", "limit_data", new { bytes = 20 }, toxicity: 0.3);
        var retries = 0;
        using var pipeline = new AegisPipelineBuilder("rst-http")
            .AddRetry(o => { o.MaxRetryAttempts = 10; o.Delay = TimeSpan.FromMilliseconds(10); o.OnRetry = _ => { retries++; return default; }; })
            .Build();
        // Her istek yeni TCP bağlantısı açsın (Connection: close): %30 olasılık bağlantı başına uygulanır
        using var client = new HttpClient(new AegisResilienceHandler(pipeline) { InnerHandler = new SocketsHttpHandler() });
        client.DefaultRequestHeaders.ConnectionClose = true;

        for (var i = 0; i < 30; i++)
        {
            using var response = await client.GetAsync($"http://{Host}:28080/get");
            Assert.True(response.IsSuccessStatusCode);
        }

        Assert.True(retries > 0, "Toxiproxy hiç sıfırlama üretmedi; test anlamsız kaldı");
    }

    [SkippableFact]
    public async Task MultiEndpointHedging_SlowPrimaryOverRealNetwork_FastSecondaryWins()
    {
        Skip.IfNot(Available, "AEGIS_TEST_TOXIPROXY tanımlı değil - test atlandı.");

        await AddToxicAsync("http-a", "yavas", "latency", new { latency = 3000, jitter = 0 });
        var handler = new MultiEndpointHedgingHandler(new MultiEndpointHedgingOptions
        {
            Endpoints = [new Uri($"http://{Host}:28080"), new Uri($"http://{Host}:28081")],
            HedgingDelay = TimeSpan.FromMilliseconds(200),
            MaxHedgedAttempts = 1
        })
        { InnerHandler = new SocketsHttpHandler() };
        using var client = new HttpClient(handler);

        var sw = Stopwatch.StartNew();
        using var response = await client.GetAsync($"http://{Host}:28080/get");
        sw.Stop();

        Assert.True(response.IsSuccessStatusCode);
        Assert.True(sw.ElapsedMilliseconds < 1500, $"Hedging yanıtı {sw.ElapsedMilliseconds} ms sürdü (yavaş uç 3000 ms)");
    }

    // ---------------------------------------------------------------------
    private static DistributedCircuitBreakerStrategy Breaker(ICircuitBreakerStateStore store) => new(store, new DistributedCircuitBreakerOptions
    {
        CircuitKey = $"kaos-{Guid.NewGuid():N}",
        StateCacheDuration = TimeSpan.Zero // her çağrı depoya gitsin (en kötü durum)
    });

    private static async Task<IConnectionMultiplexer> ConnectViaProxyAsync()
    {
        var options = ConfigurationOptions.Parse($"{Host}:26379");
        options.AbortOnConnectFail = false;
        options.BacklogPolicy = BacklogPolicy.FailFast;
        return await ConnectionMultiplexer.ConnectAsync(options);
    }

    private async Task ResetProxyAsync(string name, string listen, string upstream)
    {
        await _api.DeleteAsync($"proxies/{name}");
        (await _api.PostAsJsonAsync("proxies", new { name, listen, upstream, enabled = true })).EnsureSuccessStatusCode();
    }

    private async Task AddToxicAsync(string proxy, string name, string type, object attributes, double toxicity = 1.0) =>
        (await _api.PostAsJsonAsync($"proxies/{proxy}/toxics", new { name, type, stream = "downstream", toxicity, attributes }))
            .EnsureSuccessStatusCode();

    private async Task SetProxyEnabledAsync(string proxy, bool enabled) =>
        (await _api.PostAsJsonAsync($"proxies/{proxy}", new { enabled })).EnsureSuccessStatusCode();

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Koşul zaman aşımında sağlanmadı");
            await Task.Delay(100);
        }
    }
}
