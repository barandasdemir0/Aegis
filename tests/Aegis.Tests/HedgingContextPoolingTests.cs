using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Pipeline;

namespace Aegis.Tests;

/// <summary>
/// Hedging birincil denemesinin alt bağlamı havuzdan kiralanır (1.0.10). Başarı yolunda tahsis yapılmamalı; izolasyon,
/// özellik taşıma ve ortak korelasyon kimliği (AEGIS-110/135) eşzamanlı ve asenkron yollarda aynen korunmalıdır.
/// </summary>
public class HedgingContextPoolingTests
{
    private static IDisposableHedging Pipeline(int attempts = 2, TimeSpan? delay = null) =>
        new(new AegisPipelineBuilder("hedge-pool")
            .AddHedging(o => { o.MaxHedgedAttempts = attempts - 1; o.HedgingDelay = delay ?? TimeSpan.FromSeconds(1); })
            .Build());

    [Fact]
    public async Task SyncSuccessPath_AllocatesNothingPerCall()
    {
        using var hedging = Pipeline();
        var pipeline = hedging.Pipeline;
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
        Assert.True(perCall < 8, $"Hedging başarı yolunda çağrı başına {perCall:F1} bayt ayrıldı (1.0.9: 144 B)");
    }

    [Fact]
    public async Task SyncPath_ChildSharesCorrelationId_IsIsolated_AndMergesProperties()
    {
        using var hedging = Pipeline();
        var parent = new AegisContext();
        parent.SetProperty("in", 1);
        AegisContext? seen = null;
        string? childCorrelation = null;

        await hedging.Pipeline.ExecuteAsync(ctx =>
        {
            seen = ctx;
            childCorrelation = ctx.CorrelationId;      // ebeveynde henüz üretilmemişti: tembel okunur
            Assert.True(ctx.TryGetProperty<int>("in", out _));
            ctx.SetProperty("out", 2);
            return ValueTask.FromResult(1);
        }, parent);

        Assert.NotSame(parent, seen);                  // izole alt bağlam
        Assert.Equal(parent.CorrelationId, childCorrelation);
        Assert.True(parent.TryGetProperty<int>("out", out var merged));
        Assert.Equal(2, merged);
    }

    [Fact]
    public async Task AsyncPath_AllAttemptsShareParentCorrelationId()
    {
        using var hedging = Pipeline(attempts: 3, delay: TimeSpan.FromMilliseconds(10));
        var parent = new AegisContext();
        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();
        var contexts = new System.Collections.Concurrent.ConcurrentBag<AegisContext>();

        await hedging.Pipeline.ExecuteAsync(async ctx =>
        {
            contexts.Add(ctx);
            await Task.Delay(100, ctx.CancellationToken).ContinueWith(_ => { });
            ids.Add(ctx.CorrelationId); // bekleme SONRASI okunur: ebeveyn bağı sabitlenmiş olmalı
            return 1;
        }, parent);

        await Task.Delay(300); // kaybeden denemeler de kimliklerini yazsın
        Assert.True(contexts.Count >= 2);
        Assert.Equal(contexts.Count, contexts.Distinct().Count()); // her deneme ayrı bağlam
        Assert.All(ids, id => Assert.Equal(parent.CorrelationId, id));
    }

    [Fact]
    public async Task AbandonedAttempt_ReadsCorrelationAfterPipelineReturned_StillCorrectAndNotReusedContext()
    {
        // Havuzdan gelen boru hattı bağlamı (context verilmeden) çağrı sonunda iade edilir. Terk edilen denemenin
        // sonradan okuduğu kimlik, çağrı sırasında sabitlenmiş kimlik olmalı; yeniden kullanılan bağlamdan okunmamalı.
        using var hedging = Pipeline(attempts: 2, delay: TimeSpan.FromMilliseconds(10));
        var release = new TaskCompletionSource();
        string? duringCall = null;
        string? afterCall = null;
        var calls = 0;

        await hedging.Pipeline.ExecuteAsync(async ctx =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                duringCall = ctx.CorrelationId;
                await release.Task;              // birincil: çağrı bittikten sonra devam eder (terk edilen deneme)
                afterCall = ctx.CorrelationId;
                return 0;
            }

            return 2;                            // yedek deneme kazanır
        });

        // Havuzdaki bağlamı başka bir çağrıyla yeniden kullandır
        await hedging.Pipeline.ExecuteAsync(ctx => ValueTask.FromResult(ctx.CorrelationId.Length));
        release.SetResult();
        await Task.Delay(100);

        Assert.NotNull(duringCall);
        Assert.Equal(duringCall, afterCall);
    }

    private sealed class IDisposableHedging(Aegis.Resilience.Core.Abstractions.IAegisPipeline pipeline) : IDisposable
    {
        public Aegis.Resilience.Core.Abstractions.IAegisPipeline Pipeline { get; } = pipeline;

        public void Dispose() => Pipeline.Dispose();
    }
}
