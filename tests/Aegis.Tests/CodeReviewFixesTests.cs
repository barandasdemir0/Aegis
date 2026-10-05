using System.Diagnostics;
using System.Net;
using System.Reflection;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.Collapser;
using Aegis.Resilience.Distributed.Abstractions;
using Aegis.Resilience.Distributed.Redis;
using Aegis.Resilience.Extensions.Http;
using StackExchange.Redis;

namespace Aegis.Tests;

/// <summary>
/// 1.0.5 kod incelemesi (code-review) bulgularının kanıt testleri — AEGIS-147..156.
/// Her test, eski kodda başarısız olan somut senaryoyu yeniden üretir.
/// </summary>
public class CodeReviewFixesTests
{
    // =====================================================================
    // AEGIS-147 — Redis deposu HalfOpen üretir (TTL dolunca Closed'a düşmez)
    // =====================================================================
    [Theory]
    [InlineData("1|600000", 660_000, CircuitState.Open)]     // retention (10 dk) + 1 dk kaldı -> hâlâ açık
    [InlineData("1|600000", 600_001, CircuitState.Open)]     // 1 ms kala hâlâ açık (PTTL ms hassasiyetli)
    [InlineData("1|600000", 590_000, CircuitState.HalfOpen)] // açılma süresi doldu -> HalfOpen
    [InlineData("1|600000", null, CircuitState.HalfOpen)]    // TTL bilgisi yok -> güvenli taraf: HalfOpen
    [InlineData("1", 5_000, CircuitState.Open)]              // eski sürüm biçimi geriye uyumlu
    [InlineData("3", null, CircuitState.Isolated)]
    [InlineData("çöp", 5_000, CircuitState.Closed)]
    [InlineData("99", 5_000, CircuitState.Closed)]           // tanımsız enum değeri
    public void RedisStore_Decode_DerivesHalfOpenFromRedisTtl(string raw, int? expiryMs, CircuitState expected)
    {
        TimeSpan? expiry = expiryMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null;
        Assert.Equal(expected, RedisCircuitBreakerStateStore.Decode(raw, expiry, out _));
    }

    [Fact]
    public void RedisStore_Decode_EmptyValue_IsClosed()
    {
        Assert.Equal(CircuitState.Closed, RedisCircuitBreakerStateStore.Decode(RedisValue.Null, null, out _));
    }

    // =====================================================================
    // AEGIS-148 — Redis kesintisinde yerel Open kalıcı değildir (fail-closed olmaz)
    // =====================================================================
    [Fact]
    public async Task RedisStore_WhenRedisDown_LocalOpenState_BecomesHalfOpenAfterBreak()
    {
        var store = new RedisCircuitBreakerStateStore(DispatchProxy.Create<IConnectionMultiplexer, UnreachableRedisProxy>());

        await store.SetStateAsync("odeme", CircuitState.Open, TimeSpan.FromMilliseconds(100));
        Assert.Equal(CircuitState.Open, await store.GetStateAsync("odeme"));

        await Task.Delay(200);
        // Eskiden: Redis geri gelene kadar sonsuza dek Open -> tüm trafik reddediliyordu
        Assert.Equal(CircuitState.HalfOpen, await store.GetStateAsync("odeme"));

        // Fail-open: Redis yokken deneme hakkı pod içinde verilir
        Assert.True(await store.TryAcquireProbeAsync("odeme", "pod-1", TimeSpan.FromSeconds(5)));
        await store.SetStateAsync("odeme", CircuitState.Closed, TimeSpan.Zero);
        Assert.Equal(CircuitState.Closed, await store.GetStateAsync("odeme"));
    }

