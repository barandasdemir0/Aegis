using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.Hedging;
using Aegis.Resilience.Distributed.Abstractions;
using Aegis.Resilience.RateLimiting;

namespace Aegis.Tests;

/// <summary>
/// Aşama 3 — Polly strateji eşitliği: CB ManualControl/StateProvider, System.Threading.RateLimiting köprüsü,
/// hedging ActionGenerator + sonuca göre hedging, ayrık kaos stratejileri, tipli boru hattı.
/// </summary>
public class StrategyParityStage3Tests
{
    // ---------------------------------------------------------------- ManualControl / StateProvider

    [Fact]
    public async Task ManualControl_CreatedBeforeBuild_ControlsMultipleCircuits()
    {
        var control = new CircuitBreakerManualControl();
        var stateA = new CircuitBreakerStateProvider();
        using var a = new AegisPipelineBuilder("mc-a").AddCircuitBreaker(o => { o.ManualControl = control; o.StateProvider = stateA; }).Build();
        using var b = new AegisPipelineBuilder("mc-b").AddCircuitBreaker(o => o.ManualControl = control).Build();

        Assert.Equal(2, control.CircuitCount);
        Assert.Equal(CircuitState.Closed, stateA.CircuitState);

        await control.IsolateAsync();
        Assert.True(control.IsIsolated);
        Assert.Equal(CircuitState.Isolated, stateA.CircuitState);
        await Assert.ThrowsAsync<IsolatedCircuitException>(async () => await a.ExecuteAsync(_ => ValueTask.FromResult(1)));
        await Assert.ThrowsAsync<IsolatedCircuitException>(async () => await b.ExecuteAsync(_ => ValueTask.FromResult(1)));

        await control.CloseAsync();
        Assert.Equal(1, await a.ExecuteAsync(_ => ValueTask.FromResult(1)));
        Assert.Equal(1, await b.ExecuteAsync(_ => ValueTask.FromResult(1)));
    }

