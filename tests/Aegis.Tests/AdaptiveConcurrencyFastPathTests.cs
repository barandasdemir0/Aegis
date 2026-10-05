using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.RateLimiter;

namespace Aegis.Tests;

/// <summary>
/// 1.3.0 senkron hızlı yolu (boşta kapasite + senkron tamamlanan geri çağrı) ile yavaş yolun (kuyruk, iptal, beklemeli geri çağrı)
/// davranış sözleşmesi. Hızlı yolun kazancı (durum makinesi maliyeti) bu testlerle değil, AdaptiveConcurrencyBenchmarks ile ölçülür:
/// async ValueTask metodu da hiç beklemezse senkron tamamlandığı için tamamlanma durumu iki uygulamada aynıdır.
/// Değişmez: hızlı yol da yavaş yol da izni her koşulda geri verir (sızıntı yok) ve kapasiteyi aşmaz.
/// </summary>
public class AdaptiveConcurrencyFastPathTests
{
    private static (IAegisPipeline Pipeline, AdaptiveConcurrencyStrategy Strategy) Build(int limit, TimeSpan? queueTimeout = null)
    {
        var strategy = new AdaptiveConcurrencyStrategy(new AdaptiveConcurrencyOptions
        {
            MinConcurrency = 1,
            InitialConcurrency = limit,
            MaxConcurrency = limit,
            QueueTimeout = queueTimeout ?? TimeSpan.Zero
        });
        return (new AegisPipelineBuilder("adaptive-fast").AddStrategy(strategy).Build(), strategy);
    }

    [Fact]
    public async Task SynchronousSuccess_CompletesSynchronously_AndReleasesPermit()
    {
        var (pipeline, strategy) = Build(2);
        using var _ = pipeline;

        var pending = pipeline.ExecuteAsync(_ => ValueTask.FromResult(7));

        Assert.True(pending.IsCompletedSuccessfully); // senkron tamamlanma sözleşmesi
        Assert.Equal(7, await pending);
        Assert.Equal(0, strategy.ActiveExecutions);
    }

    [Fact]
    public async Task SynchronousFailure_ReleasesPermit_AndPropagatesOriginalException()
    {
        var (pipeline, strategy) = Build(1);
        using var _ = pipeline;

        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException("boom")));
            Assert.Equal(0, strategy.ActiveExecutions); // sızan izin olsa 2. çağrıda reddedilirdi
        }
    }

    [Fact]
    public async Task AsyncCallback_HoldsPermitUntilDone_ThenReleases()
    {
        var (pipeline, strategy) = Build(1);
        using var _ = pipeline;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = pipeline.ExecuteAsync(async _ => { await gate.Task; return 1; }).AsTask();
        Assert.Equal(1, strategy.ActiveExecutions);
        await Assert.ThrowsAsync<RateLimitRejectedException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(2))); // dolu

        gate.SetResult();
        Assert.Equal(1, await running);
        Assert.Equal(0, strategy.ActiveExecutions);
        Assert.Equal(3, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(3))); // izin geri geldi
    }

    [Fact]
    public async Task CancelledToken_DoesNotConsumePermit_AndStillThrowsCancellation()
    {
        var (pipeline, strategy) = Build(1);
        using var _ = pipeline;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pipeline.ExecuteAsync(async ct => { await Task.Yield(); return 1; }, cts.Token));

        Assert.Equal(0, strategy.ActiveExecutions);
        Assert.Equal(1, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));
    }

    [Fact]
    public async Task FullCapacity_QueuesAndAdmitsWhenPermitFrees()
    {
        var (pipeline, strategy) = Build(1, queueTimeout: TimeSpan.FromSeconds(5));
        using var _ = pipeline;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = pipeline.ExecuteAsync(async _ => { await gate.Task; return 1; }).AsTask();
        var queued = pipeline.ExecuteAsync(_ => ValueTask.FromResult(2)).AsTask(); // kuyruğa girer (yavaş yol)
        await Task.Delay(50);
        Assert.False(queued.IsCompleted);

        gate.SetResult();
        Assert.Equal(1, await first);
        Assert.Equal(2, await queued);
        Assert.Equal(0, strategy.ActiveExecutions);
    }

    [Fact]
    public async Task ConcurrentStorm_NeverExceedsLimit_AndLeaksNoPermit()
    {
        const int limit = 8;
        var (pipeline, strategy) = Build(limit, queueTimeout: TimeSpan.FromMilliseconds(5));
        using var _ = pipeline;
        var inside = 0;
        var maxInside = 0;

        await Task.WhenAll(Enumerable.Range(0, 64).Select(worker => Task.Run(async () =>
        {
            var random = new Random(worker);
            for (var i = 0; i < 400; i++)
            {
                try
                {
                    await pipeline.ExecuteAsync(async _ =>
                    {
                        var now = Interlocked.Increment(ref inside);
                        InterlockedMax(ref maxInside, now);
                        try
                        {
                            switch (random.Next(3))
                            {
                                case 0: return 0;                                        // senkron
                                case 1: await Task.Yield(); return 0;                    // async
                                default: throw new InvalidOperationException("geçici");  // hata
                            }
                        }
                        finally
                        {
                            Interlocked.Decrement(ref inside);
                        }
                    });
                }
                catch (Exception ex) when (ex is InvalidOperationException or RateLimitRejectedException)
                {
                }
            }
        })));

        Assert.True(maxInside <= limit, $"aynı anda {maxInside} çağrı (sınır {limit})");
        Assert.Equal(0, strategy.ActiveExecutions);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
