using System.Runtime.CompilerServices;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;

namespace Aegis.Tests;

/// <summary>
/// Polly v8 çalıştırma API'si eşitliği (Polly: ResiliencePipelineTests.Async / .Sync / ExecuteOutcomeAsync):
/// TState, CancellationToken, fırlatmayan Outcome ve senkron Execute biçimleri. Hepsi aynı çekirdekten geçtiği için
/// iptal, retry ve AEGIS-134 token kuralı her biçimde aynı olmalıdır.
/// </summary>
public class ExecutionApiParityTests
{
    private static IAegisPipeline RetryPipeline(int attempts = 2) =>
        new AegisPipelineBuilder("api-parity").AddRetry(o => { o.MaxRetryAttempts = attempts; o.Delay = TimeSpan.Zero; }).Build();

    // ---------------------------------------------------------------- TState

    [Fact]
    public async Task ExecuteAsync_WithState_PassesStateAndContext()
    {
        using var pipeline = RetryPipeline();
        var context = new AegisContext();

        var result = await pipeline.ExecuteAsync(
            static (ctx, s) => ValueTask.FromResult($"{s.Prefix}-{s.Value}-{ctx.PipelineName}"),
            (Prefix: "x", Value: 42),
            context);

        Assert.Equal("x-42-api-parity", result);
    }

    [Fact]
    public async Task ExecuteAsync_WithState_IsRetried()
    {
        using var pipeline = RetryPipeline();
        var counter = new StrongBox<int>();

        var result = await pipeline.ExecuteAsync(static (_, c) =>
        {
            if (++c.Value < 3)
            {
                throw new InvalidOperationException();
            }

            return ValueTask.FromResult(c.Value);
        }, counter);

        Assert.Equal(3, result);
    }

    [Fact]
    public async Task ExecuteAsync_VoidWithState_Works()
    {
        using var pipeline = RetryPipeline();
        var box = new StrongBox<int>();

        await pipeline.ExecuteAsync(static (_, b) => { b.Value = 7; return ValueTask.CompletedTask; }, box);

        Assert.Equal(7, box.Value);
    }

    [Fact]
    public async Task ExecuteAsync_WithState_SuccessPathAllocatesNothing()
    {
        using var pipeline = new AegisPipelineBuilder("tstate-alloc")
            .AddTimeout(TimeSpan.FromSeconds(30))
            .AddRetry(o => { o.MaxRetryAttempts = 3; o.Delay = TimeSpan.Zero; })
            .AddCircuitBreaker(o => o.MinimumThroughput = 100)
            .Build();

        static ValueTask<int> Work(AegisContext _, int s) => ValueTask.FromResult(s + 1);

        for (var i = 0; i < 2_000; i++)
        {
            await pipeline.ExecuteAsync(Work, i);
        }

        const int calls = 10_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < calls; i++)
        {
            _ = SynchronousResult.Of(pipeline.ExecuteAsync(Work, i));
        }

