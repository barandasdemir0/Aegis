using System.Collections.Concurrent;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Core.Strategies.Retry;

namespace Aegis.Tests;

/// <summary>
/// DÜŞMANCA (adversarial) stres testleri: kütüphaneyi bilerek BOZMAYA çalışan senaryolar.
/// Kötü niyetli/hatalı yapılandırma, iptal fırtınası, istisna fırtınası, sınır değerler,
/// dispose yarışları, geri çağrılarda (callback) patlayan kullanıcı kodu, senkron bloklama.
/// Her test "kütüphane ne yapmamalı" sorusunu kilitler: asla kilitlenmemeli, asla sızdırmamalı,
/// asla beklenmeyen istisna tipi fırlatmamalı, asla yanlış sonuç döndürmemeli.
/// </summary>
public class AdversarialStressTests
{
    // =====================================================================
    // 1. KÖTÜ YAPILANDIRMA: geçersiz değerler KURULUMDA reddedilmeli (fail-fast, AEGIS-130).
    //    Eskiden Math.Max(1, x) ile sessizce "düzeltiliyordu": PermitLimit=0 -> 1 istek geçiyordu,
    //    Timeout=0 -> zaman aşımı hiç uygulanmıyordu. Kullanıcı hatayı üretimde yanlış davranış olarak görüyordu.
    // =====================================================================
    [Fact]
    public void InvalidConfiguration_ShouldBeRejectedAtBuildTime_NotSilentlyCoerced()
    {
        // Retry
        Assert.Throws<ArgumentOutOfRangeException>(() => new AegisPipelineBuilder("x").AddRetry(o => o.MaxRetryAttempts = -1).Build());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AegisPipelineBuilder("x").AddRetry(o => o.Delay = TimeSpan.FromSeconds(-1)).Build());

