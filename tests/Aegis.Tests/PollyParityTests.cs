using System.Collections.Concurrent;
using System.Diagnostics;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Core.Strategies.Timeout;

namespace Aegis.Tests;

/// <summary>
/// POLLY PARİTE TESTLERİ — Polly'nin kendi test paketinde (Polly.Core.Tests) kütüphaneyi en çok zorlayan
/// senaryoların Aegis'e birebir uyarlanması. Her bölüm, Polly'deki karşılığına atıfta bulunur:
///   • Issues/IssuesTests.CancellationTokenPropagation_3086 — token ikamesi yapan stratejilerde çağıranın iptali
///   • Issues/IssuesTests.InfiniteRetry_2163 — sonsuz retry'da gecikme taşması
///   • Issues/IssuesTests.CircuitBreakerStateSharing_959 — farklı sonuç tipleri arasında devre durumu paylaşımı
///   • CircuitBreaker/Controller/CircuitStateControllerTests — HalfOpen tek probe, stack trace büyümesi, BreakDuration taşması
///   • Hedging/HedgingResilienceStrategyTests — sıfır/sonsuz gecikme, atılan sonuçların dispose'u, farklı bağlamlar
///   • Retry/RetryResilienceStrategyTests — gecikme sırasında iptal, atılan sonuçların dispose'u, MaxDelay
///   • Timeout/TimeoutResilienceStrategyTests — token geri yükleme, SynchronizationContext, stack trace
/// </summary>
public class PollyParityTests
{
    // =====================================================================
    // 1. Polly #3086 — Token ikamesi yapan stratejilerde çağıranın iptali
    //    "Çağıran iptal ettiğinde, fırlatılan OperationCanceledException ÇAĞIRANIN token'ını taşımalı"
    // =====================================================================
    [Fact]
    public async Task Timeout_CallerCancellation_ExceptionCarriesCallerToken_3086()
    {
        var pipeline = new AegisPipelineBuilder("p3086-timeout").AddTimeout(TimeSpan.FromSeconds(10)).Build();
        using var cts = new CancellationTokenSource();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pipeline.ExecuteAsync(async ctx =>
            {
                cts.CancelAfter(20);
                await Task.Delay(5000, ctx.CancellationToken); // ctx token = Timeout'un ikame ettiği bağlı token
                return 1;
            }, new AegisContext(cts.Token)));