        var perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)calls;
        Assert.True(perCall < 8, $"TState yolunda çağrı başına {perCall:F1} bayt ayrıldı");
    }

    // ---------------------------------------------------------------- CancellationToken

    [Fact]
    public async Task ExecuteAsync_WithToken_CallbackReceivesToken()
    {
        using var pipeline = RetryPipeline();
        using var cts = new CancellationTokenSource();
        CancellationToken seen = default;

        await pipeline.ExecuteAsync(ct => { seen = ct; return ValueTask.FromResult(1); }, cts.Token);

        Assert.Equal(cts.Token, seen);
    }

    [Fact]
    public async Task ExecuteAsync_WithToken_TimeoutLinksCallbackToken()
    {
        using var pipeline = new AegisPipelineBuilder("ct-timeout").AddTimeout(TimeSpan.FromMilliseconds(50)).Build();

        await Assert.ThrowsAsync<AegisTimeoutException>(async () =>
            await pipeline.ExecuteAsync(async ct =>
            {
                await Task.Delay(5_000, ct);
                return 1;
            }, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_WithToken_PreCancelled_DoesNotInvokeCallback()
    {
        using var pipeline = RetryPipeline();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var invoked = false;

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pipeline.ExecuteAsync(_ => { invoked = true; return ValueTask.CompletedTask; }, cts.Token));

        Assert.False(invoked);
        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    [Fact]
    public async Task ExecuteAsync_WithStateAndToken_Works()
    {
        using var pipeline = RetryPipeline();
        using var cts = new CancellationTokenSource();

        var result = await pipeline.ExecuteAsync(
            static (s, ct) => ValueTask.FromResult(s * 2 + (ct.CanBeCanceled ? 1 : 0)), 20, cts.Token);

        Assert.Equal(41, result);
    }

    [Fact]
    public async Task ExecuteAsync_WithToken_CallerCancellation_CarriesCallerToken_3086()
    {
        using var pipeline = new AegisPipelineBuilder("ct-3086").AddTimeout(TimeSpan.FromSeconds(10)).Build();
        using var cts = new CancellationTokenSource();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pipeline.ExecuteAsync(async ct =>
            {
                cts.CancelAfter(20);
                await Task.Delay(5_000, ct);
                return 1;
            }, cts.Token));

        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    // ---------------------------------------------------------------- Outcome (fırlatmaz)

    [Fact]
    public async Task ExecuteOutcomeAsync_Failure_ReturnsOutcomeWithoutThrowing()
    {
        using var pipeline = RetryPipeline();
        var calls = 0;

        var outcome = await pipeline.ExecuteOutcomeAsync(
            (_, _) => { calls++; return ValueTask.FromResult(Outcome<int>.FromException(new InvalidOperationException("x"))); },
            0);

        Assert.False(outcome.IsSuccess);
        Assert.IsType<InvalidOperationException>(outcome.Exception);
        Assert.Equal(3, calls); // 1 + 2 retry: outcome içindeki istisna da retry'ı tetikler
    }

    [Fact]
    public async Task ExecuteOutcomeAsync_CallbackThrows_IsCaptured()
    {
        using var pipeline = new AegisPipelineBuilder("outcome-throw").Build();

        var outcome = await pipeline.ExecuteOutcomeAsync<int, int>((_, _) => throw new FormatException(), 0);

        Assert.IsType<FormatException>(outcome.Exception);
    }

    [Fact]
    public async Task ExecuteOutcomeAsync_Success_ReturnsResult()
    {
        using var pipeline = RetryPipeline();

        var outcome = await pipeline.ExecuteOutcomeAsync(_ => ValueTask.FromResult("ok"));

        Assert.True(outcome.IsSuccess);
        Assert.Equal("ok", outcome.Result);
    }

    [Fact]
    public async Task ExecuteOutcomeAsync_PreCancelled_ReturnsCancelledOutcome()
    {
        using var pipeline = RetryPipeline();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcome = await pipeline.ExecuteOutcomeAsync(_ => ValueTask.FromResult(1), new AegisContext(cts.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(outcome.Exception);
    }

    // ---------------------------------------------------------------- Async hata sözleşmesi

    [Fact]
    public void ExecuteAsync_Failure_IsNeverThrownSynchronously()
    {
        using var pipeline = RetryPipeline();

        // Çağrının kendisi fırlatmamalı; hata dönen görevin içinde olmalı (Task.WhenAny / AsTask kullanımları).
        var pending = pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException());
        var voidPending = pipeline.ExecuteAsync(_ => throw new InvalidOperationException());

        Assert.True(pending.IsFaulted);
        Assert.True(voidPending.IsFaulted);
    }

    // ---------------------------------------------------------------- Senkron Execute

    [Fact]
    public void Execute_Sync_ReturnsResultAndRetries()
    {
        using var pipeline = RetryPipeline();
        var calls = 0;

        var result = pipeline.Execute(_ =>
        {
            if (++calls < 3)
            {
                throw new InvalidOperationException();
            }

            return calls;
        });

        Assert.Equal(3, result);
    }

    [Fact]
    public void Execute_Sync_ThrowsOriginalExceptionWithStackTrace()
    {
        using var pipeline = RetryPipeline(0);

        var ex = Assert.Throws<InvalidOperationException>(() => pipeline.Execute<int>(_ => Thrower()));

        Assert.Contains(nameof(Thrower), ex.StackTrace);
    }

    [Fact]
    public void Execute_SyncForms_AllWork()
    {
        using var pipeline = RetryPipeline();
        using var cts = new CancellationTokenSource();
        var hits = 0;

        pipeline.Execute(_ => { hits++; });
        pipeline.Execute(() => { hits++; });
        pipeline.Execute(_ => { hits++; }, cts.Token);
        pipeline.Execute(static (_, box) => { box.Value++; }, new StrongBox<int>());

        Assert.Equal(3, hits);
        Assert.Equal(5, pipeline.Execute(() => 5));
        Assert.Equal(6, pipeline.Execute(ct => ct == cts.Token ? 6 : -1, cts.Token));
        Assert.Equal(7, pipeline.Execute(static (_, s) => s + 1, 6));
    }

    [Fact]
    public void Execute_Sync_WithRetryDelay_WaitsAndSucceeds()
    {
        using var pipeline = new AegisPipelineBuilder("sync-delay")
            .AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.FromMilliseconds(20); o.UseJitter = false; })
            .Build();
        var calls = 0;

        var result = pipeline.Execute(() => ++calls == 1 ? throw new InvalidOperationException() : "ok");

        Assert.Equal("ok", result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Execute_Sync_PreCancelled_Throws()
    {
        using var pipeline = RetryPipeline();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => pipeline.Execute(_ => { }, cts.Token));
    }

    // ---------------------------------------------------------------- Arayüz varsayılanları (özel boru hatları)

    [Fact]
    public async Task CustomPipeline_OnlyImplementingBaseMethods_GetsAllForms()
    {
        IAegisPipeline pipeline = new MinimalPipeline();
        using var cts = new CancellationTokenSource();

        Assert.Equal(3, await pipeline.ExecuteAsync(static (_, s) => ValueTask.FromResult(s), 3));
        Assert.Equal(4, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(4), cts.Token));
        Assert.Equal(5, pipeline.Execute(() => 5));
        Assert.Equal(6, pipeline.Execute(static (_, s) => s, 6));

        var failed = await pipeline.ExecuteOutcomeAsync<int>(_ => throw new FormatException());
        Assert.IsType<FormatException>(failed.Exception);
        Assert.Equal(5, ((MinimalPipeline)pipeline).Calls); // her biçim tek bir temel çağrıya düşer
    }

    private static int Thrower() => throw new InvalidOperationException("sync");

    private sealed class MinimalPipeline : IAegisPipeline
    {
        public int Calls;
        public string Name => "minimal";
        public IReadOnlyList<IAegisStrategy> Strategies => [];

        public ValueTask<TResult> ExecuteAsync<TResult>(Func<AegisContext, ValueTask<TResult>> callback, AegisContext? context = null)
        {
            Calls++;
            return callback(context ?? new AegisContext());
        }

        public ValueTask ExecuteAsync(Func<AegisContext, ValueTask> callback, AegisContext? context = null)
        {
            Calls++;
            return callback(context ?? new AegisContext());
        }

        public void Dispose()
        {
        }
    }
}