        // Circuit Breaker
        foreach (var ratio in new[] { -1.0, 0.0, 1.5, double.NaN, double.PositiveInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AegisPipelineBuilder("x").AddCircuitBreaker(o => o.FailureRatio = ratio).Build());
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new AegisPipelineBuilder("x").AddCircuitBreaker(o => o.MinimumThroughput = 0).Build());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AegisPipelineBuilder("x").AddCircuitBreaker(o => o.BreakDuration = TimeSpan.Zero).Build());

        // Timeout: 0 ve negatif geçersiz (yanlışlıkla FromTicks yazılması yakalanır); InfiniteTimeSpan geçerli
        Assert.Throws<ArgumentOutOfRangeException>(() => new AegisPipelineBuilder("x").AddTimeout(TimeSpan.Zero).Build());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AegisPipelineBuilder("x").AddTimeout(TimeSpan.FromMilliseconds(-100)).Build());
        _ = new AegisPipelineBuilder("x").AddTimeout(System.Threading.Timeout.InfiniteTimeSpan).Build();

        // Rate limiter ailesi
        Assert.Throws<ArgumentOutOfRangeException>(() => new AegisPipelineBuilder("x").AddRateLimiter(new RateLimiterOptions { PermitLimit = 0 }).Build());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AegisPipelineBuilder("x").AddRateLimiter(new RateLimiterOptions { PermitLimit = 3, Window = TimeSpan.Zero }).Build());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowRateLimiterStrategy(new SlidingWindowRateLimiterOptions { SegmentsPerWindow = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AegisPipelineBuilder("x").AddConcurrencyLimiter(0).Build());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AegisPipelineBuilder("x").AddAdaptiveConcurrency(o => { o.MinConcurrency = 10; o.MaxConcurrency = 5; }).Build());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AegisPipelineBuilder("x").AddAdaptiveConcurrency(o => o.SmoothingFactor = 0).Build());

        // Hedging / Cache / Chaos
        Assert.Throws<ArgumentOutOfRangeException>(() => new AegisPipelineBuilder("x").AddHedging(o => o.MaxHedgedAttempts = 0).Build());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AegisPipelineBuilder("x").AddCache(TimeSpan.FromMinutes(1), o => o.MaxEntries = 0).Build());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AegisPipelineBuilder("x").AddCache(TimeSpan.FromSeconds(-1)).Build());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AegisPipelineBuilder("x").AddChaos(o => o.InjectionRate = 1.5).Build());
    }

    [Fact]
    public void InvalidConfiguration_ErrorMessage_ShouldNameTheOffendingProperty()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AegisPipelineBuilder("x").AddRateLimiter(new RateLimiterOptions { PermitLimit = 0 }).Build());
        Assert.Contains("PermitLimit", ex.Message);
        Assert.Contains("RateLimiterOptions", ex.Message);
    }

    [Fact]
    public async Task Retry_ZeroAttempts_ShouldExecuteExactlyOnce()
    {
        var pipeline = new AegisPipelineBuilder("r0").AddRetry(o => { o.MaxRetryAttempts = 0; o.Delay = TimeSpan.Zero; }).Build();
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteAsync<int>(_ => { calls++; throw new InvalidOperationException(); }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Retry_HugeExponentialBackoff_ShouldBeCappedByMaxDelay_NotOverflow()
    {
        // 2^60 * 1sn => TimeSpan taşması riski; MaxDelay'e sabitlenmeli
        var options = new RetryOptions
        {
            MaxRetryAttempts = 65,
            Delay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromMilliseconds(1),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true
        };
        var pipeline = new AegisPipelineBuilder("rhuge").AddRetry(options).Build();
        var calls = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteAsync<int>(_ => { calls++; throw new InvalidOperationException(); }));
        sw.Stop();
        Assert.Equal(66, calls);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"MaxDelay uygulanmadı, {sw.Elapsed} sürdü");
    }

    [Fact]
    public async Task Timeout_DynamicBudgetExhausted_ShouldTimeoutImmediately_NotRunUnbounded()
    {
        // AEGIS-131: TimeoutGenerator kalan bütçe olarak 0/negatif döndürünce istek SINIRSIZ çalışıyordu.
        var pipeline = new AegisPipelineBuilder("budget0")
            .AddTimeout(TimeSpan.FromSeconds(10), o => o.TimeoutGenerator = ctx =>
                ctx.Properties.TryGetValue("BudgetMs", out var b) ? TimeSpan.FromMilliseconds((int)b!) : null)
            .Build();

        foreach (var budget in new[] { 0, -50 })
        {
            var ctx = new AegisContext(); ctx.SetProperty("BudgetMs", budget);
            var ran = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAsync<AegisTimeoutException>(async () =>
                await pipeline.ExecuteAsync(async c => { ran = true; await Task.Delay(2000, c.CancellationToken); return 1; }, ctx));
            sw.Stop();
            Assert.False(ran, $"bütçe={budget} iken callback çalıştırıldı");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"bütçe={budget} iken {sw.Elapsed} beklendi");
        }
    }

    [Fact]
    public async Task Hedging_HugeAttempts_ShouldNotSpawnUnboundedTasks_WhenFirstWins()
    {
        var pipeline = new AegisPipelineBuilder("hbig").AddHedging(o => { o.MaxHedgedAttempts = 9999; o.HedgingDelay = TimeSpan.FromMilliseconds(50); }).Build();
        var calls = 0;
        var r = await pipeline.ExecuteAsync(_ => { Interlocked.Increment(ref calls); return ValueTask.FromResult("hızlı"); });
        Assert.Equal("hızlı", r);
        Assert.True(calls <= 2, $"ilk deneme anında bitti ama {calls} deneme başlatıldı");
    }

    [Fact]
    public async Task Cache_ZeroTtl_ShouldNeverServeStale()
    {
        var pipeline = new AegisPipelineBuilder("c0").AddCache(TimeSpan.Zero).Build();
        var a = await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1));
        var b = await pipeline.ExecuteAsync(_ => ValueTask.FromResult(2));
        Assert.Equal(1, a);
        Assert.Equal(2, b); // TTL=0 => önbellekten dönmemeli
    }

    [Fact]
    public async Task Fallback_WithoutHandler_ShouldThrowClear_NotReturnDefault()
    {
        // AEGIS-129: handler yokken string için null / int için 0 dönüyordu (sessiz veri bozulması)
        var pipeline = new AegisPipelineBuilder("fallback-nohandler").AddFallback(o => o.ShouldHandle = _ => true).Build();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new HttpRequestException("asıl")));
        Assert.Contains("FallbackHandler", ex.Message);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task Fallback_NullForReferenceType_ShouldBeAccepted()
    {
        var pipeline = new AegisPipelineBuilder("fallback-null")
            .AddFallback(o => { o.ShouldHandle = _ => true; o.FallbackHandler = (_, _) => ValueTask.FromResult<object?>(null); })
            .Build();
        var r = await pipeline.ExecuteAsync<string?>(_ => throw new InvalidOperationException());
        Assert.Null(r);
        var r2 = await pipeline.ExecuteAsync<int?>(_ => throw new InvalidOperationException());
        Assert.Null(r2);
    }

    // =====================================================================
    // 2. İPTAL FIRTINASI
    // =====================================================================
    [Fact]
    public async Task CancellationStorm_AcrossFullPipeline_ShouldNeverLeakPermitsOrHang()
    {
        var limiter = new ConcurrencyLimiterStrategy(new ConcurrencyLimiterOptions { MaxConcurrentExecutions = 8, QueueLimit = int.MaxValue, QueueTimeout = TimeSpan.FromSeconds(5) });
        var pipeline = new AegisPipelineBuilder("cancel-storm")
            .AddRetry(o => { o.MaxRetryAttempts = 3; o.Delay = TimeSpan.FromMilliseconds(5); })
            .AddStrategy(limiter)
            .AddTimeout(TimeSpan.FromSeconds(2))
            .AddHedging(o => { o.MaxHedgedAttempts = 1; o.HedgingDelay = TimeSpan.FromMilliseconds(10); })
            .Build();

        var unexpected = new ConcurrentBag<Exception>();
        var tasks = Enumerable.Range(0, 2000).Select(i => Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(i % 30)); // 0..29ms sonra iptal
            try
            {
                await pipeline.ExecuteAsync(async ctx => { await Task.Delay(20, ctx.CancellationToken); return 1; }, new AegisContext(cts.Token));
            }
            catch (OperationCanceledException) { }
            catch (RateLimitRejectedException) { }
            catch (AegisTimeoutException) { }
            catch (Exception ex) { unexpected.Add(ex); }
        })).ToArray();

        var all = Task.WhenAll(tasks);
        var done = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(90)));
        Assert.True(ReferenceEquals(done, all), "iptal fırtınasında kilitlendi");
        Assert.True(unexpected.IsEmpty, "beklenmeyen: " + string.Join(" | ", unexpected.Take(3).Select(e => e.GetType().Name + ": " + e.Message)));

        // İzin sızıntısı yok: limitin tamamı geri gelmiş olmalı
        var probe = await pipeline.ExecuteAsync(_ => ValueTask.FromResult(42));
        Assert.Equal(42, probe);
    }

    [Fact]
    public async Task PreCancelledToken_ShouldThrowOperationCanceled_NotRunCallback()
    {
        var pipeline = new AegisPipelineBuilder("precancel")
            .AddRetry(o => o.MaxRetryAttempts = 5)
            .AddCircuitBreaker()
            .AddTimeout(TimeSpan.FromSeconds(1))
            .Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ran = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pipeline.ExecuteAsync(_ => { ran = true; return ValueTask.FromResult(1); }, new AegisContext(cts.Token)));
        Assert.False(ran, "iptal edilmiş token ile callback çalıştırıldı");
    }

    [Fact]
    public async Task Retry_ShouldNotRetry_WhenCallerCancels()
    {
        var pipeline = new AegisPipelineBuilder("retry-cancel").AddRetry(o => { o.MaxRetryAttempts = 10; o.Delay = TimeSpan.FromMilliseconds(50); }).Build();
        using var cts = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pipeline.ExecuteAsync<int>(_ =>
            {
                calls++;
                cts.Cancel(); // çağıran vazgeçti
                throw new InvalidOperationException("geçici");
            }, new AegisContext(cts.Token)));
        Assert.True(calls <= 2, $"çağıran iptal ettikten sonra {calls} kez denendi");
    }

    // =====================================================================
    // 3. KULLANICI GERİ ÇAĞRILARI (callback) PATLADIĞINDA
    // =====================================================================
    [Fact]
    public async Task Retry_OnRetryCallbackThrows_ShouldSurfaceNotHang()
    {
        var pipeline = new AegisPipelineBuilder("onretry-throw")
            .AddRetry(o => { o.MaxRetryAttempts = 3; o.Delay = TimeSpan.Zero; o.OnRetry = _ => throw new ApplicationException("callback patladı"); })
            .Build();
        var task = pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()).AsTask();
        var done = await Task.WhenAny(task, Task.Delay(5000));
        Assert.True(ReferenceEquals(done, task), "OnRetry patlayınca askıda kaldı");
        await Assert.ThrowsAnyAsync<Exception>(() => task);
    }

    [Fact]
    public async Task Retry_ShouldHandlePredicateThrows_ShouldNotHang()
    {
        var pipeline = new AegisPipelineBuilder("pred-throw")
            .AddRetry(o => { o.MaxRetryAttempts = 3; o.Delay = TimeSpan.Zero; o.ShouldHandle = _ => throw new ApplicationException("predicate patladı"); })
            .Build();
        var task = pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException("asıl")).AsTask();
        var done = await Task.WhenAny(task, Task.Delay(5000));
        Assert.True(ReferenceEquals(done, task));
        await Assert.ThrowsAnyAsync<Exception>(() => task);
    }

    [Fact]
    public async Task CircuitBreaker_OnOpenedCallbackThrows_ShouldStillOpenCircuit()
    {
        var pipeline = new AegisPipelineBuilder("onopened-throw")
            .AddCircuitBreaker(o =>
            {
                o.MinimumThroughput = 2; o.FailureRatio = 0.5; o.BreakDuration = TimeSpan.FromSeconds(30);
                o.OnOpened = _ => throw new ApplicationException("OnOpened patladı");
            })
            .Build();

        for (var i = 0; i < 3; i++)
        {
            try { await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()); }
            catch (Exception) { }
        }

        // Callback patlamış olsa da devre AÇIK olmalı: callback hatası devre durumunu bozmamalı
        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));
    }

    [Fact]
    public async Task Collapser_KeySelectorThrows_ShouldNotPoisonOtherCallers()
    {
        var calls = 0;
        var pipeline = new AegisPipelineBuilder("collapser-poison")
            .AddRequestCollapser(o => o.KeySelector = ctx =>
            {
                if (ctx.Properties.ContainsKey("Poison")) throw new ApplicationException("key patladı");
                return "sabit";
            })
            .Build();

        var poison = new AegisContext(); poison.SetProperty("Poison", true);
        await Assert.ThrowsAsync<ApplicationException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1), poison));

        // Zehirli çağrıdan sonra normal çağrılar çalışmaya devam etmeli
        var r = await pipeline.ExecuteAsync(_ => { Interlocked.Increment(ref calls); return ValueTask.FromResult(99); });
        Assert.Equal(99, r);
    }

    [Fact]
    public async Task Collapser_LeaderThrows_AllFollowersGetSameException_AndNextCallStartsFresh()
    {
        var pipeline = new AegisPipelineBuilder("collapser-leaderfail").AddRequestCollapser(o => o.KeySelector = _ => "aynı").Build();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;

        var followers = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            try
            {
                await pipeline.ExecuteAsync<int>(async _ =>
                {
                    Interlocked.Increment(ref executions);
                    await gate.Task;
                    throw new InvalidOperationException("lider çöktü");
                });
                return (Exception?)null;
            }
            catch (Exception ex) { return ex; }
        })).ToArray();

        await Task.Delay(100);
        gate.SetResult();
        var results = await Task.WhenAll(followers);

        Assert.Equal(1, executions);
        Assert.All(results, ex => Assert.IsType<InvalidOperationException>(ex));

        // Zehirli sonuç önbelleklenmemeli: sonraki çağrı yeni bir yürütme başlatmalı
        var fresh = await pipeline.ExecuteAsync(_ => ValueTask.FromResult("taze"));
        Assert.Equal("taze", fresh);
    }

    // =====================================================================
    // 4. İSTİSNA FIRTINASI + CB + RETRY birlikte (gerçek felaket)
    // =====================================================================
    [Fact]
    public async Task ExceptionStorm_ThroughFullStack_ShouldOnlyProduceKnownExceptionTypes()
    {
        var pipeline = new AegisPipelineBuilder("storm")
            .AddTimeout(TimeSpan.FromMilliseconds(500))
            .AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.FromMilliseconds(1); })
            .AddCircuitBreaker(o => { o.MinimumThroughput = 10; o.FailureRatio = 0.3; o.BreakDuration = TimeSpan.FromMilliseconds(100); })
            .AddConcurrencyLimiter(16, o => { o.QueueLimit = int.MaxValue; o.QueueTimeout = TimeSpan.FromMilliseconds(200); })
            .AddFallback(o => { o.ShouldHandle = ex => ex is TimeoutException; o.FallbackHandler = (_, _) => ValueTask.FromResult<object?>(-1); })
            .Build();

        var exceptionTypes = new ConcurrentDictionary<string, int>();
        var tasks = Enumerable.Range(0, 3000).Select(i => Task.Run(async () =>
        {
            var kind = i % 7;
            try
            {
                await pipeline.ExecuteAsync<int>(async ctx =>
                {
                    switch (kind)
                    {
                        case 0: throw new InvalidOperationException();
                        case 1: throw new HttpRequestException();
                        case 2: throw new TimeoutException();
                        case 3: await Task.Delay(700, ctx.CancellationToken); return 0; // zaman aşımı
                        case 4: throw new OutOfMemoryException("sahte");
                        case 5: throw new StackOverflowException("sahte");
                        default: return 1;
                    }
                });
            }
            catch (Exception ex) { exceptionTypes.AddOrUpdate(ex.GetType().Name, 1, (_, c) => c + 1); }
        })).ToArray();

        var all = Task.WhenAll(tasks);
        var done = await Task.WhenAny(all, Task.Delay(TimeSpan.FromMinutes(2)));
        Assert.True(ReferenceEquals(done, all), "istisna fırtınasında kilitlendi");

        var allowed = new HashSet<string>
        {
            nameof(InvalidOperationException), nameof(HttpRequestException), nameof(TimeoutException),
            nameof(AegisTimeoutException), nameof(BrokenCircuitException), nameof(RateLimitRejectedException),
            nameof(OutOfMemoryException), nameof(StackOverflowException), nameof(TaskCanceledException), nameof(OperationCanceledException)
        };
        var unknown = exceptionTypes.Keys.Where(k => !allowed.Contains(k)).ToList();
        Assert.True(unknown.Count == 0, "BEKLENMEYEN istisna tipleri sızdı: " + string.Join(", ", unknown));
    }

    // =====================================================================
    // 5. DISPOSE YARIŞLARI
    // =====================================================================
    [Fact]
    public async Task DisposeWhileExecuting_ShouldNotCrashInFlightOperations_OrDeadlock()
    {
        for (var round = 0; round < 20; round++)
        {
            var pipeline = new AegisPipelineBuilder($"dispose-race-{round}")
                .AddCache(TimeSpan.FromSeconds(10))
                .AddConcurrencyLimiter(4, o => o.QueueLimit = int.MaxValue)
                .AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.Zero; })
                .Build();

            var inflight = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
            {
                try { await pipeline.ExecuteAsync(async _ => { await Task.Delay(5); return 1; }); }
                catch (ObjectDisposedException) { } // kabul edilebilir
                catch (RateLimitRejectedException) { }
            })).ToArray();

            await Task.Delay(2);
            pipeline.Dispose();
            pipeline.Dispose(); // idempotent

            var all = Task.WhenAll(inflight);
            var done = await Task.WhenAny(all, Task.Delay(10_000));
            Assert.True(ReferenceEquals(done, all), $"round {round}: dispose sırasında kilitlendi");
            await all; // beklenmeyen istisna varsa burada patlar
        }
    }

    // =====================================================================
    // 6. SENKRON BLOKLAMA (klasik deadlock tuzağı)
    // =====================================================================
