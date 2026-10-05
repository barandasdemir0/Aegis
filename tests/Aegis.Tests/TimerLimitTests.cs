using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Retry;

namespace Aegis.Tests;

/// <summary>
/// Batırma testinin bulduğu hata (1.3.0): .NET zamanlayıcılarının üst sınırını (int.MaxValue ms ≈ 24,8 gün) aşan süreler
/// (ör. "sınırsız" anlamında TimeSpan.MaxValue) çalışma anında ArgumentOutOfRangeException fırlatıyordu. Artık sonsuz sayılır.
/// </summary>
public class TimerLimitTests
{
    [Theory]
    [InlineData(-1)]   // TimeSpan.MaxValue
    [InlineData(25)]   // sınırın hemen üstü (gün)
    [InlineData(3650)]
    public async Task Timeout_BeyondTimerLimit_IsTreatedAsInfinite(int days)
    {
        var timeout = days < 0 ? TimeSpan.MaxValue : TimeSpan.FromDays(days);
        using var pipeline = new AegisPipelineBuilder("huge-timeout").AddTimeout(timeout).Build();

        Assert.Equal(7, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(7)));
        Assert.Equal(8, pipeline.Execute(_ => 8));
    }

    [Fact]
    public async Task PessimisticTimeout_BeyondTimerLimit_IsTreatedAsInfinite()
    {
        using var pipeline = new AegisPipelineBuilder("huge-pessimistic")
            .AddTimeout(TimeSpan.MaxValue, o => o.Mode = Aegis.Resilience.Core.Strategies.Timeout.TimeoutStrategyMode.Pessimistic)
            .Build();

        Assert.Equal(7, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(7)));
    }

    [Fact]
    public async Task RetryDelay_BeyondTimerLimit_WaitsUntilCancelled()
    {
        using var pipeline = new AegisPipelineBuilder("huge-retry")
            .AddRetry(o =>
            {
                o.Delay = TimeSpan.FromDays(100);
                o.MaxDelay = TimeSpan.MaxValue;
                o.BackoffType = DelayBackoffType.Constant;
                o.UseJitter = false;
            })
            .Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException(), cts.Token));
    }

    [Fact]
    public async Task HedgingDelay_BeyondTimerLimit_OnlyHedgesAfterFailure()
    {
        var calls = 0;
        using var pipeline = new AegisPipelineBuilder("huge-hedging")
            .AddHedging(o => { o.MaxHedgedAttempts = 1; o.HedgingDelay = TimeSpan.MaxValue; })
            .Build();

        var result = await pipeline.ExecuteAsync(_ => Interlocked.Increment(ref calls) == 1
            ? throw new InvalidOperationException()
            : ValueTask.FromResult(5));

        Assert.Equal(5, result);
    }

    [Fact]
    public void Cache_WithMaxTtl_Constructs()
    {
        using var pipeline = new AegisPipelineBuilder("huge-cache").AddCache(TimeSpan.MaxValue).Build();

        Assert.NotNull(pipeline);
    }
}
