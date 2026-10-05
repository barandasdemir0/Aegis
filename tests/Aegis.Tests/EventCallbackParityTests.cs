using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Chaos;
using Aegis.Resilience.Core.Strategies.Hedging;
using Aegis.Resilience.Core.Strategies.RateLimiter;

namespace Aegis.Tests;

/// <summary>
/// Polly olay geri çağrıları eşitliği: OnRejected + RetryAfter (hız sınırı), OnHedging + DelayGenerator (hedging),
/// OnInjected + EnabledGenerator/InjectionRateGenerator (kaos).
/// </summary>
public class EventCallbackParityTests
{
    [Fact]
    public async Task TokenBucket_Rejection_CarriesRetryAfter_AndCallsOnRejected()
    {
        RateLimiterRejectedArguments? rejected = null;
        using var pipeline = new AegisPipelineBuilder("rl-retry-after")
            .AddRateLimiter(new RateLimiterOptions
            {
                PermitLimit = 1,
                Window = TimeSpan.FromSeconds(10),
                OnRejected = a => { rejected = a; return ValueTask.CompletedTask; }
            })
            .Build();

        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1));
        var ex = await Assert.ThrowsAsync<RateLimitRejectedException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));

        Assert.NotNull(ex.RetryAfter);
        Assert.InRange(ex.RetryAfter!.Value, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(10)); // 1 izin / 10 sn
        Assert.Equal("RateLimiter", rejected!.StrategyName);
        Assert.Equal(ex.RetryAfter, rejected.RetryAfter);
    }

    [Fact]
    public async Task SlidingWindow_Rejection_RetryAfterIsWithinOneSegment()
    {
        using var pipeline = new AegisPipelineBuilder("sw-retry-after")
            .AddSlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions { PermitLimit = 1, Window = TimeSpan.FromSeconds(6), SegmentsPerWindow = 6 })
            .Build();

        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1));
        var ex = await Assert.ThrowsAsync<RateLimitRejectedException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));

        Assert.InRange(ex.RetryAfter!.Value, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ConcurrencyLimiter_OnRejected_ThrowingCallbackIsSwallowed()
    {
        var calls = 0;
        using var pipeline = new AegisPipelineBuilder("cl-onrejected")
            .AddConcurrencyLimiter(1, o =>
            {
                o.QueueTimeout = TimeSpan.Zero;
                o.OnRejected = _ => { calls++; throw new InvalidOperationException("bildirim patladı"); };
            })
            .Build();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = pipeline.ExecuteAsync(async _ => { await gate.Task; return 1; }).AsTask();
        var ex = await Assert.ThrowsAsync<RateLimitRejectedException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));
        gate.SetResult();
        await holder;

        Assert.Equal(1, calls);
        Assert.Null(ex.RetryAfter); // eşzamanlılık sınırında tahmin yok (Polly ile aynı)
    }

    [Fact]
    public async Task Hedging_OnHedging_AndDelayGeneratorPerAttempt()
    {
        var clock = new FakeTimeProvider();
        var hedged = new ConcurrentQueue<int>();
        var requestedDelays = new ConcurrentQueue<int>();
        using var pipeline = new AegisPipelineBuilder("hedge-gen")
            .AddHedging(o =>
            {
                o.MaxHedgedAttempts = 2;
                o.HedgingDelay = TimeSpan.FromHours(1); // üretici varken kullanılmamalı
                o.DelayGenerator = a => { requestedDelays.Enqueue(a.AttemptNumber); return ValueTask.FromResult(TimeSpan.FromSeconds(a.AttemptNumber)); };
                o.OnHedging = a => { hedged.Enqueue(a.AttemptNumber); return ValueTask.CompletedTask; };
            })
            .WithTimeProvider(clock)
            .Build();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        var task = pipeline.ExecuteAsync(async _ => { Interlocked.Increment(ref started); return await release.Task; }).AsTask();

        // Saat, zamanlayıcı kaydolmadan ilerletilirse tetiklenmez (yarış). Koşul sağlanana kadar küçük adımlarla ilerlet.
        await AdvanceUntilAsync(clock, () => Volatile.Read(ref started) >= 2);   // 1. yedek (gecikme 1 sn)
        await AdvanceUntilAsync(clock, () => Volatile.Read(ref started) >= 3);   // 2. yedek (gecikme 2 sn)
        release.SetResult(7);

        Assert.Equal(7, await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal([1, 2], hedged);
        Assert.Equal([1, 2], requestedDelays);
    }

    [Fact]
    public async Task Chaos_ContextGenerators_AndOnInjected()
    {
        var injected = new ConcurrentQueue<ChaosInjectionKind>();
        using var pipeline = new AegisPipelineBuilder("chaos-gen")
            .AddChaos(o =>
            {
                o.Enabled = false; // üretici bunun yerine geçer
                o.EnabledGenerator = ctx => ValueTask.FromResult(ctx.TryGetProperty<string>("tenant", out var t) && t == "test");
                o.InjectionRateGenerator = _ => ValueTask.FromResult(1.0);
                o.OnInjected = a => { injected.Enqueue(a.Kind); return ValueTask.CompletedTask; };
            })
            .Build();

        Assert.Equal(1, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1))); // kiracı yok: kaos yok

        var context = new AegisContext();
        context.SetProperty("tenant", "test");
        await Assert.ThrowsAsync<ChaosInjectedException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1), context));

        Assert.Equal([ChaosInjectionKind.Fault], injected);
    }

    private static async Task AdvanceUntilAsync(FakeTimeProvider clock, Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