#pragma warning disable xUnit1031 // Senkron bloklama BU TESTİN KONUSU: klasik deadlock tuzağını bilerek kuruyoruz
    [Fact]
    public void SyncOverAsync_GetAwaiterGetResult_ShouldNotDeadlock()
    {
        var pipeline = new AegisPipelineBuilder("sync-block")
            .AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.FromMilliseconds(5); })
            .AddTimeout(TimeSpan.FromSeconds(5))
            // Bol kuyruk süresi: test KİLİTLENMEYİ ölçer. Tek çekirdekte senkron bloklama havuzu aç bırakıp kısa kuyruk
            // süresinde red üretebilir; bu kilitlenme değildir (1 CPU Linux konteynerde görüldü).
            .AddConcurrencyLimiter(2, o => { o.QueueLimit = int.MaxValue; o.QueueTimeout = TimeSpan.FromSeconds(30); })
            .Build();

        var t = Task.Run(() =>
        {
            var results = new int[50];
            Parallel.For(0, 50, i =>
            {
                results[i] = pipeline.ExecuteAsync(async _ => { await Task.Delay(1); return i; }).AsTask().GetAwaiter().GetResult();
            });
            return results.Sum();
        });
        Assert.True(t.Wait(TimeSpan.FromSeconds(60)), "senkron bloklama ile deadlock");
        Assert.Equal(Enumerable.Range(0, 50).Sum(), t.Result);
    }
