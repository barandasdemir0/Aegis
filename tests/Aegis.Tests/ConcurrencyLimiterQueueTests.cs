using System.Diagnostics;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.RateLimiter;

namespace Aegis.Tests;

/// <summary>
/// Kilitsiz eşzamanlılık sınırlayıcının kuyruk yolu: bekleyen izin bırakılınca hemen uyanır (kayıp uyandırma yok),
/// süre dolunca reddedilir, bekleme sırasında iptal bekleyeni sızdırmaz, yoğun yükte limit asla aşılmaz.
/// </summary>
public class ConcurrencyLimiterQueueTests
{
    private static (ConcurrencyLimiterStrategy Strategy, IDisposable Pipeline, Func<Func<Task>, Task<int>> Run) Create(int limit, TimeSpan queueTimeout)
    {
        var strategy = new ConcurrencyLimiterStrategy(new ConcurrencyLimiterOptions { MaxConcurrentExecutions = limit, QueueLimit = int.MaxValue, QueueTimeout = queueTimeout });
        var pipeline = new AegisPipelineBuilder("kuyruk").AddStrategy(strategy).Build();
        return (strategy, pipeline, work => pipeline.ExecuteAsync(async _ => { await work(); return 1; }).AsTask());
    }

    [Fact]
    public async Task QueuedCall_WakesUpAsSoonAsAPermitIsReleased()
    {
        var (_, pipeline, run) = Create(limit: 1, queueTimeout: TimeSpan.FromSeconds(10));
        using var _ = pipeline;
        var holder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = run(() => holder.Task);
        var second = run(() => Task.CompletedTask);
        await Task.Delay(100);
        Assert.False(second.IsCompleted); // kuyrukta bekliyor

        var released = Stopwatch.StartNew();
        holder.SetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(released.Elapsed < TimeSpan.FromSeconds(1), $"bekleyen geç uyandı: {released.Elapsed}");
        await first;
    }

    [Fact]
    public async Task QueuedCall_IsRejectedWhenQueueTimeoutExpires()
    {
        var (_, pipeline, run) = Create(limit: 1, queueTimeout: TimeSpan.FromMilliseconds(150));
        using var _ = pipeline;
        var holder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = run(() => holder.Task);

        await Assert.ThrowsAsync<RateLimitRejectedException>(() => run(() => Task.CompletedTask));

        holder.SetResult();
        await first;
        Assert.Equal(1, await run(() => Task.CompletedTask)); // izin sızmadı
    }

    [Fact]
    public async Task CancellationWhileQueued_PropagatesAndDoesNotLeakPermitsOrWaiters()
    {
        var strategy = new ConcurrencyLimiterStrategy(new ConcurrencyLimiterOptions { MaxConcurrentExecutions = 1, QueueLimit = int.MaxValue, QueueTimeout = TimeSpan.FromSeconds(10) });
        using var pipeline = new AegisPipelineBuilder("kuyruk-iptal").AddStrategy(strategy).Build();
        var holder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = pipeline.ExecuteAsync(async _ => { await holder.Task; return 1; }).AsTask();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.ExecuteAsync(_ => new ValueTask<int>(2), new Aegis.Resilience.Core.Context.AegisContext(cts.Token)).AsTask());

        holder.SetResult();
        await first;
        Assert.Equal(3, await pipeline.ExecuteAsync(_ => new ValueTask<int>(3)));
    }

    [Fact]
    public async Task HeavyContention_NeverExceedsLimit_AndEveryCallCompletes()
    {
        var (_, pipeline, run) = Create(limit: 4, queueTimeout: TimeSpan.FromSeconds(30));
        using var _ = pipeline;
        var inside = 0;
        var maxObserved = 0;

        async Task Work()
        {
            var now = Interlocked.Increment(ref inside);
            int seen;
            while ((seen = Volatile.Read(ref maxObserved)) < now && Interlocked.CompareExchange(ref maxObserved, now, seen) != seen)
            {
            }

            await Task.Yield();
            Interlocked.Decrement(ref inside);
        }

        var calls = Enumerable.Range(0, 2_000).Select(_ => Task.Run(() => run(Work)));
        var results = await Task.WhenAll(calls);

        Assert.Equal(2_000, results.Length);
        Assert.InRange(maxObserved, 1, 4);
    }
}
