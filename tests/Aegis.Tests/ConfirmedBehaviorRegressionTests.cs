using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Strategies.Cache;
using Aegis.Resilience.Core.Strategies.Collapser;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.Hedging;
using Aegis.Resilience.Core.Strategies.Timeout;

namespace Aegis.Tests;

public class ConfirmedBehaviorRegressionTests
{
    [Fact]
    public async Task PessimisticTimeout_CallerCancellation_RemainsCancellation()
    {
        var strategy = new TimeoutStrategy(new TimeoutOptions
        {
            Mode = TimeoutStrategyMode.Pessimistic,
            Timeout = TimeSpan.FromSeconds(5)
        });
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await strategy.ExecuteAsync(async ctx =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ctx.CancellationToken);
                return 1;
            }, new AegisContext(cts.Token)));
    }

    [Fact]
    public async Task RequestCollapser_SharedWork_ReceivesWinningCallersProperties()
    {
        var strategy = new RequestCollapserStrategy(new RequestCollapserOptions
        {
            KeySelector = _ => "shared"
        });
        var context = new AegisContext();
        context.SetProperty("tenant", "alpha");

        var tenant = await strategy.ExecuteAsync(ctx =>
        {
            ctx.TryGetProperty<string>("tenant", out var value);
            return ValueTask.FromResult(value);
        }, context);

        Assert.Equal("alpha", tenant);
    }

    [Fact]
    public async Task Cache_TtlStartsWhenSlowCallbackCompletes()
    {
        using var strategy = new CacheStrategy(new CacheOptions
        {
            Ttl = TimeSpan.FromMilliseconds(300)
        });
        var calls = 0;
        var context = new AegisContext(pipelineName: "cache-regression");

        async ValueTask<int> SlowCallback(AegisContext _)
        {
            calls++;
            await Task.Delay(400);
            return calls;
        }

        var first = await strategy.ExecuteAsync(SlowCallback, context);
        var second = await strategy.ExecuteAsync(SlowCallback, context);

        Assert.Equal(1, first);
        Assert.Equal(1, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CircuitBreaker_FailedHalfOpenNotification_DoesNotBlockProbe()
    {
        // 1.0.5: Gözlemci (OnHalfOpened) hatası yutulur; deneme isteği yine yapılır ve devre kapanır.
        // (Eskiden gözlemci hatası çağırana yükseliyor, başarılı olabilecek deneme hiç yapılmıyordu.)
        var strategy = new CircuitBreakerStrategy(new CircuitBreakerOptions
        {
            MinimumThroughput = 1,
            FailureRatio = 1,
            BreakDuration = TimeSpan.FromMilliseconds(20),
            OnHalfOpened = _ => throw new InvalidOperationException("observer failed")
        });
        var context = new AegisContext();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await strategy.ExecuteAsync<int>(_ => throw new InvalidOperationException("backend failed"), context));

        await Task.Delay(50);
        var probeResult = await strategy.ExecuteAsync(_ => ValueTask.FromResult(1), context);
        Assert.Equal(1, probeResult);
        Assert.Equal(CircuitState.Closed, strategy.State);
    }

    [Fact]
    public async Task Hedging_CallerCancellation_DoesNotWaitForUncooperativeAttempts()
    {
        var strategy = new HedgingStrategy(new HedgingOptions
        {
            MaxHedgedAttempts = 1,
            HedgingDelay = TimeSpan.Zero
        });
        using var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new AegisContext(cts.Token);

        var operation = strategy.ExecuteAsync(_ => new ValueTask<int>(gate.Task), context).AsTask();
        cts.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await operation.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            gate.TrySetResult(42);
        }
    }
}