#pragma warning restore xUnit1031

    // =====================================================================
    // 7. SINIR DEĞERLER: null sonuç, boş/dev anahtar, değer tipleri
    // =====================================================================
    [Fact]
    public async Task NullResults_ThroughCacheAndCollapser_ShouldRoundTrip()
    {
        var pipeline = new AegisPipelineBuilder("nulls")
            .AddCache(TimeSpan.FromSeconds(5), o => o.CacheNulls = true)
            .AddRequestCollapser(o => o.KeySelector = _ => "k")
            .Build();
        var a = await pipeline.ExecuteAsync(_ => ValueTask.FromResult<string?>(null));
        var b = await pipeline.ExecuteAsync(_ => ValueTask.FromResult<string?>("olmamalı"));
        Assert.Null(a);
        Assert.Null(b); // null önbelleklendi
    }

    [Fact]
    public async Task ValueTypeResults_ThroughEveryStrategy_ShouldRoundTrip()
    {
        var pipeline = new AegisPipelineBuilder("valuetypes")
            .AddRetry(o => o.MaxRetryAttempts = 1)
            .AddCircuitBreaker()
            .AddTimeout(TimeSpan.FromSeconds(5))
            .AddHedging(o => o.MaxHedgedAttempts = 1)
            .AddCache(TimeSpan.FromSeconds(5))
            .AddFallback(o => { o.ShouldHandle = _ => true; o.FallbackHandler = (_, _) => ValueTask.FromResult<object?>(default(Guid)); })
            .Build();

        var g = Guid.NewGuid();
        var r = await pipeline.ExecuteAsync(_ => ValueTask.FromResult(g));
        Assert.Equal(g, r);

        var pipeline2 = new AegisPipelineBuilder("valuetypes2").AddRetry().AddTimeout(TimeSpan.FromSeconds(1)).Build();
        var d = await pipeline2.ExecuteAsync(_ => ValueTask.FromResult(3.14m));
        Assert.Equal(3.14m, d);
        var ts = await pipeline2.ExecuteAsync(_ => ValueTask.FromResult(TimeSpan.FromDays(1)));
        Assert.Equal(TimeSpan.FromDays(1), ts);
        var tup = await pipeline2.ExecuteAsync(_ => ValueTask.FromResult((1, "a")));
        Assert.Equal((1, "a"), tup);
    }

    [Fact]
    public async Task Fallback_WrongResultType_ShouldThrowClearException_NotCorruptResult()
    {
        // Fallback nesnesi string döndürüyor ama pipeline int bekliyor
        var pipeline = new AegisPipelineBuilder("fallback-type")
            .AddFallback(o => { o.ShouldHandle = _ => true; o.FallbackHandler = (_, _) => ValueTask.FromResult<object?>("yanlış tip"); })
            .Build();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException("asıl")));
        Assert.Contains("String", ex.Message); // hangi tipin döndüğünü söylemeli
        Assert.Contains("Int32", ex.Message);  // hangi tipin beklendiğini söylemeli
        Assert.NotNull(ex.InnerException);     // asıl hata korunmalı
    }

    [Fact]
    public async Task EmptyAndHugeKeys_ShouldWork()
    {
        var pipeline = new AegisPipelineBuilder("keys")
            .AddCache(TimeSpan.FromSeconds(5), o => o.KeySelector = ctx => ctx.Properties.TryGetValue("K", out var k) ? k?.ToString() ?? "" : "")
            .Build();
        foreach (var key in new[] { "", " ", new string('x', 100_000), "\0", "🔥" })
        {
            var ctx = new AegisContext(); ctx.SetProperty("K", key);
            var r = await pipeline.ExecuteAsync(_ => ValueTask.FromResult(key.Length), ctx);
            Assert.Equal(key.Length, r);
        }
    }

    // =====================================================================
    // 8. UZUN SÜRELİ: örnekleme pencereleri arası doğruluk
    // =====================================================================
    [Fact]
    public async Task CircuitBreaker_ManySamplingWindows_ShouldRecoverAndNotAccumulateStaleFailures()
    {
        var pipeline = new AegisPipelineBuilder("cb-windows")
            .AddCircuitBreaker(o => { o.MinimumThroughput = 5; o.FailureRatio = 0.5; o.SamplingDuration = TimeSpan.FromMilliseconds(200); o.BreakDuration = TimeSpan.FromMilliseconds(100); })
            .Build();

        // 1. pencere: hep hata -> açılır
        for (var i = 0; i < 6; i++) { try { await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()); } catch { } }
        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));

        // Kırılma süresi + örnekleme penceresi geçsin
        await Task.Delay(400);

        // Sonraki 5 pencere boyunca HEP başarı: eski hatalar sayılmamalı, devre kapalı kalmalı
        for (var w = 0; w < 5; w++)
        {
            for (var i = 0; i < 20; i++)
            {
                var r = await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1));
                Assert.Equal(1, r);
            }
            await Task.Delay(120);
        }
    }

    [Fact]
    public async Task Retry_ResultBased_InfiniteBadResult_ShouldStopAtMaxAttempts()
    {
        var pipeline = new AegisPipelineBuilder("result-loop")
            .AddRetry(o => { o.MaxRetryAttempts = 4; o.Delay = TimeSpan.Zero; o.ShouldHandleResult = r => r is null or ""; })
            .Build();
        var calls = 0;
        var r = await pipeline.ExecuteAsync(_ => { calls++; return ValueTask.FromResult(""); });
        Assert.Equal("", r);
        Assert.Equal(5, calls);
    }
}