    // =====================================================================
    // AEGIS-157 — Redis yokken başlayan uygulama: depo oluşur, fail-open çalışır (Kubernetes başlangıç sırası)
    // =====================================================================
    [Fact]
    public async Task AddAegisRedisStateStore_RedisUnreachableAtStartup_StoreStillResolvesAndFailsOpen()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddAegisRedisStateStore("127.0.0.1:1,connectTimeout=300");
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);

        // Eskiden: RedisConnectionException (abortConnect=true) -> her istekte HTTP 500; ardından senkron bağlantı
        // ilk isteği saniyelerce bekletiyordu. Çözümleme dahil her şey anında olmalı.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var store = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ICircuitBreakerStateStore>(provider);
        Assert.IsType<RedisCircuitBreakerStateStore>(store);

        Assert.Equal(CircuitState.Closed, await store.GetStateAsync("odeme"));
        await store.SetStateAsync("odeme", CircuitState.Open, TimeSpan.FromSeconds(30));
        Assert.Equal(CircuitState.Open, await store.GetStateAsync("odeme")); // yerel yedek durum
        sw.Stop();

        // Fail-open ANINDA olmalı: komutlar bağlantı gelene kadar kuyrukta bekletilmemeli (eskiden işlem başına ~5 sn)
        Assert.True(sw.ElapsedMilliseconds < 500, $"Kesinti sırasında çözümleme + 3 depo işlemi {sw.ElapsedMilliseconds} ms sürdü");

        // AEGIS-160: bağlantı yokken depo bunu dışarı bildirir (health check 'Degraded' raporlar)
        Assert.False(store.IsAvailable);
    }

    // =====================================================================
    // AEGIS-159 — StaleFallback: askıdayken tamamlanan yenilemeden sonra İKİNCİ yenileme başlamaz
    // (1 CPU / 512 MB Linux konteynerde görüldü; burada kancayla deterministik üretilir)
    // =====================================================================
    [Fact]
    public async Task StaleFallback_RefreshCompletedWhileCallerPaused_DoesNotStartSecondRefresh()
    {
        var refreshes = 0;
        var strategy = new Aegis.Resilience.Core.Strategies.Fallback.StaleFallbackStrategy(new Aegis.Resilience.Core.Strategies.Fallback.StaleFallbackOptions
        {
            FreshnessDuration = TimeSpan.FromMilliseconds(50),
            MaxStaleAge = TimeSpan.FromMinutes(10)
        });
        await strategy.ExecuteAsync(_ => ValueTask.FromResult("v0"), new AegisContext());
        await Task.Delay(100); // girdi bayatladı

        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseNext = 1;
        strategy.AfterStaleReadForTesting = async () =>
        {
            if (Interlocked.Exchange(ref pauseNext, 0) == 1)
            {
                paused.SetResult();
                await resume.Task;
            }
        };

        // A: bayat girdiyi okur ve tam yenileme hakkını almadan önce askıya alınır
        var a = strategy.ExecuteAsync(_ => { Interlocked.Increment(ref refreshes); return ValueTask.FromResult("vA"); }, new AegisContext()).AsTask();
        await paused.Task;

        // B: aynı bayat girdiyi görür, yenilemeyi başlatır; yenileme biter, önbellek tazelenir, hak bırakılır
        Assert.Equal("v0", await strategy.ExecuteAsync(_ => { Interlocked.Increment(ref refreshes); return ValueTask.FromResult("vB"); }, new AegisContext()));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref refreshes) == 0 && DateTime.UtcNow < deadline) await Task.Delay(5);
        await Task.Delay(30); // arka plan görevinin kaydetme + hak bırakma adımları

        // A devam eder: eski anlık görüntüye bakıp ikinci yenilemeyi BAŞLATMAMALI
        resume.SetResult();
        Assert.Equal("v0", await a);
        await Task.Delay(100);

        Assert.Equal(1, refreshes); // eskiden: 2
    }

    // =====================================================================
    // AEGIS-160 — Paylaşılan depoya ulaşılamıyorsa health check SESSİZ kalmaz (split-brain görünür olur)
    // (Kubernetes'te yanlış Redis adresiyle görüldü: pod'lar durum paylaşmıyordu, /health yine Healthy diyordu)
    // =====================================================================
    [Fact]
    public async Task HealthCheck_DistributedBreakerWithUnreachableStore_ReportsDegradedNotHealthy()
    {
        var registry = new Aegis.Resilience.Extensions.DependencyInjection.AegisPipelineRegistry();
        var store = new UnavailableStore();
        registry.RegisterPipeline("odeme", new AegisPipelineBuilder("odeme")
            .AddStrategy(new DistributedCircuitBreakerStrategy(store, new DistributedCircuitBreakerOptions())).Build());
        var check = new Aegis.Resilience.Extensions.HealthChecks.AegisHealthCheck(registry);

        var result = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded, result.Status);
        Assert.Contains("pod-yerel", result.Description);
        Assert.Contains("odeme:sharedState", result.Data.Keys);

        store.Available = true;
        result = await check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy, result.Status);
    }

    private sealed class UnavailableStore : ICircuitBreakerStateStore
    {
        private readonly InMemoryCircuitBreakerStateStore _inner = new();
        public bool Available { get; set; }
        public bool IsAvailable => Available;
        public ValueTask<CircuitState> GetStateAsync(string k, CancellationToken ct = default) => _inner.GetStateAsync(k, ct);
        public ValueTask SetStateAsync(string k, CircuitState s, TimeSpan ttl, CancellationToken ct = default) => _inner.SetStateAsync(k, s, ttl, ct);
        public ValueTask<(int SuccessCount, int FailureCount)> RecordResultAsync(string k, bool ok, TimeSpan w, CancellationToken ct = default) => _inner.RecordResultAsync(k, ok, w, ct);
    }

    // =====================================================================
    // AEGIS-161 — Yavaş (çökmemiş) Redis: her işlem süre sınırında kesilir, çağrılar Redis gecikmesini miras almaz
    // (Toxiproxy ile 1,5 sn Redis gecikmesinde çağrı başına 3 sn ölçülmüştü)
    // =====================================================================
    [Fact]
    public async Task RedisStore_HangingRedis_OperationsBoundedByTimeout_AndReportedUnavailable()
    {
        var store = new RedisCircuitBreakerStateStore(DispatchProxy.Create<IConnectionMultiplexer, HangingRedisProxy>(), "asili:", TimeSpan.FromMilliseconds(100));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(CircuitState.Closed, await store.GetStateAsync("odeme"));
        await store.SetStateAsync("odeme", CircuitState.Open, TimeSpan.FromSeconds(30));
        Assert.Equal(CircuitState.Open, await store.GetStateAsync("odeme")); // yerel yedek durum
        Assert.True(await store.TryAcquireProbeAsync("odeme", "pod-1", TimeSpan.FromSeconds(5))); // fail-open
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1500, $"Yanıt vermeyen Redis ile 4 işlem {sw.ElapsedMilliseconds} ms sürdü");
        Assert.False(store.IsAvailable); // bağlı görünse de yavaş: health check Degraded raporlar
    }

    [Fact]
    public void RedisStore_InvalidOperationTimeout_FailsFast()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RedisCircuitBreakerStateStore(DispatchProxy.Create<IConnectionMultiplexer, HangingRedisProxy>(), "x:", TimeSpan.Zero));
    }

    // =====================================================================
    // AEGIS-162 — Devre durum değişimi metriği pipeline + state etiketli (OpenTelemetry Collector ile görüldü)
    // =====================================================================
    [Fact]
    public async Task Telemetry_CircuitStateChange_TaggedWithPipelineAndState()
    {
        var name = $"cb-tag-{Guid.NewGuid():N}";
        var seen = new System.Collections.Concurrent.ConcurrentBag<(string? Pipeline, string? State)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Name == "aegis.circuitbreaker.state_changes.total") l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? p = null, s = null;
            foreach (var t in tags) { if (t.Key == "pipeline") p = t.Value as string; if (t.Key == "state") s = t.Value as string; }
            if (p == name) seen.Add((p, s));
        });
        listener.Start();

        var manual = new CircuitBreakerManualControl();
        using var pipeline = new AegisPipelineBuilder(name)
            .AddCircuitBreaker(o => { o.MinimumThroughput = 1; o.FailureRatio = 1; o.BreakDuration = TimeSpan.FromMilliseconds(30); o.ManualControl = manual; }).Build();
        await Assert.ThrowsAsync<IOException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new IOException()));
        await Task.Delay(60);
        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)); // HalfOpen -> Closed
        await manual.IsolateAsync(); // manuel işlem de etiketli olmalı

        Assert.Equal(new[] { "half_open", "closed", "isolated", "open" }.OrderBy(x => x), seen.Select(x => x.State).OrderBy(x => x));
    }

    // =====================================================================
    // AEGIS-163 — Timeout CTS havuzu: iptal edilmiş kaynak asla yeniden verilmez; çağıran iptali yine akar
    // =====================================================================
    [Fact]
    public async Task Timeout_PooledTokenSources_NeverLeakCancellationToNextCalls()
    {
        using var pipeline = new AegisPipelineBuilder("cts-havuz").AddTimeout(TimeSpan.FromMilliseconds(50)).Build();

        for (var round = 0; round < 20; round++)
        {
            // 1) Zaman aşımı: kaynak iptal edilir -> havuza DÖNMEMELİ
            await Assert.ThrowsAsync<AegisTimeoutException>(async () =>
                await pipeline.ExecuteAsync(async ctx => { await Task.Delay(2000, ctx.CancellationToken); return 0; }));

            // 2) Hemen ardından gelen çağrılar temiz (iptal edilmemiş) token görmeli
            for (var i = 0; i < 50; i++)
            {
                Assert.False(await pipeline.ExecuteAsync(ctx => ValueTask.FromResult(ctx.CancellationToken.IsCancellationRequested)));
            }
        }

        // 3) Çağıranın iptali, havuzlanmış kaynağa kayıt üzerinden akmalı (bağlı CTS yerine). Uzun zaman aşımlı ayrı boru
        //    hattı: 50 ms'lik zaman aşımı ile 30 ms'lik çağıran iptali ağır yükte yarışıp testi kararsız yapıyordu.
        using var longTimeout = new AegisPipelineBuilder("cts-havuz-iptal").AddTimeout(TimeSpan.FromSeconds(30)).Build();
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await longTimeout.ExecuteAsync(async ctx => { await Task.Delay(10_000, ctx.CancellationToken); return 0; }, new AegisContext(caller.Token)));
        Assert.Equal(caller.Token, ex.CancellationToken);
    }

    // =====================================================================
    // AEGIS-149 — Request Collapser: anahtar zorunlu, sonuç tipi anahtarın parçası
    // =====================================================================
    [Fact]
    public void Collapser_WithoutKeySelector_FailsFastAtBuild()
    {
        var ex = Assert.Throws<ArgumentException>(() => new AegisPipelineBuilder("c").AddRequestCollapser().Build());
        Assert.Contains("KeySelector", ex.Message);
    }

    [Fact]
    public async Task Collapser_SameContextDifferentOperations_AreNotMergedByDefaultAnymore()
    {
        // Eski varsayılan (CorrelationId) ile iki FARKLI işlem tek işleme iniyor, ikincisi birincinin sonucunu alıyordu.
        using var pipeline = new AegisPipelineBuilder("c")
            .AddRequestCollapser(o => o.KeySelector = ctx => ctx.TryGetProperty<string>("Op", out var op) ? op : null)
            .Build();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var user = pipeline.ExecuteAsync(async _ => { await gate.Task; return "kullanici"; }, Ctx("user")).AsTask();
        var orders = pipeline.ExecuteAsync(async _ => { await gate.Task; return "siparisler"; }, Ctx("orders")).AsTask();
        gate.SetResult();

        Assert.Equal("kullanici", await user);
        Assert.Equal("siparisler", await orders);

        static AegisContext Ctx(string op)
        {
            var c = new AegisContext();
            c.SetProperty("Op", op);
            return c;
        }
    }

    [Fact]
    public async Task Collapser_SameKeyDifferentResultTypes_NeverInvalidCast()
    {
        using var pipeline = new AegisPipelineBuilder("c").AddRequestCollapser(o => o.KeySelector = _ => "ayni-anahtar").Build();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var asInt = pipeline.ExecuteAsync(async _ => { await gate.Task; return 42; }).AsTask();
        var asString = pipeline.ExecuteAsync(async _ => { await gate.Task; return "metin"; }).AsTask();
        gate.SetResult();

        Assert.Equal(42, await asInt);
        Assert.Equal("metin", await asString);
    }

    // =====================================================================
    // AEGIS-150 — Durum okumak (health check / pano) OnHalfOpened olayını yutmaz
    // =====================================================================
    [Fact]
    public async Task CircuitBreaker_StatePolling_DoesNotSwallowOnHalfOpened()
    {
        var halfOpened = 0;
        var cb = new CircuitBreakerStrategy(new CircuitBreakerOptions
        {
            MinimumThroughput = 1,
            FailureRatio = 1,
            BreakDuration = TimeSpan.FromMilliseconds(50),
            OnHalfOpened = _ => { Interlocked.Increment(ref halfOpened); return default; }
        });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await cb.ExecuteAsync<int>(_ => throw new InvalidOperationException(), new AegisContext()));
        await Task.Delay(100);

        // Health check / pano yoklaması
        IObservableCircuitState observer = cb;
        Assert.Equal(CircuitState.HalfOpen, observer.LastKnownState);
        Assert.Equal(CircuitState.HalfOpen, cb.State);
        Assert.Equal(0, halfOpened);

        Assert.Equal(1, await cb.ExecuteAsync(_ => ValueTask.FromResult(1), new AegisContext()));
        Assert.Equal(1, halfOpened);
        Assert.Equal(CircuitState.Closed, cb.State);
    }

    // =====================================================================
    // AEGIS-151 — Kullanıcı olay hatası sonucu/istisnayı ezmez
    // =====================================================================
    [Fact]
    public async Task CircuitBreaker_ThrowingOnClosed_DoesNotLoseSuccessfulProbeResult()
    {
        var cb = new CircuitBreakerStrategy(new CircuitBreakerOptions
        {
            MinimumThroughput = 1,
            FailureRatio = 1,
            BreakDuration = TimeSpan.FromMilliseconds(30),
            OnClosed = _ => throw new HttpRequestException("slack alarmı gönderilemedi")
        });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await cb.ExecuteAsync<int>(_ => throw new InvalidOperationException(), new AegisContext()));
        await Task.Delay(60);

        Assert.Equal(99, await cb.ExecuteAsync(_ => ValueTask.FromResult(99), new AegisContext()));
        Assert.Equal(CircuitState.Closed, cb.State);
    }

    [Fact]
    public async Task CircuitBreaker_ThrowingOnOpened_DoesNotMaskOriginalException()
    {
        var cb = new CircuitBreakerStrategy(new CircuitBreakerOptions
        {
            MinimumThroughput = 1,
            FailureRatio = 1,
            OnOpened = _ => throw new InvalidCastException("gözlemci hatası"),
            BreakDurationGenerator = _ => throw new FormatException("üretici hatası")
        });

        var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await cb.ExecuteAsync<int>(_ => throw new TimeoutException("gerçek hata"), new AegisContext()));
        Assert.Equal("gerçek hata", ex.Message);
        Assert.Equal(CircuitState.Open, cb.State); // üretici patlasa da devre açıldı (statik BreakDuration)
    }

    [Fact]
    public async Task DistributedCircuitBreaker_ThrowingCallbacks_DoNotAffectCaller()
    {
        var cb = new DistributedCircuitBreakerStrategy(new InMemoryCircuitBreakerStateStore(), new DistributedCircuitBreakerOptions
        {
            CircuitKey = $"dcb-{Guid.NewGuid():N}",
            MinimumThroughput = 1,
            FailureRatio = 1,
            BreakDuration = TimeSpan.FromMilliseconds(30),
            StateCacheDuration = TimeSpan.Zero,
            OnOpened = _ => throw new InvalidCastException(),
            OnHalfOpened = _ => throw new InvalidCastException(),
            OnClosed = _ => throw new InvalidCastException()
        });

        var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await cb.ExecuteAsync<int>(_ => throw new TimeoutException("gerçek"), new AegisContext()));
        Assert.Equal("gerçek", ex.Message);

        await Task.Delay(60);
        Assert.Equal(5, await cb.ExecuteAsync(_ => ValueTask.FromResult(5), new AegisContext()));
        Assert.Equal(CircuitState.Closed, cb.LastKnownState);
    }

    // =====================================================================
    // AEGIS-152 — Idempotent olmayan istek: gerçek hata yükselir, sahte ek deneme yok
    // =====================================================================
    [Fact]
    public async Task Http_NonIdempotentPost_NetworkFailure_SurfacesRealException_OnceCountedByBreaker()
    {
        var calls = 0;
        var retries = 0;
        var cb = new CircuitBreakerStrategy(new CircuitBreakerOptions { MinimumThroughput = 100 });
        using var pipeline = new AegisPipelineBuilder("odeme")
            .AddRetry(o => { o.MaxRetryAttempts = 3; o.Delay = TimeSpan.Zero; o.OnRetry = _ => { retries++; return default; }; })
            .AddStrategy(cb)
            .Build();
        var handler = new AegisResilienceHandler(pipeline)
        {
            InnerHandler = new LambdaHandler(_ =>
            {
                Interlocked.Increment(ref calls);
                throw new HttpRequestException("bağlantı sıfırlandı");
            })
        };
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync("https://banka/odeme", new StringContent("{}")));
        Assert.Equal("bağlantı sıfırlandı", ex.Message); // eskiden: yapay InvalidOperationException
        Assert.Equal(1, calls);
        Assert.Equal(0, retries);                         // eskiden: 3 boşuna deneme + backoff
    }

    [Fact]
    public async Task Http_SuppressFlag_DoesNotLeakIntoCallerContext()
    {
        using var pipeline = new AegisPipelineBuilder("p").AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.Zero; }).Build();
        var calls = 0;
        var handler = new AegisResilienceHandler(pipeline)
        {
            InnerHandler = new LambdaHandler(_ =>
            {
                calls++;
                return new HttpResponseMessage(calls < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
            })
        };
        using var invoker = new HttpMessageInvoker(handler);
        var shared = new AegisContext();

        using var post = new HttpRequestMessage(HttpMethod.Post, "https://x/");
        post.SetAegisContext(shared);
        using (await invoker.SendAsync(post, CancellationToken.None)) { }
        Assert.False(AegisContextKeys.AreAdditionalAttemptsSuppressed(shared));

        // Aynı bağlamla yapılan idempotent GET normal şekilde yeniden denenmeli
        calls = 0;
        using var get = new HttpRequestMessage(HttpMethod.Get, "https://x/");
        get.SetAegisContext(shared);
        using var response = await invoker.SendAsync(get, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task SuppressFlag_RetryAndHedging_RunExactlyOnce()
    {
        var context = new AegisContext();
        context.Properties[AegisContextKeys.SuppressAdditionalAttempts] = true;
        var calls = 0;

        using var retry = new AegisPipelineBuilder("r").AddRetry(o => { o.MaxRetryAttempts = 5; o.Delay = TimeSpan.Zero; }).Build();
        await Assert.ThrowsAsync<IOException>(async () =>
            await retry.ExecuteAsync<int>(_ => { calls++; throw new IOException(); }, context));
        Assert.Equal(1, calls);

        calls = 0;
        using var hedging = new AegisPipelineBuilder("h").AddHedging(o => { o.MaxHedgedAttempts = 2; o.HedgingDelay = TimeSpan.Zero; }).Build();
        await Assert.ThrowsAsync<IOException>(async () =>
            await hedging.ExecuteAsync<int>(_ => { Interlocked.Increment(ref calls); throw new IOException(); }, context));
        Assert.Equal(1, calls);
    }

    // =====================================================================
    // AEGIS-153 — Kayan pencere: sınıra denk gelen hata patlaması kaçmaz
    // =====================================================================
    [Fact]
    public void HealthWindow_CountsCallsWithinLastSamplingDuration_AcrossBucketBoundaries()
    {
        var window = new HealthWindow(TimeSpan.FromSeconds(1));
        var t0 = Stopwatch.GetTimestamp();
        long At(double seconds) => t0 + (long)(seconds * Stopwatch.Frequency);

        for (var i = 0; i < 5; i++) window.Record(At(0.70), isFailure: true, isSlow: false);
        for (var i = 0; i < 5; i++) window.Record(At(1.10), isFailure: true, isSlow: false);

        // Sabit pencere t=1.0'da sıfırlanıp yalnızca 5 görürdü; kayan pencere son 1 sn'deki 10 hatayı görür
        Assert.Equal(10, window.Snapshot(At(1.10)).FailureCount);
        // Eski dilimler pencereden kademeli düşer
        Assert.Equal(5, window.Snapshot(At(1.90)).FailureCount);
        Assert.Equal(0, window.Snapshot(At(2.30)).FailureCount);
    }

    [Fact]
    public async Task CircuitBreaker_FailureBurstStraddlingOldWindowBoundary_OpensCircuit()
    {
        var cb = new CircuitBreakerStrategy(new CircuitBreakerOptions
        {
            MinimumThroughput = 10,
            FailureRatio = 1,
            SamplingDuration = TimeSpan.FromSeconds(2)
        });

        await Task.Delay(1500);
        for (var i = 0; i < 5; i++) await Fail();
        await Task.Delay(800); // t=2.3 sn: eski sabit pencere (başlangıç t=0) burada sayaçları sıfırlıyordu
        for (var i = 0; i < 5; i++) await Fail();

        Assert.Equal(CircuitState.Open, cb.State);

        async Task Fail()
        {
            try { await cb.ExecuteAsync<int>(_ => throw new IOException(), new AegisContext()); }
            catch (IOException) { }
        }
    }

    // =====================================================================
    // AEGIS-154 — Eski deneme isteğinin finally'si yeni denemenin kilidini açamaz
    // =====================================================================
    [Fact]
    public async Task CircuitBreaker_StaleProbeCompletion_DoesNotReleaseNewProbe()
    {
        var openedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var openedCalls = 0;
        var cb = new CircuitBreakerStrategy(new CircuitBreakerOptions
        {
            MinimumThroughput = 1,
            FailureRatio = 1,
            BreakDuration = TimeSpan.FromMilliseconds(30),
            // İkinci açılışta (HalfOpen denemesi başarısız) gözlemci yavaş: deneme A'nın finally'si gecikir
            OnOpened = async _ => { if (Interlocked.Increment(ref openedCalls) == 2) await openedGate.Task; }
        });

        await Assert.ThrowsAsync<IOException>(async () => await cb.ExecuteAsync<int>(_ => throw new IOException(), new AegisContext()));
        await Task.Delay(60);

        var probeA = cb.ExecuteAsync<int>(_ => throw new IOException(), new AegisContext()).AsTask(); // devreyi yeniden açar, OnOpened'da bekler
        await Task.Delay(80); // açılma süresi yeniden doldu

        var probeBGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeB = cb.ExecuteAsync(_ => new ValueTask<int>(probeBGate.Task), new AegisContext()).AsTask(); // yeni deneme

        openedGate.SetResult(); // A biter; finally'si çalışır
        await Assert.ThrowsAsync<IOException>(() => probeA);

        // B hâlâ deneme yaparken C geçmemeli (eskiden A'nın finally'si bayrağı sıfırlıyor, C de geçiyordu)
        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
            await cb.ExecuteAsync(_ => ValueTask.FromResult(3), new AegisContext()));

        probeBGate.SetResult(1);
        Assert.Equal(1, await probeB);
        Assert.Equal(CircuitState.Closed, cb.State);
    }

    // =====================================================================
    // AEGIS-155 — Dağıtık devre kesici: küme çapında tek deneme + ortak seçenekler
    // =====================================================================
    [Fact]
    public async Task DistributedCircuitBreaker_HalfOpen_SingleProbeAcrossPods_ThenStatsReset()
    {
        var store = new InMemoryCircuitBreakerStateStore();
        var key = $"dcb-{Guid.NewGuid():N}";
        var halfOpened = 0;
        TimeSpan? generatedBreak = null;
        DistributedCircuitBreakerStrategy Pod() => new(store, new DistributedCircuitBreakerOptions
        {
            CircuitKey = key,
            MinimumThroughput = 2,
            FailureRatio = 1,
            BreakDuration = TimeSpan.FromSeconds(30),
            BreakDurationGenerator = e => generatedBreak = TimeSpan.FromMilliseconds(50),
            StateCacheDuration = TimeSpan.Zero,
            OnHalfOpened = _ => { Interlocked.Increment(ref halfOpened); return default; }
        });
        var podA = Pod();
        var podB = Pod();

        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<IOException>(async () => await podA.ExecuteAsync<int>(_ => throw new IOException(), new AegisContext()));
        }
        Assert.Equal(TimeSpan.FromMilliseconds(50), generatedBreak); // ortak BreakDurationGenerator artık dağıtıkta da var
        await Task.Delay(100);

        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = podA.ExecuteAsync(_ => new ValueTask<int>(gate.Task), new AegisContext()).AsTask();
        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
            await podB.ExecuteAsync(_ => ValueTask.FromResult(0), new AegisContext()));

        gate.SetResult(1);
        Assert.Equal(1, await probe);
        Assert.Equal(1, halfOpened);

        // Kapanış sayaçları sıfırladı: tek yeni hata (MinimumThroughput=2) devreyi yeniden açmamalı
        await Assert.ThrowsAsync<IOException>(async () => await podB.ExecuteAsync<int>(_ => throw new IOException(), new AegisContext()));
        Assert.Equal(2, await podB.ExecuteAsync(_ => ValueTask.FromResult(2), new AegisContext()));
    }

    // =====================================================================
    // AEGIS-156 — Paketler arası bağlam anahtarları tek kaynaktan
    // =====================================================================
    [Fact]
    public void ContextKeys_HttpRetryAfterKey_IsTheCoreContract()
    {
        Assert.Equal(AegisContextKeys.RetryAfterDelay, HttpRetryAfterHelper.RetryAfterPropertyKey);
    }

    [Fact]
    public async Task ContextKeys_CacheKey_IsHonored()
    {
        var calls = 0;
        using var cache = new AegisPipelineBuilder("c").AddCache(TimeSpan.FromMinutes(1)).Build();
        var a = new AegisContext(); a.SetProperty(AegisContextKeys.CacheKey, "urun-1");
        var b = new AegisContext(); b.SetProperty(AegisContextKeys.CacheKey, "urun-1");
        await cache.ExecuteAsync(_ => { calls++; return ValueTask.FromResult(1); }, a);
        await cache.ExecuteAsync(_ => { calls++; return ValueTask.FromResult(1); }, b);
        Assert.Equal(1, calls);
    }

    // ---------------------------------------------------------------------
    private sealed class LambdaHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}

/// <summary>Her komutta bağlantı hatası fırlatan Redis (kesinti simülasyonu).</summary>
public class UnreachableRedisProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IConnectionMultiplexer.GetDatabase))
        {
            throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "Redis erişilemez (test)", null, CommandStatus.Unknown);
        }

        return null;
    }
}

/// <summary>Bağlı görünen ama hiçbir komuta yanıt vermeyen Redis (aşırı yavaş sunucu simülasyonu).</summary>
public class HangingRedisProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var returnType = targetMethod?.ReturnType;
        if (returnType == typeof(bool))
        {
            return true; // IsConnected
        }

        if (targetMethod?.Name == nameof(IConnectionMultiplexer.GetDatabase))
        {
            return DispatchProxy.Create<IDatabase, HangingRedisProxy>();
        }

        if (returnType is { IsGenericType: true } && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var tcs = Activator.CreateInstance(typeof(TaskCompletionSource<>).MakeGenericType(returnType.GetGenericArguments()[0]))!;
            return tcs.GetType().GetProperty("Task")!.GetValue(tcs); // asla tamamlanmaz
        }

        return returnType == typeof(Task) ? new TaskCompletionSource().Task : null;
    }
}