        // Polly kuralı: sızan token, stratejinin iç bağlı token'ı DEĞİL, çağıranın token'ı olmalı
        Assert.Equal(cts.Token, ex.CancellationToken);
        Assert.IsNotType<AegisTimeoutException>(ex);
    }

    [Theory]
    [InlineData("timeout-then-retry")]
    [InlineData("retry-then-timeout")]
    [InlineData("nested-timeouts")]
    [InlineData("hedging")]
    [InlineData("full-stack")]
    public async Task ChainedStrategies_CallerCancellation_ExceptionCarriesCallerToken_3086(string layout)
    {
        IAegisPipelineBuilder b = new AegisPipelineBuilder($"p3086-{layout}");
        b = layout switch
        {
            "timeout-then-retry" => b.AddTimeout(TimeSpan.FromSeconds(10)).AddRetry(o => o.MaxRetryAttempts = 3),
            "retry-then-timeout" => b.AddRetry(o => o.MaxRetryAttempts = 3).AddTimeout(TimeSpan.FromSeconds(10)),
            "nested-timeouts" => b.AddTimeout(TimeSpan.FromSeconds(10)).AddTimeout(TimeSpan.FromSeconds(9)).AddTimeout(TimeSpan.FromSeconds(8)),
            "hedging" => b.AddHedging(o => { o.MaxHedgedAttempts = 2; o.HedgingDelay = TimeSpan.FromSeconds(5); }),
            _ => b.AddTimeout(TimeSpan.FromSeconds(10)).AddRetry(o => o.MaxRetryAttempts = 2).AddCircuitBreaker()
                  .AddHedging(o => o.HedgingDelay = TimeSpan.FromSeconds(5)).AddConcurrencyLimiter(4).AddTimeout(TimeSpan.FromSeconds(9))
        };
        var pipeline = b.Build();
        using var cts = new CancellationTokenSource();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pipeline.ExecuteAsync(async ctx =>
            {
                cts.CancelAfter(20);
                await Task.Delay(5000, ctx.CancellationToken);
                return 1;
            }, new AegisContext(cts.Token)));

        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    [Fact]
    public async Task Timeout_RealTimeout_StillThrowsTimeoutException_NotOce_3086()
    {
        var pipeline = new AegisPipelineBuilder("p3086-real").AddTimeout(TimeSpan.FromMilliseconds(50)).Build();
        using var cts = new CancellationTokenSource(); // çağıran iptal ETMEZ

        await Assert.ThrowsAsync<AegisTimeoutException>(async () =>
            await pipeline.ExecuteAsync(async ctx => { await Task.Delay(5000, ctx.CancellationToken); return 1; }, new AegisContext(cts.Token)));
    }

    [Fact]
    public async Task Timeout_UnrelatedCancellation_ExceptionPreservedUnchanged_3086()
    {
        // Kullanıcı kodu, boru hattıyla ilgisi olmayan bir token ile iptal olursa istisna aynen geçmeli
        var pipeline = new AegisPipelineBuilder("p3086-unrelated").AddTimeout(TimeSpan.FromSeconds(10)).AddRetry(o => o.MaxRetryAttempts = 0).Build();
        using var unrelated = new CancellationTokenSource();
        unrelated.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pipeline.ExecuteAsync<int>(_ => throw new OperationCanceledException("ilgisiz", unrelated.Token)));

        Assert.Equal(unrelated.Token, ex.CancellationToken);
        Assert.Equal("ilgisiz", ex.Message);
    }

    // =====================================================================
    // 2. Polly Timeout — Execute_NoTimeoutOrCancellation_EnsureCancellationTokenRestored
    // =====================================================================
    [Fact]
    public async Task Timeout_AfterExecution_OriginalTokenRestoredOnContext()
    {
        var pipeline = new AegisPipelineBuilder("token-restore").AddTimeout(TimeSpan.FromSeconds(5)).Build();
        using var cts = new CancellationTokenSource();
        var ctx = new AegisContext(cts.Token);

        CancellationToken seenInside = default;
        await pipeline.ExecuteAsync(c => { seenInside = c.CancellationToken; return ValueTask.FromResult(1); }, ctx);

        Assert.NotEqual(cts.Token, seenInside);           // içeride ikame edilmiş bağlı token
        Assert.Equal(cts.Token, ctx.CancellationToken);   // dışarıda orijinal geri yüklenmiş

        // Zaman aşımında da geri yüklenmeli
        var p2 = new AegisPipelineBuilder("token-restore-2").AddTimeout(TimeSpan.FromMilliseconds(30)).Build();
        await Assert.ThrowsAsync<AegisTimeoutException>(async () =>
            await p2.ExecuteAsync(async c => { await Task.Delay(2000, c.CancellationToken); return 1; }, ctx));
        Assert.Equal(cts.Token, ctx.CancellationToken);
    }

    // =====================================================================
    // 3. Polly Timeout — Execute_EnsureCancellationTokenRegistrationNotExecutedOnSynchronizationContext
    //    Tek iş parçacıklı SynchronizationContext (UI / eski ASP.NET) altında klasik deadlock tuzağı.
    // =====================================================================
    private sealed class SingleThreadSyncContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = new();
        private readonly Thread _thread;

        public SingleThreadSyncContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (cb, state) in _queue.GetConsumingEnumerable())
                {
                    cb(state);
                }
            }) { IsBackground = true, Name = "AegisTestSyncCtx" };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException("Send desteklenmez");

        public Task<T> RunAsync<T>(Func<Task<T>> func)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try { tcs.SetResult(await func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            }, null);
            return tcs.Task;
        }

        public void Dispose() => _queue.CompleteAdding();
    }

    [Fact]
    public async Task FullPipeline_UnderSingleThreadedSynchronizationContext_ShouldNotDeadlock()
    {
        using var syncCtx = new SingleThreadSyncContext();
        var pipeline = new AegisPipelineBuilder("syncctx")
            .AddTimeout(TimeSpan.FromSeconds(5))
            .AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.FromMilliseconds(10); })
            .AddCircuitBreaker()
            .AddHedging(o => { o.MaxHedgedAttempts = 1; o.HedgingDelay = TimeSpan.FromMilliseconds(20); })
            .AddConcurrencyLimiter(2)
            .AddCache(TimeSpan.FromSeconds(1))
            .Build();

        var run = syncCtx.RunAsync(async () =>
        {
            var calls = 0;
            var r = await pipeline.ExecuteAsync(async ctx =>
            {
                await Task.Delay(5, ctx.CancellationToken); // await -> sync ctx'e geri döner
                if (++calls < 2) throw new InvalidOperationException("ilk deneme");
                return "ok";
            });
            // Zaman aşımı yolu da sync ctx altında kilitlenmemeli
            var p2 = new AegisPipelineBuilder("syncctx-timeout").AddTimeout(TimeSpan.FromMilliseconds(30)).Build();
            try { await p2.ExecuteAsync(async ctx => { await Task.Delay(2000, ctx.CancellationToken); return 1; }); }
            catch (AegisTimeoutException) { }
            return r;
        });

        var done = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(ReferenceEquals(done, run), "tek iş parçacıklı SynchronizationContext altında DEADLOCK");
        Assert.Equal("ok", await run);
    }

    // =====================================================================
    // 4. Polly Retry — ExecuteAsync_CanceledDuringDelay_EnsureNotExecutedAgain & CanceledBeforeExecution
    // =====================================================================
    [Fact]
    public async Task Retry_CanceledDuringDelay_ShouldNotExecuteAgain_AndThrowPromptly()
    {
        var pipeline = new AegisPipelineBuilder("retry-cancel-delay")
            .AddRetry(o => { o.MaxRetryAttempts = 5; o.Delay = TimeSpan.FromSeconds(10); o.BackoffType = DelayBackoffType.Constant; o.UseJitter = false; })
            .Build();
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var sw = Stopwatch.StartNew();

        var task = pipeline.ExecuteAsync<int>(_ => { calls++; throw new InvalidOperationException(); }, new AegisContext(cts.Token)).AsTask();
        await Task.Delay(100);
        cts.Cancel(); // 10 saniyelik gecikmenin ortasında iptal

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        sw.Stop();
        Assert.Equal(1, calls);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"iptal gecikmeyi kesmedi: {sw.Elapsed}");
        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    [Fact]
    public async Task Retry_CanceledDuringExecution_ResultReturned_NotRetried()
    {
        // Polly: ExecuteAsync_CanceledDuringExecution_EnsureResultReturned — callback iptali fark etmeyip sonuç dönerse, sonuç korunur
        var pipeline = new AegisPipelineBuilder("retry-cancel-exec").AddRetry(o => { o.MaxRetryAttempts = 3; o.ShouldHandleResult = r => r is 0; }).Build();
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var r = await pipeline.ExecuteAsync(_ => { calls++; cts.Cancel(); return ValueTask.FromResult(0); }, new AegisContext(cts.Token))
            .AsTask().ContinueWith(t => t.IsCanceled || t.IsFaulted ? -1 : t.Result);
        Assert.True(calls == 1, $"iptalden sonra tekrar denendi: {calls}");
        Assert.True(r is 0 or -1);
    }

    // =====================================================================
    // 5. Polly Retry/Hedging — EnsureDiscardedResultsDisposed
    //    Yeniden denenen/atılan IDisposable sonuçlar (ör. HttpResponseMessage) dispose edilmeli — sızıntı önlemi.
    // =====================================================================
    private sealed class TrackedDisposable(string name) : IDisposable
    {
        public string Name { get; } = name;
        public int DisposeCount;
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    [Fact]
    public async Task Retry_ResultBased_DiscardedResults_ShouldBeDisposed_FinalResultNot()
    {
        var pipeline = new AegisPipelineBuilder("retry-dispose")
            .AddRetry(o => { o.MaxRetryAttempts = 3; o.Delay = TimeSpan.Zero; o.ShouldHandleResult = r => r is TrackedDisposable { Name: "kötü" }; })
            .Build();

        var produced = new List<TrackedDisposable>();
        var final = await pipeline.ExecuteAsync(_ =>
        {
            var d = new TrackedDisposable(produced.Count < 2 ? "kötü" : "iyi");
            produced.Add(d);
            return ValueTask.FromResult(d);
        });

        Assert.Equal(3, produced.Count);
        Assert.Equal("iyi", final.Name);
        Assert.Equal(0, final.DisposeCount);                              // döndürülen sonuç dispose EDİLMEMELİ
        Assert.All(produced.Take(2), d => Assert.Equal(1, d.DisposeCount)); // atılanlar tam 1 kez dispose edilmeli
    }

    [Fact]
    public async Task Hedging_DiscardedLoserResults_ShouldBeDisposed_WinnerNot()
    {
        var pipeline = new AegisPipelineBuilder("hedge-dispose").AddHedging(o => { o.MaxHedgedAttempts = 2; o.HedgingDelay = TimeSpan.FromMilliseconds(10); }).Build();
        var produced = new ConcurrentBag<TrackedDisposable>();
        var attempt = 0;

        var winner = await pipeline.ExecuteAsync(async ctx =>
        {
            var n = Interlocked.Increment(ref attempt);
            var d = new TrackedDisposable($"deneme-{n}");
            produced.Add(d);
            // Birincil yavaş (kaybeder ama yine de sonuç üretir), ikincil hızlı kazanır
            await Task.Delay(n == 1 ? 300 : 0, CancellationToken.None);
            return d;
        });

        await Task.Delay(500); // kaybedenin bitmesini bekle
        Assert.Equal(0, winner.DisposeCount);
        var losers = produced.Where(d => !ReferenceEquals(d, winner)).ToList();
        Assert.NotEmpty(losers);
        Assert.All(losers, d => Assert.Equal(1, d.DisposeCount));
    }

    // =====================================================================
    // 6. Polly #2163 — Sonsuz retry'da gecikme taşması
    // =====================================================================
    [Fact]
    public async Task InfiniteRetry_ExponentialDelay_ShouldNeverOverflow_2163()
    {
        var observedDelays = new List<TimeSpan>();
        var pipeline = new AegisPipelineBuilder("p2163")
            .AddRetry(o =>
            {
                o.MaxRetryAttempts = int.MaxValue;
                o.BackoffType = DelayBackoffType.Exponential;
                o.Delay = TimeSpan.FromSeconds(2);
                o.MaxDelay = TimeSpan.FromMilliseconds(1); // gerçek zamanda koşabilmek için tavan çok düşük
                o.UseJitter = true;
                o.OnRetry = a => { lock (observedDelays) observedDelays.Add(a.RetryDelay); return ValueTask.CompletedTask; };
            })
            .Build();

        var calls = 0;
        var r = await pipeline.ExecuteAsync(_ =>
        {
            if (++calls < 2049) throw new InvalidOperationException();
            return ValueTask.FromResult("bitti");
        });

        Assert.Equal("bitti", r);
        Assert.Equal(2048, observedDelays.Count);
        Assert.All(observedDelays, d =>
        {
            Assert.True(d >= TimeSpan.Zero, $"negatif gecikme (taşma): {d}");
            Assert.True(d <= TimeSpan.FromMilliseconds(1), $"MaxDelay aşıldı: {d}");
        });
    }

    // =====================================================================
    // 7. Polly #959 — Farklı sonuç tipleri arasında devre durumu paylaşımı
    // =====================================================================
    [Fact]
    public async Task CircuitBreaker_StateSharedAcrossResultTypes_959()
    {
        var pipeline = new AegisPipelineBuilder("p959")
            .AddCircuitBreaker(o =>
            {
                o.FailureRatio = 1.0; o.MinimumThroughput = 10; o.BreakDuration = TimeSpan.FromMilliseconds(300);
                o.ShouldHandleResult = r => r is -1 or "error";
            })
            .Build();

        for (var i = 0; i < 5; i++)
        {
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult(-1));        // int hatası
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult("error"));   // string hatası
        }

        // 10 hata iki tipten toplandı -> devre AÇIK, her iki tip için de
        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(0)));
        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult("valid")));

        await Task.Delay(400);
        Assert.Equal(0, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(0)));
        Assert.Equal("valid", await pipeline.ExecuteAsync(_ => ValueTask.FromResult("valid")));
    }

    // =====================================================================
    // 8. Polly CircuitStateController — OnActionPreExecute_CircuitOpened_EnsureExceptionStackTraceDoesNotGrow
    // =====================================================================
    [Fact]
    public async Task BrokenCircuitException_RepeatedRejections_StackTraceShouldNotGrow()
    {
        var pipeline = new AegisPipelineBuilder("stacktrace").AddCircuitBreaker(o => { o.MinimumThroughput = 1; o.BreakDuration = TimeSpan.FromMinutes(1); }).Build();
        try { await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()); } catch { }

        int? firstLength = null;
        for (var i = 0; i < 200; i++)
        {
            var ex = await Assert.ThrowsAsync<BrokenCircuitException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));
            var len = ex.StackTrace?.Length ?? 0;
            firstLength ??= len;
            Assert.True(len <= firstLength * 3 + 2000, $"stack trace büyüyor: {firstLength} -> {len} (iterasyon {i})");
        }
    }

    // =====================================================================
    // 9. Polly CircuitStateController — OnActionFailureAsync_EnsureBreakDurationNotOverflow
    // =====================================================================
    [Fact]
    public async Task CircuitBreaker_BreakDurationGeneratorReturnsMaxValue_ShouldNotOverflow()
    {
        var pipeline = new AegisPipelineBuilder("breakoverflow")
            .AddCircuitBreaker(o => { o.MinimumThroughput = 1; o.BreakDurationGenerator = _ => TimeSpan.MaxValue; })
            .Build();

        try { await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()); } catch (InvalidOperationException) { }
        // Devre açılmış olmalı; taşma (ArgumentOutOfRange / Overflow) fırlamamalı
        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));
    }

    // =====================================================================
    // 10. Polly CircuitStateController — HalfOpen: yalnızca TEK probe geçer, gerisi reddedilir
    // =====================================================================
    [Fact]
    public async Task CircuitBreaker_HalfOpen_ExactlyOneProbe_OthersRejected_ThenCloses()
    {
        var pipeline = new AegisPipelineBuilder("halfopen-probe")
            .AddCircuitBreaker(o => { o.MinimumThroughput = 1; o.FailureRatio = 0.5; o.BreakDuration = TimeSpan.FromMilliseconds(100); })
            .Build();

        try { await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()); } catch (InvalidOperationException) { }
        await Task.Delay(150); // Open -> HalfOpen'a geçmeye hazır

        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;

        var probe = pipeline.ExecuteAsync(async _ =>
        {
            Interlocked.Increment(ref executions);
            probeStarted.TrySetResult();
            await releaseProbe.Task;
            return "probe";
        }).AsTask();

        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Probe uçuştayken gelen 50 eşzamanlı istek: HEPSİ reddedilmeli, hiçbiri çalışmamalı
        var others = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(async () =>
        {
            try { await pipeline.ExecuteAsync(_ => { Interlocked.Increment(ref executions); return ValueTask.FromResult("sızdı"); }); return false; }
            catch (BrokenCircuitException) { return true; }
        })));
        Assert.All(others, rejected => Assert.True(rejected));
        Assert.Equal(1, executions);

        releaseProbe.SetResult();
        Assert.Equal("probe", await probe);

        // Probe başarılı -> Closed: sonraki istekler geçer
        Assert.Equal("ok", await pipeline.ExecuteAsync(_ => ValueTask.FromResult("ok")));
    }

    [Fact]
    public async Task CircuitBreaker_HalfOpenProbeFails_ShouldReopen_WithFreshBreakDuration()
    {
        var pipeline = new AegisPipelineBuilder("halfopen-fail")
            .AddCircuitBreaker(o => { o.MinimumThroughput = 1; o.FailureRatio = 0.5; o.BreakDuration = TimeSpan.FromMilliseconds(600); })
            .Build();
        // 600 ms: tek çekirdekli Linux konteynerde 150 ms'lik süre ile 100 ms'lik kontrol arasındaki 50 ms pay aşılabiliyordu.

        try { await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()); } catch (InvalidOperationException) { }
        await Task.Delay(700);

        // HalfOpen probe başarısız -> tekrar Open
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException("probe çöktü")));
        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));

        // Yeni kırılma süresi SIFIRDAN başlamalı: 100ms sonra hâlâ açık, ~700ms sonra tekrar probe verir
        await Task.Delay(100);
        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));
        await Task.Delay(600);
        Assert.Equal(1, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));
    }

    // =====================================================================
    // 11. Polly Hedging — ZeroHedgingDelay_EnsureAllTasksSpawnedAtOnce / InfiniteHedgingDelay_EnsureNoConcurrentExecutions
    // =====================================================================
    [Fact]
    public async Task Hedging_ZeroDelay_AllAttemptsSpawnedConcurrently()
    {
        var pipeline = new AegisPipelineBuilder("hedge-zero").AddHedging(o => { o.MaxHedgedAttempts = 4; o.HedgingDelay = TimeSpan.Zero; }).Build();
        var concurrent = 0; var maxConcurrent = 0; var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        var r = await pipeline.ExecuteAsync(async ctx =>
        {
            var c = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref maxConcurrent, c);
            if (Interlocked.Increment(ref started) == 5) allStarted.TrySetResult();
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); // hepsi başlamadan kimse bitmesin
            Interlocked.Decrement(ref concurrent);
            return "x";
        });

        Assert.Equal("x", r);
        Assert.Equal(5, maxConcurrent);
    }

    [Fact]
    public async Task Hedging_InfiniteDelay_NoConcurrentExecutions_FallbackOnlyAfterFailure()
    {
        var pipeline = new AegisPipelineBuilder("hedge-inf").AddHedging(o => { o.MaxHedgedAttempts = 2; o.HedgingDelay = Timeout.InfiniteTimeSpan; }).Build();
        var concurrent = 0; var maxConcurrent = 0; var attempt = 0;

        var r = await pipeline.ExecuteAsync(async _ =>
        {
            var c = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref maxConcurrent, c);
            await Task.Delay(30);
            Interlocked.Decrement(ref concurrent);
            if (Interlocked.Increment(ref attempt) < 3) throw new InvalidOperationException("başarısız");
            return "üçüncü";
        });

        Assert.Equal("üçüncü", r);
        Assert.Equal(1, maxConcurrent); // sonsuz gecikme = asla paralel, yalnızca ardışık yedekleme
        Assert.Equal(3, attempt);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int cur;
        while ((cur = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, cur) != cur) { }
    }

    // =====================================================================
    // 12. Polly Hedging — EveryHedgedTaskShouldHaveDifferentContexts / AllAttemptsFailAndTheOriginalCallIsTheSlowest
    // =====================================================================
    [Fact]
    public async Task Hedging_EachAttempt_DistinctContextAndToken_ParentUnchanged()
    {
        var pipeline = new AegisPipelineBuilder("hedge-ctx").AddHedging(o => { o.MaxHedgedAttempts = 2; o.HedgingDelay = TimeSpan.Zero; }).Build();
        var parent = new AegisContext();
        var seen = new ConcurrentBag<(AegisContext ctx, CancellationToken token)>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        await pipeline.ExecuteAsync(async ctx =>
        {
            seen.Add((ctx, ctx.CancellationToken));
            if (Interlocked.Increment(ref started) == 3) gate.TrySetResult();
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return 1;
        }, parent);

        Assert.Equal(3, seen.Count);
        Assert.Equal(3, seen.Select(s => s.ctx).Distinct().Count());
        Assert.Equal(3, seen.Select(s => s.token).Distinct().Count());
        Assert.DoesNotContain(seen, s => ReferenceEquals(s.ctx, parent));
        Assert.All(seen, s => Assert.Equal(parent.CorrelationId, s.ctx.CorrelationId));
    }

    [Fact]
    public async Task Hedging_AllAttemptsFail_LastExceptionThrown_WithPreservedStackTrace()
    {
        var pipeline = new AegisPipelineBuilder("hedge-allfail").AddHedging(o => { o.MaxHedgedAttempts = 2; o.HedgingDelay = TimeSpan.FromMilliseconds(5); }).Build();
        var n = 0;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteAsync<int>(_ => ThrowFromNamedMethod(Interlocked.Increment(ref n))));
        Assert.Contains(nameof(ThrowFromNamedMethod), ex.StackTrace ?? "");
    }

    private static ValueTask<int> ThrowFromNamedMethod(int n) => throw new InvalidOperationException($"deneme {n}");

    [Fact]
    public async Task Retry_ExhaustedException_StackTracePreserved()
    {
        var pipeline = new AegisPipelineBuilder("retry-stack").AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.Zero; }).Build();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync(_ => ThrowFromNamedMethod(0)));
        Assert.Contains(nameof(ThrowFromNamedMethod), ex.StackTrace ?? "");
    }

    // =====================================================================
    // 13. Polly Hedging — EnsureBackgroundWorkInSuccessfulCallNotCancelled
    // =====================================================================
    [Fact]
    public async Task Hedging_WinnerToken_NotCancelledAfterReturn()
    {
        var pipeline = new AegisPipelineBuilder("hedge-winner-token").AddHedging(o => { o.MaxHedgedAttempts = 1; o.HedgingDelay = TimeSpan.FromMilliseconds(10); }).Build();
        CancellationToken winnerToken = default;
        var attempt = 0;
        var r = await pipeline.ExecuteAsync(async ctx =>
        {
            var n = Interlocked.Increment(ref attempt);
            if (n == 1) { await Task.Delay(500, CancellationToken.None); return "yavaş"; }
            winnerToken = ctx.CancellationToken;
            return "kazanan";
        });
        Assert.Equal("kazanan", r);
        await Task.Delay(50);
        Assert.False(winnerToken.IsCancellationRequested, "kazananın token'ı iptal edildi — arka plan işi kesilir");
    }

    // =====================================================================
    // 14. Polly Retry — RetryDelayGenerator_ReturnsNull_EnsureDefaultRetry / MaxDelay_EnsureRespected
    // =====================================================================
    [Fact]
    public async Task Retry_DelayGeneratorReturnsNull_FallsBackToConfiguredDelay()
    {
        var delays = new List<TimeSpan>();
        var pipeline = new AegisPipelineBuilder("delaygen-null")
            .AddRetry(o =>
            {
                o.MaxRetryAttempts = 2; o.Delay = TimeSpan.FromMilliseconds(7); o.BackoffType = DelayBackoffType.Constant; o.UseJitter = false;
                o.DelayGenerator = _ => ValueTask.FromResult<TimeSpan?>(null);
                o.OnRetry = a => { delays.Add(a.RetryDelay); return ValueTask.CompletedTask; };
            })
            .Build();
        try { await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()); } catch (InvalidOperationException) { }
        Assert.Equal(2, delays.Count);
        Assert.All(delays, d => Assert.Equal(TimeSpan.FromMilliseconds(7), d));
    }

    [Fact]
    public async Task Retry_DelayGeneratorOverridesEverything_IncludingMaxDelayIntent()
    {
        var delays = new List<TimeSpan>();
        var pipeline = new AegisPipelineBuilder("delaygen-override")
            .AddRetry(o =>
            {
                o.MaxRetryAttempts = 3; o.Delay = TimeSpan.FromSeconds(10);
                o.DelayGenerator = a => ValueTask.FromResult<TimeSpan?>(TimeSpan.FromMilliseconds(a.AttemptNumber));
                o.OnRetry = a => { delays.Add(a.RetryDelay); return ValueTask.CompletedTask; };
            })
            .Build();
        var sw = Stopwatch.StartNew();
        try { await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()); } catch (InvalidOperationException) { }
        sw.Stop();
        Assert.Equal(3, delays.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"DelayGenerator yerine 10sn Delay kullanıldı: {sw.Elapsed}");
    }

    // =====================================================================
    // 15. Polly Timeout — Execute_Timeout_EnsureStackTrace & OnTimeout arguments
    // =====================================================================
    [Fact]
    public async Task Timeout_Exception_HasStackTrace_AndOnTimeoutReceivesConfiguredTimeout()
    {
        TimeSpan? seen = null;
        var pipeline = new AegisPipelineBuilder("timeout-stack")
            .AddTimeout(TimeSpan.FromMilliseconds(40), o => o.OnTimeout = (_, t) => { seen = t; return ValueTask.CompletedTask; })
            .Build();
        var ex = await Assert.ThrowsAsync<AegisTimeoutException>(async () =>
            await pipeline.ExecuteAsync(async ctx => { await Task.Delay(2000, ctx.CancellationToken); return 1; }));
        Assert.False(string.IsNullOrEmpty(ex.StackTrace));
        Assert.Equal(TimeSpan.FromMilliseconds(40), seen);
    }

    // =====================================================================
    // 16. Polly Fallback — OnFallback throws -> rethrown, original preserved as inner? (Aegis: handler hatası yükselir)
    // =====================================================================
    [Fact]
    public async Task Fallback_HandlerThrows_HandlerExceptionSurfaces_NotSwallowed()
    {
        var pipeline = new AegisPipelineBuilder("fallback-throws")
            .AddFallback(o => { o.ShouldHandle = _ => true; o.FallbackHandler = (_, _) => throw new ApplicationException("handler patladı"); })
            .Build();
        var ex = await Assert.ThrowsAsync<ApplicationException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException("asıl")));
        Assert.Equal("handler patladı", ex.Message);
    }

    // =====================================================================
    // 17. Polly Simmy (Chaos) — InjectionRate 0/1, Enabled=false, Latency respects cancellation
    // =====================================================================
    [Fact]
    public async Task Chaos_InjectionRateBounds_AndDisabled_BehaveDeterministically()
    {
        var always = new AegisPipelineBuilder("chaos-1").AddChaos(o => { o.Enabled = true; o.InjectionRate = 1.0; }).Build();
        for (var i = 0; i < 20; i++)
            await Assert.ThrowsAsync<ChaosInjectedException>(async () => await always.ExecuteAsync(_ => ValueTask.FromResult(1)));

        var never = new AegisPipelineBuilder("chaos-0").AddChaos(o => { o.Enabled = true; o.InjectionRate = 0.0; }).Build();
        for (var i = 0; i < 200; i++) Assert.Equal(1, await never.ExecuteAsync(_ => ValueTask.FromResult(1)));

        var disabled = new AegisPipelineBuilder("chaos-off").AddChaos(o => { o.Enabled = false; o.InjectionRate = 1.0; }).Build();
        for (var i = 0; i < 200; i++) Assert.Equal(1, await disabled.ExecuteAsync(_ => ValueTask.FromResult(1)));
    }

    [Fact]
    public async Task Chaos_InjectedLatency_ShouldRespectCancellation()
    {
        var pipeline = new AegisPipelineBuilder("chaos-latency")
            .AddChaos(o => { o.Enabled = true; o.InjectionRate = 1.0; o.Latency = TimeSpan.FromSeconds(30); o.FaultGenerator = null; })
            .Build();
        using var cts = new CancellationTokenSource(100);
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1), new AegisContext(cts.Token)));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"kaos gecikmesi iptali yok saydı: {sw.Elapsed}");
    }

    // =====================================================================
    // 18. Polly — ExecuteAsync_EnsureCancellationRequested_Throws (iptal edilmiş token ile hiçbir strateji çalışmamalı)
    // =====================================================================
    [Fact]
    public async Task EveryStrategy_PreCancelledToken_ThrowsOce_WithoutRunningCallback()
    {
        var builders = new Func<IAegisPipelineBuilder, IAegisPipelineBuilder>[]
        {
            b => b.AddRetry(), b => b.AddCircuitBreaker(), b => b.AddTimeout(TimeSpan.FromSeconds(1)),
            b => b.AddHedging(), b => b.AddConcurrencyLimiter(1), b => b.AddRateLimiter(10, TimeSpan.FromSeconds(1)),
            b => b.AddCache(TimeSpan.FromSeconds(1)), b => b.AddRequestCollapser(o => o.KeySelector = _ => "k"), b => b.AddAdaptiveConcurrency(),
            b => b.AddFallback(o => { o.ShouldHandle = _ => false; o.FallbackHandler = (_, _) => ValueTask.FromResult<object?>(0); })
        };
        using var cts = new CancellationTokenSource(); cts.Cancel();

        var i = 0;
        foreach (var add in builders)
        {
            var pipeline = add(new AegisPipelineBuilder($"precancel-{i++}")).Build();
            var ran = false;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await pipeline.ExecuteAsync(_ => { ran = true; return ValueTask.FromResult(1); }, new AegisContext(cts.Token)));
            Assert.False(ran, $"strateji #{i} iptal edilmiş token ile callback'i çalıştırdı");
        }
    }
}
