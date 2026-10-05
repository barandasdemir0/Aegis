using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Distributed.Abstractions;
using Aegis.Resilience.Distributed.Redis;
using StackExchange.Redis;

namespace Aegis.Tests;

/// <summary>
/// GERÇEK Redis sunucusuna karşı çalışan entegrasyon testleri.
/// Şimdiye kadar dağıtık Circuit Breaker yalnızca InMemory depo ile test edilmişti;
/// Lua script'i, TTL davranışı ve çoklu-pod senkronizasyonu gerçek Redis'te hiç doğrulanmamıştı.
/// <para>
/// ÇALIŞTIRMA: Redis gerekir. Docker ile:
///   docker run -d --name aegis-redis-test -p 6379:6379 redis:7-alpine
/// Redis yoksa testler otomatik olarak ATLANIR (Skip) — CI'da kırmızıya düşmez.
/// Farklı adres için AEGIS_TEST_REDIS ortam değişkenini kullanın (örn. "localhost:6380").
/// </para>
/// </summary>
public class RealRedisIntegrationTests : IDisposable
{
    private static readonly string RedisConnectionString =
        Environment.GetEnvironmentVariable("AEGIS_TEST_REDIS") ?? "localhost:6379";

    private readonly IConnectionMultiplexer? _redis;
    private readonly string _keyPrefix = $"aegistest:{Guid.NewGuid():N}:";

    public RealRedisIntegrationTests()
    {
        try
        {
            var config = ConfigurationOptions.Parse(RedisConnectionString);
            config.ConnectTimeout = 2000;
            config.AbortOnConnectFail = false;
            var mux = ConnectionMultiplexer.Connect(config);
            _redis = mux.IsConnected ? mux : null;
        }
        catch
        {
            _redis = null; // Redis yok -> testler atlanacak
        }
    }

    private bool RedisAvailable => _redis is { IsConnected: true };

    // Bu testler Redis HIZLIYKEN geçerli olan katı semantiği (tekillik, paylaşım) ölçer. Varsayılan 250 ms işlem sınırı,
    // 1 CPU konteynerde + paralel yükte aşılıp tasarım gereği fail-open'a düşebiliyordu (yavaş Redis ayrı testte ölçülür).
    private static readonly TimeSpan StrictTimeout = TimeSpan.FromSeconds(5);

    public void Dispose() => _redis?.Dispose();

    [SkippableFact]
    public async Task RedisStore_SetAndGetState_ShouldPersistAcrossStoreInstances()
    {
        Skip.IfNot(RedisAvailable, $"Redis bulunamadi ({RedisConnectionString}) - test atlandi.");

        // İKİ AYRI store örneği = iki ayrı pod
        var podA = new RedisCircuitBreakerStateStore(_redis!, _keyPrefix, StrictTimeout);
        var podB = new RedisCircuitBreakerStateStore(_redis!, _keyPrefix, StrictTimeout);

        var key = "odeme-servisi";

        Assert.Equal(CircuitState.Closed, await podA.GetStateAsync(key));
        Assert.Equal(CircuitState.Closed, await podB.GetStateAsync(key));

        // Pod A devreyi açıyor
        await podA.SetStateAsync(key, CircuitState.Open, TimeSpan.FromSeconds(30));

        // Pod B, KENDİ belleğinde hiçbir şey yokken Redis'ten okuyup Open görmeli
        Assert.Equal(CircuitState.Open, await podB.GetStateAsync(key));

        // Pod B kapatıyor -> Pod A da görmeli
        await podB.SetStateAsync(key, CircuitState.Closed, TimeSpan.Zero);
        Assert.Equal(CircuitState.Closed, await podA.GetStateAsync(key));
    }