    [Fact]
    public async Task ManualControl_InitiallyIsolated_NewCircuitStartsIsolated()
    {
        var control = new CircuitBreakerManualControl(isIsolated: true);
        using var pipeline = new AegisPipelineBuilder("mc-init").AddCircuitBreaker(o => o.ManualControl = control).Build();

        await Assert.ThrowsAsync<IsolatedCircuitException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));
    }

    [Fact]
    public void StateProvider_CannotBeSharedBetweenCircuits()
    {
        var provider = new CircuitBreakerStateProvider();
        using var first = new AegisPipelineBuilder("sp-1").AddCircuitBreaker(o => o.StateProvider = provider).Build();

        Assert.Throws<InvalidOperationException>(() =>
            new AegisPipelineBuilder("sp-2").AddCircuitBreaker(o => o.StateProvider = provider).Build());
    }

    [Fact]
    public async Task ManualControl_WorksWithDistributedCircuitBreaker()
    {
        var store = new InMemoryCircuitBreakerStateStore();
        var control = new CircuitBreakerManualControl(isIsolated: true);
        using var pipeline = new AegisPipelineBuilder("mc-dist")
            .AddStrategy(new DistributedCircuitBreakerStrategy(store, new DistributedCircuitBreakerOptions { ManualControl = control }))
            .Build();

        // Kurucuda uzak depoya yazılmaz; izolasyon ilk çağrıda uygulanır.
        await Assert.ThrowsAsync<IsolatedCircuitException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));

        await control.CloseAsync();
        Assert.Equal(1, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));
    }

    // ---------------------------------------------------------------- System.Threading.RateLimiting

    [Fact]
    public async Task RateLimitingBridge_UsesDotNetLimiter_AndCarriesRetryAfter()
    {
        using var pipeline = new AegisPipelineBuilder("bridge")
            .AddFixedWindowRateLimiter(new FixedWindowRateLimiterOptions { PermitLimit = 1, Window = TimeSpan.FromSeconds(30), QueueLimit = 0 })
            .Build();

        Assert.Equal(1, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));
        var ex = await Assert.ThrowsAsync<RateLimitRejectedException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));
        Assert.NotNull(ex.RetryAfter); // FixedWindow, RetryAfter metaverisi üretir
    }

    [Fact]
    public async Task RateLimitingBridge_ConcurrencyLease_IsReleasedEvenWhenCallbackFails()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        using var pipeline = new AegisPipelineBuilder("bridge-lease").AddRateLimiter(limiter).Build();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()));
        Assert.Equal(1, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1))); // izin geri verildi
    }

    [Fact]
    public async Task RateLimitingBridge_PartitionedPerTenant()
    {
        using var limiter = PartitionedRateLimiter.Create<AegisContext, string>(ctx =>
            RateLimitPartition.GetConcurrencyLimiter(
                ctx.TryGetProperty<string>("tenant", out var t) ? t! : "anon",
                _ => new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 }));
        using var pipeline = new AegisPipelineBuilder("bridge-part").AddRateLimiter(limiter).Build();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        AegisContext Tenant(string name)
        {
            var c = new AegisContext();
            c.SetProperty("tenant", name);
            return c;
        }

        var holder = pipeline.ExecuteAsync(async _ => { await gate.Task; return 1; }, Tenant("a")).AsTask();
        await Assert.ThrowsAsync<RateLimitRejectedException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1), Tenant("a")));
        Assert.Equal(2, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(2), Tenant("b"))); // başka kiracı etkilenmez
        gate.SetResult();
        await holder;
    }

    // ---------------------------------------------------------------- Hedging

    [Fact]
    public async Task Hedging_ActionGenerator_RunsDifferentActionForHedgedAttempt()
    {
        var seenAttempts = new ConcurrentQueue<int>();
        using var pipeline = new AegisPipelineBuilder("hedge-action")
            .AddHedging(o =>
            {
                o.MaxHedgedAttempts = 1;
                o.HedgingDelay = TimeSpan.FromMilliseconds(20);
                o.ActionGenerator = a => ctx =>
                {
                    seenAttempts.Enqueue(ctx.GetPropertyOrDefault(HedgingOptions.AttemptNumberKey, -1));
                    return ValueTask.FromResult<object?>("yedek-bolge");
                };
            })
            .Build();

        var result = await pipeline.ExecuteAsync(async ctx =>
        {
            await Task.Delay(2_000, ctx.CancellationToken); // birincil yavaş
            return "ana-bolge";
        });

        Assert.Equal("yedek-bolge", result);
        Assert.Equal([1], seenAttempts);
    }

    [Fact]
    public async Task Hedging_ResultBased_BadResultTriggersNextAttempt_AndIsDisposed()
    {
        var disposed = new ConcurrentBag<int>();
        using var pipeline = new AegisPipelineBuilder("hedge-result")
            .AddHedging(o =>
            {
                o.MaxHedgedAttempts = 2;
                o.HedgingDelay = System.Threading.Timeout.InfiniteTimeSpan; // yalnızca önceki başarısız olunca
                o.ShouldHandleOutcome = new AegisPredicateBuilder().HandleResult<Response>(r => r.Status >= 500);
            })
            .Build();
        var calls = 0;

        var result = await pipeline.ExecuteAsync(_ =>
        {
            var n = Interlocked.Increment(ref calls);
            return ValueTask.FromResult(new Response(n < 3 ? 503 : 200, disposed));
        });

        Assert.Equal(200, result.Status);
        Assert.Equal(3, calls);
        await Task.Delay(100);
        Assert.Equal(2, disposed.Count); // atılan iki 503 yanıtı serbest bırakıldı
    }

    [Fact]
    public async Task Hedging_ResultBased_AllBad_ReturnsLastResultNotException()
    {
        using var pipeline = new AegisPipelineBuilder("hedge-allbad")
            .AddHedging(o =>
            {
                o.MaxHedgedAttempts = 1;
                o.HedgingDelay = System.Threading.Timeout.InfiniteTimeSpan;
                o.ShouldHandleResult = r => r is 503;
            })
            .Build();

        Assert.Equal(503, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(503)));
    }

    private sealed class Response(int status, ConcurrentBag<int> disposed) : IDisposable
    {
        public int Status { get; } = status;

        public void Dispose() => disposed.Add(Status);
    }

    // ---------------------------------------------------------------- Ayrık kaos

    [Fact]
    public async Task SplitChaosStrategies_EachInjectsOnlyItsOwnKind()
    {
        using var fault = new AegisPipelineBuilder("c-fault").AddChaosFault(1.0, () => new TimeoutException()).Build();
        await Assert.ThrowsAsync<TimeoutException>(async () => await fault.ExecuteAsync(_ => ValueTask.FromResult(1)));

        using var outcome = new AegisPipelineBuilder("c-outcome").AddChaosOutcome(1.0, _ => -1).Build();
        Assert.Equal(-1, await outcome.ExecuteAsync(_ => ValueTask.FromResult(1)));

        var behaved = 0;
        using var behavior = new AegisPipelineBuilder("c-behavior").AddChaosBehavior(1.0, _ => { behaved++; return default; }).Build();
        Assert.Equal(1, await behavior.ExecuteAsync(_ => ValueTask.FromResult(1))); // asıl çağrı devam eder
        Assert.Equal(1, behaved);

        using var latency = new AegisPipelineBuilder("c-latency").AddChaosLatency(1.0, TimeSpan.FromMilliseconds(50)).Build();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(1, await latency.ExecuteAsync(_ => ValueTask.FromResult(1))); // hata yok
        Assert.True(sw.ElapsedMilliseconds >= 40);
    }

    // ---------------------------------------------------------------- Tipli boru hattı

    [Fact]
    public async Task TypedPipeline_AllFormsWork_AndSharesZeroAllocCore()
    {
        using IAegisPipeline<int> pipeline = new AegisPipelineBuilder("typed")
            .AddRetry(o =>
            {
                o.MaxRetryAttempts = 2;
                o.Delay = TimeSpan.Zero;
                o.ShouldHandleOutcome = new AegisPredicateBuilder().HandleResult<int>(r => r < 0);
            })
            .Build<int>();
        var calls = 0;

        Assert.Equal(1, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(++calls < 2 ? -1 : 1)));
        Assert.Equal(5, await pipeline.ExecuteAsync(static (_, s) => ValueTask.FromResult(s), 5));
        Assert.Equal(6, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(6), CancellationToken.None));
        Assert.Equal(7, pipeline.Execute(() => 7));
        Assert.True((await pipeline.ExecuteOutcomeAsync(_ => ValueTask.FromResult(8))).IsSuccess);
        Assert.Same(pipeline.Untyped.Strategies, pipeline.Strategies);

        static ValueTask<int> Work(AegisContext _) => ValueTask.FromResult(1);
        for (var i = 0; i < 2_000; i++)
        {
            await pipeline.ExecuteAsync(Work);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            _ = SynchronousResult.Of(pipeline.ExecuteAsync(Work));
        }

        Assert.True((GC.GetAllocatedBytesForCurrentThread() - before) / 10_000.0 < 8);
    }
}
