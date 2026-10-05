using Microsoft.Extensions.Time.Testing;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Chaos;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.Retry;

namespace Aegis.Tests;

/// <summary>
/// Polly v8 eşitliği: <see cref="TimeProvider"/> (sahte saatle beklemeden test), <c>Randomizer</c>, bağlam farkındalıklı
/// async koşullar (<see cref="AegisPredicate"/>, <see cref="AegisPredicateBuilder"/>) ve tipli özellik anahtarları.
/// </summary>
public class TimeAndPredicateParityTests
{
    // ---------------------------------------------------------------- TimeProvider

    [Fact]
    public async Task Retry_UsesTimeProvider_DelayCompletesOnlyWhenClockAdvances()
    {
        var clock = new FakeTimeProvider();
        using var pipeline = new AegisPipelineBuilder("fake-retry")
            .AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.FromMinutes(10); o.BackoffType = DelayBackoffType.Constant; o.UseJitter = false; })
            .WithTimeProvider(clock)
            .Build();
        var calls = 0;

        var task = pipeline.ExecuteAsync(_ => ++calls == 1 ? throw new InvalidOperationException() : ValueTask.FromResult(calls)).AsTask();

        await Task.Delay(50);
        Assert.False(task.IsCompleted); // 10 dakikalık gecikme gerçek zamanda beklenmiyor
        Assert.Equal(1, calls);

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(2, await task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Timeout_UsesTimeProvider()
    {
        var clock = new FakeTimeProvider();
        using var pipeline = new AegisPipelineBuilder("fake-timeout").AddTimeout(TimeSpan.FromHours(1)).WithTimeProvider(clock).Build();

        var task = pipeline.ExecuteAsync(async ctx =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ctx.CancellationToken);
            return 1;
        }).AsTask();

        await Task.Delay(50);
        Assert.False(task.IsCompleted);

        clock.Advance(TimeSpan.FromHours(1));
        await Assert.ThrowsAsync<AegisTimeoutException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task PessimisticTimeout_UsesTimeProvider()
    {
        var clock = new FakeTimeProvider();
        using var pipeline = new AegisPipelineBuilder("fake-pessimistic")
            .AddTimeout(TimeSpan.FromHours(1), o => o.Mode = Aegis.Resilience.Core.Strategies.Timeout.TimeoutStrategyMode.Pessimistic)
            .WithTimeProvider(clock)
            .Build();
        using var release = new CancellationTokenSource();

        var task = pipeline.ExecuteAsync(async _ =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, release.Token); // token'a saygısız iş
            return 1;
        }).AsTask();

        await Task.Delay(50);
        Assert.False(task.IsCompleted);

        clock.Advance(TimeSpan.FromHours(1));
        await Assert.ThrowsAsync<AegisTimeoutException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        release.Cancel();
    }

    [Fact]
    public async Task CircuitBreaker_BreakDuration_FollowsTimeProvider()
    {
        var clock = new FakeTimeProvider();
        var breaker = new CircuitBreakerStateProvider();
        using var pipeline = new AegisPipelineBuilder("fake-cb")
            .AddCircuitBreaker(o => { o.MinimumThroughput = 2; o.FailureRatio = 0.5; o.BreakDuration = TimeSpan.FromHours(2); o.SamplingDuration = TimeSpan.FromMinutes(1); o.StateProvider = breaker; })
            .WithTimeProvider(clock)
            .Build();
        

        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()));
        }

        Assert.Equal(CircuitState.Open, breaker.CircuitState);
        clock.Advance(TimeSpan.FromHours(2) - TimeSpan.FromSeconds(1));
        Assert.Equal(CircuitState.Open, breaker.CircuitState);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(CircuitState.HalfOpen, breaker.CircuitState);

        Assert.Equal(1, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));
        Assert.Equal(CircuitState.Closed, breaker.CircuitState);
    }

    [Fact]
    public async Task CircuitBreaker_SamplingWindow_FollowsTimeProvider()
    {
        var clock = new FakeTimeProvider();
        var breaker = new CircuitBreakerStateProvider();
        using var pipeline = new AegisPipelineBuilder("fake-cb-window")
            .AddCircuitBreaker(o => { o.MinimumThroughput = 2; o.FailureRatio = 0.5; o.SamplingDuration = TimeSpan.FromSeconds(10); o.StateProvider = breaker; })
            .WithTimeProvider(clock)
            .Build();
        

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()));
        clock.Advance(TimeSpan.FromSeconds(30)); // ilk hata pencereden düşer
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()));

        Assert.Equal(CircuitState.Closed, breaker.CircuitState);
    }

    // ---------------------------------------------------------------- Randomizer

    [Fact]
    public async Task Retry_Randomizer_MakesJitterDeterministic()
    {
        var clock = new FakeTimeProvider();
        var delays = new List<TimeSpan>();
        using var pipeline = new AegisPipelineBuilder("jitter")
            .AddRetry(o =>
            {
                o.MaxRetryAttempts = 1; o.Delay = TimeSpan.FromSeconds(10); o.BackoffType = DelayBackoffType.Constant; o.UseJitter = true;
                o.Randomizer = () => 0.0; // jitter katsayısı 0.5 + 0.5 * 0 = 0.5
                o.OnRetry = a => { delays.Add(a.RetryDelay); return ValueTask.CompletedTask; };
            })
            .WithTimeProvider(clock)
            .Build();
        var calls = 0;

        var task = pipeline.ExecuteAsync(_ => ++calls == 1 ? throw new InvalidOperationException() : ValueTask.FromResult(1)).AsTask();
        await Task.Delay(50);
        clock.Advance(TimeSpan.FromSeconds(5));
        await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([TimeSpan.FromSeconds(5)], delays);
    }

    [Theory]
    [InlineData(0.1, true)]
    [InlineData(0.9, false)]
    public async Task Chaos_Randomizer_ControlsInjection(double random, bool injected)
    {
        using var pipeline = new AegisPipelineBuilder("chaos-random")
            .AddChaos(o => { o.Enabled = true; o.InjectionRate = 0.5; o.Randomizer = () => random; })
            .Build();

        var run = async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1));

        if (injected)
        {
            await Assert.ThrowsAsync<ChaosInjectedException>(run);
        }
        else
        {
            Assert.Equal(1, await run());
        }
    }

    // ---------------------------------------------------------------- Koşullar (predicate)

    [Fact]
    public async Task Retry_PredicateBuilder_HandlesExceptionsAndResults()
    {
        using var pipeline = new AegisPipelineBuilder("pb")
            .AddRetry(o =>
            {
                o.MaxRetryAttempts = 5; o.Delay = TimeSpan.Zero;
                o.ShouldHandleOutcome = new AegisPredicateBuilder().Handle<TimeoutException>().HandleResult<int>(r => r < 0);
            })
            .Build();

        var calls = 0;
        var result = await pipeline.ExecuteAsync(_ => ++calls switch
        {
            1 => throw new TimeoutException(),
            2 => ValueTask.FromResult(-1),
            _ => ValueTask.FromResult(calls)
        });
        Assert.Equal(3, result);

        calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteAsync<int>(_ => { calls++; throw new InvalidOperationException(); }));
        Assert.Equal(1, calls); // ele alınmayan istisna yeniden denenmez
    }

    [Fact]
    public async Task Retry_AsyncContextAwarePredicate_SeesAttemptNumberAndContext()
    {
        var seenAttempts = new List<int>();
        using var pipeline = new AegisPipelineBuilder("ctx-pred")
            .AddRetry(o =>
            {
                o.MaxRetryAttempts = 5; o.Delay = TimeSpan.Zero;
                o.ShouldHandleOutcome = AegisPredicate.Create(async args =>
                {
                    await Task.Yield();
                    seenAttempts.Add(args.AttemptNumber);
                    return args.Exception is not null && args.Context.OperationKey == "GetOrder" && args.AttemptNumber < 2;
                });
            })
            .Build();

        var context = new AegisContext { OperationKey = "GetOrder" };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException(), context));

        Assert.Equal([0, 1, 2], seenAttempts); // 3 deneme; üçüncüde koşul false
    }

    [Fact]
    public async Task Retry_Predicate_NeverRetriesCancellation()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        using var pipeline = new AegisPipelineBuilder("pred-cancel")
            .AddRetry(o => { o.MaxRetryAttempts = 3; o.Delay = TimeSpan.Zero; o.ShouldHandleOutcome = new AegisPredicateBuilder().HandleAnyException(); })
            .Build();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pipeline.ExecuteAsync<int>(_ =>
        {
            calls++;
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }, new AegisContext(cts.Token)));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CircuitBreaker_PredicateOnResult_OpensCircuit()
    {
        using var pipeline = new AegisPipelineBuilder("cb-pred")
            .AddCircuitBreaker(o =>
            {
                o.MinimumThroughput = 2; o.FailureRatio = 0.5; o.BreakDuration = TimeSpan.FromMinutes(1);
                o.ShouldHandleOutcome = new AegisPredicateBuilder().HandleResult(500);
            })
            .Build();

        Assert.Equal(500, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(500)));
        Assert.Equal(500, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(500)));

        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(200)));
    }

    [Fact]
    public void PredicateBuilder_HandleInner_FindsWrappedAndAggregated()
    {
        var predicate = new AegisPredicateBuilder().HandleInner<TimeoutException>(ex => ex.Message == "db");

        Assert.True(predicate.ShouldHandle(Outcome<int>.FromException(new InvalidOperationException("x", new TimeoutException("db")))));
        Assert.True(predicate.ShouldHandle(Outcome<int>.FromException(new AggregateException(new FormatException(), new TimeoutException("db")))));
        Assert.False(predicate.ShouldHandle(Outcome<int>.FromException(new InvalidOperationException("x", new TimeoutException("api")))));
        Assert.False(predicate.ShouldHandle(Outcome<int>.FromResult(1)));
    }

    [Fact]
    public void PredicateBuilder_ResultRules_MatchDerivedAndObjectTypes()
    {
        var predicate = new AegisPredicateBuilder().HandleResult<string>(s => s.Length == 0);

        Assert.True(predicate.ShouldHandle(Outcome<string>.FromResult("")));
        Assert.True(predicate.ShouldHandle(Outcome<object>.FromResult("")));   // object sonuçlu boru hattı
        Assert.False(predicate.ShouldHandle(Outcome<object>.FromResult(0)));
        Assert.False(predicate.ShouldHandle(Outcome<string>.FromException(new FormatException()))); // istisna kuralı yok
    }

    [Fact]
    public async Task Retry_PredicateBuilder_SuccessPathAllocatesNothing()
    {
        using var pipeline = new AegisPipelineBuilder("pb-alloc")
            .AddRetry(o => { o.MaxRetryAttempts = 3; o.Delay = TimeSpan.Zero; o.ShouldHandleOutcome = new AegisPredicateBuilder().Handle<TimeoutException>().HandleResult<int>(r => r < 0); })
            .Build();

        static ValueTask<int> Work(AegisContext _) => ValueTask.FromResult(1);
        for (var i = 0; i < 2_000; i++)
        {
            await pipeline.ExecuteAsync(Work);
        }

        const int calls = 10_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < calls; i++)
        {
            _ = SynchronousResult.Of(pipeline.ExecuteAsync(Work));
        }

        var perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)calls;
        Assert.True(perCall < 8, $"Koşullu başarı yolunda çağrı başına {perCall:F1} bayt ayrıldı (int sonuç kutulanmamalı)");
    }

    // ---------------------------------------------------------------- Tipli özellik anahtarları + OperationKey

    [Fact]
    public void TypedPropertyKeys_RoundTripAndInteropWithStringKeys()
    {
        var tenant = new AegisPropertyKey<string>("tenant");
        var retries = new AegisPropertyKey<int>("retries");
        var context = new AegisContext();

        Assert.False(context.TryGetProperty(tenant, out _));
        Assert.Equal(7, context.GetPropertyOrDefault(retries, 7));

        context.SetProperty(tenant, "acme");
        context.SetProperty(retries, 3);

        Assert.True(context.TryGetProperty(tenant, out var value));
        Assert.Equal("acme", value);
        Assert.Equal(3, context.GetPropertyOrDefault(retries, 0));
        Assert.True(context.TryGetProperty<string>("tenant", out var viaString)); // aynı sözlük
        Assert.Equal("acme", viaString);
        Assert.Equal(new AegisPropertyKey<string>("tenant"), tenant);
    }

    [Fact]
    public void OperationKey_FlowsToChildAndIsClearedOnReset()
    {
        var context = new AegisContext { OperationKey = "GetOrder" };

        Assert.Equal("GetOrder", context.CreateChild(CancellationToken.None).OperationKey);

        context.Reset();
        Assert.Null(context.OperationKey);
    }
}