    [SkippableFact]
    public async Task RedisStore_LuaScript_ShouldAtomicallyAccumulateCounters()
    {
        Skip.IfNot(RedisAvailable, $"Redis bulunamadi ({RedisConnectionString}) - test atlandi.");

        var store = new RedisCircuitBreakerStateStore(_redis!, _keyPrefix, StrictTimeout);
        var key = $"sayac-{Guid.NewGuid():N}";
        var window = TimeSpan.FromSeconds(30);

        var (s1, f1) = await store.RecordResultAsync(key, isSuccess: true, window);
        Assert.Equal(1, s1);
        Assert.Equal(0, f1);

        var (s2, f2) = await store.RecordResultAsync(key, isSuccess: false, window);
        Assert.Equal(1, s2);
        Assert.Equal(1, f2);

        var (s3, f3) = await store.RecordResultAsync(key, isSuccess: false, window);
        Assert.Equal(1, s3);
        Assert.Equal(2, f3);
    }

    [SkippableFact]
    public async Task RedisStore_LuaScript_ShouldBeAtomicUnderConcurrentLoad()
    {
        Skip.IfNot(RedisAvailable, $"Redis bulunamadi ({RedisConnectionString}) - test atlandi.");

        var store = new RedisCircuitBreakerStateStore(_redis!, _keyPrefix, StrictTimeout);
        var key = $"esamanli-{Guid.NewGuid():N}";
        var window = TimeSpan.FromSeconds(60);

        // 200 eşzamanlı kayıt: 100 başarı + 100 hata. Lua atomik değilse sayaçlar tutmaz.
        var tasks = Enumerable.Range(0, 200)
            .Select(i => store.RecordResultAsync(key, isSuccess: i % 2 == 0, window).AsTask())
            .ToArray();

        var results = await Task.WhenAll(tasks);
        var final = results.OrderByDescending(r => r.SuccessCount + r.FailureCount).First();

        Assert.Equal(100, final.SuccessCount);
        Assert.Equal(100, final.FailureCount);
    }

    [SkippableFact]
    public async Task RedisStore_OpenState_ShouldExpireAutomatically_ViaRedisTtl()
    {
        Skip.IfNot(RedisAvailable, $"Redis bulunamadi ({RedisConnectionString}) - test atlandi.");

        var store = new RedisCircuitBreakerStateStore(_redis!, _keyPrefix, StrictTimeout);
        var key = $"ttl-{Guid.NewGuid():N}";

        await store.SetStateAsync(key, CircuitState.Open, TimeSpan.FromSeconds(2));
        Assert.Equal(CircuitState.Open, await store.GetStateAsync(key));

        // 1.0.5 (AEGIS-147): Açılma süresi dolunca devre HalfOpen olur — Closed DEĞİL. Eskiden anahtar silinip Closed
        // okunuyor, tüm pod'lar tek deneme yapmadan tam trafiğe dönüyordu. Kapanış yalnızca başarılı denemeyle olur.
        await Task.Delay(2500);
        Assert.Equal(CircuitState.HalfOpen, await store.GetStateAsync(key));

        await store.SetStateAsync(key, CircuitState.Closed, TimeSpan.Zero);
        Assert.Equal(CircuitState.Closed, await store.GetStateAsync(key));
    }

    [SkippableFact]
    public async Task RedisStore_ProbeLease_IsExclusiveAcrossPods_AndOwnerChecked()
    {
        Skip.IfNot(RedisAvailable, $"Redis bulunamadi ({RedisConnectionString}) - test atlandi.");

        var podA = new RedisCircuitBreakerStateStore(_redis!, _keyPrefix, StrictTimeout);
        var podB = new RedisCircuitBreakerStateStore(_redis!, _keyPrefix, StrictTimeout);
        var key = $"probe-{Guid.NewGuid():N}";

        Assert.True(await podA.TryAcquireProbeAsync(key, "A", TimeSpan.FromSeconds(30)));
        Assert.False(await podB.TryAcquireProbeAsync(key, "B", TimeSpan.FromSeconds(30)));

        // Sahibi olmayan pod hakkı bırakamaz
        await podB.ReleaseProbeAsync(key, "B");
        Assert.False(await podB.TryAcquireProbeAsync(key, "B", TimeSpan.FromSeconds(30)));

        await podA.ReleaseProbeAsync(key, "A");
        Assert.True(await podB.TryAcquireProbeAsync(key, "B", TimeSpan.FromSeconds(30)));
    }

    [SkippableFact]
    public async Task DistributedCircuitBreaker_OverRealRedis_HalfOpenAllowsSingleProbeAcrossPods()
    {
        Skip.IfNot(RedisAvailable, $"Redis bulunamadi ({RedisConnectionString}) - test atlandi.");

        var circuitKey = $"tek-deneme-{Guid.NewGuid():N}";
        DistributedCircuitBreakerStrategy Pod() => new(new RedisCircuitBreakerStateStore(_redis!, _keyPrefix, StrictTimeout), new DistributedCircuitBreakerOptions
        {
            CircuitKey = circuitKey,
            MinimumThroughput = 1,
            FailureRatio = 1,
            BreakDuration = TimeSpan.FromSeconds(1),
            StateCacheDuration = TimeSpan.Zero
        });
        var podA = Pod();
        var podB = Pod();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await podA.ExecuteAsync<int>(_ => throw new InvalidOperationException("down"), new AegisContext()));
        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
            await podB.ExecuteAsync(_ => ValueTask.FromResult(1), new AegisContext()));

        await Task.Delay(1300);

        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = podA.ExecuteAsync(async _ => { probeStarted.SetResult(); return await gate.Task; }, new AegisContext()).AsTask();

        // Pod A'nın geri çağrısı başladıysa deneme hakkını ALMIŞTIR. Bunu beklemeden B'yi çağırmak yarış olurdu:
        // A, Redis'ten durumu okurken B hakkı önce kapıp deneme isteğini kendisi yapabilir (tekillik yine korunur,
        // ama "hakkı A aldı" varsayımı bozulur — bu test 1.0.5'te bu yüzden aralıklı düşüyordu).
        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Pod A deneme yaparken Pod B'nin isteği reddedilmeli (küme çapında tek deneme)
        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
            await podB.ExecuteAsync(_ => ValueTask.FromResult(2), new AegisContext()));

        gate.SetResult(42);
        Assert.Equal(42, await probe);
        Assert.Equal(7, await podB.ExecuteAsync(_ => ValueTask.FromResult(7), new AegisContext()));
    }

    [SkippableFact]
    public async Task AddAegisRedisStateStore_ConnectsInBackground_ThenSharesStateWithOtherPods()
    {
        Skip.IfNot(RedisAvailable, $"Redis bulunamadi ({RedisConnectionString}) - test atlandi.");

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddAegisRedisStateStore(RedisConnectionString, _keyPrefix);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var diStore = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ICircuitBreakerStateStore>(provider);

        var otherPod = new RedisCircuitBreakerStateStore(_redis!, _keyPrefix, StrictTimeout);
        var key = $"arka-plan-{Guid.NewGuid():N}";
        await otherPod.SetStateAsync(key, CircuitState.Open, TimeSpan.FromSeconds(30));

        // Bağlantı arka planda kurulunca DI deposu diğer pod'un yazdığı durumu görmeli
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var seen = CircuitState.Closed;
        while (DateTime.UtcNow < deadline && (seen = await diStore.GetStateAsync(key)) != CircuitState.Open)
        {
            await Task.Delay(50);
        }

        Assert.Equal(CircuitState.Open, seen);
        Assert.True(((RedisCircuitBreakerStateStore)diStore).IsAvailable); // AEGIS-160: bağlantı kurulunca erişilebilir raporlanır
    }

    [SkippableFact]
    public async Task DistributedCircuitBreaker_OverRealRedis_ConcurrentStormAcrossPods_ExactlyOneProbeExecutes()
    {
        Skip.IfNot(RedisAvailable, $"Redis bulunamadi ({RedisConnectionString}) - test atlandi.");

        var circuitKey = $"firtina-{Guid.NewGuid():N}";
        var pods = Enumerable.Range(0, 3).Select(_ => new DistributedCircuitBreakerStrategy(
            new RedisCircuitBreakerStateStore(_redis!, _keyPrefix, StrictTimeout),
            new DistributedCircuitBreakerOptions
            {
                CircuitKey = circuitKey,
                MinimumThroughput = 1,
                FailureRatio = 1,
                BreakDuration = TimeSpan.FromSeconds(1),
                StateCacheDuration = TimeSpan.Zero
            })).ToArray();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pods[0].ExecuteAsync<int>(_ => throw new InvalidOperationException("down"), new AegisContext()));
        await Task.Delay(1300);

        // 3 pod'dan AYNI ANDA 30 çağrı: kim kazanırsa kazansın yalnızca TEK geri çağrı çalışmalı
        var executions = 0;
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, 30).Select(i => Task.Run(async () =>
        {
            try
            {
                return await pods[i % 3].ExecuteAsync(async _ => { Interlocked.Increment(ref executions); return await gate.Task; }, new AegisContext());
            }
            catch (BrokenCircuitException)
            {
                return -1;
            }
        })).ToArray();

        await Task.Delay(500); // tüm çağrılar karar noktasını geçsin
        gate.SetResult(1);
        var results = await Task.WhenAll(calls);

        Assert.Equal(1, executions);
        Assert.Equal(1, results.Count(r => r == 1));
        Assert.Equal(29, results.Count(r => r == -1));
    }

    [SkippableFact]
    public async Task DistributedCircuitBreaker_OverRealRedis_ShouldShareStateAcrossPods()
    {
        Skip.IfNot(RedisAvailable, $"Redis bulunamadi ({RedisConnectionString}) - test atlandi.");

        var store = new RedisCircuitBreakerStateStore(_redis!, _keyPrefix, StrictTimeout);
        var circuitKey = $"gercek-redis-{Guid.NewGuid():N}";

        IAegisPipeline BuildPod(string name)
        {
            IAegisPipelineBuilder b = new AegisPipelineBuilder(name);
            return b.AddDistributedCircuitBreaker(store, o =>
            {
                o.CircuitKey = circuitKey;
                o.MinimumThroughput = 2;
                o.FailureRatio = 0.5;
                o.BreakDuration = TimeSpan.FromSeconds(10);
                o.StateCacheDuration = TimeSpan.Zero; // her çağrıda Redis'e sor
            }).Build();
        }

        using var podA = BuildPod("redis-pod-a");
        using var podB = BuildPod("redis-pod-b");

        // Pod A üzerinde devreyi aç
        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<HttpRequestException>(async () =>
                await podA.ExecuteAsync<string>(_ => throw new HttpRequestException("downstream coktu"),
                    new AegisContext()));
        }

        // Pod B hiç hata görmedi ama GERÇEK REDIS üzerinden paylaşılan durumu okuyup fail-fast yapmalı
        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
            await podB.ExecuteAsync(_ => ValueTask.FromResult("calismamali"), new AegisContext()));
    }

    [SkippableFact]
    public async Task RedisStore_WhenRedisUnreachable_ShouldFailOpenNotThrow()
    {
        Skip.IfNot(RedisAvailable, "Redis bulunamadi - test atlandi.");

        // Kasıtlı olarak ulaşılamaz bir adrese bağlan
        var badConfig = ConfigurationOptions.Parse("localhost:6399");
        badConfig.ConnectTimeout = 500;
        badConfig.AbortOnConnectFail = false;
        using var badMux = ConnectionMultiplexer.Connect(badConfig);

        var store = new RedisCircuitBreakerStateStore(badMux, "aegis-unreachable:");

        // Redis erişilemezken uygulamayı ÇÖKERTMEMELİ, fail-open davranmalı
        var state = await store.GetStateAsync("herhangi");
        Assert.Equal(CircuitState.Closed, state);

        await store.SetStateAsync("herhangi", CircuitState.Open, TimeSpan.FromSeconds(5)); // patlamamalı

        var (s, f) = await store.RecordResultAsync("herhangi", isSuccess: false, TimeSpan.FromSeconds(10));
        Assert.True(s + f >= 1); // yerel fallback sayacı devreye girmeli
    }
}
