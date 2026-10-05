using System.Collections.Concurrent;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Pipeline;

namespace Aegis.Tests;

/// <summary>
/// Polly eşitliği: <c>ResilienceContext.ContinueOnCapturedContext</c> (Polly testi: ExecuteAsync_EnsureCallbackCalledOnCapturedContext
/// benzeri). Bağlam gerektiren ortamlarda (UI, eski ASP.NET) strateji gecikmesinden sonraki deneme özgün bağlamda çalışmalıdır.
/// </summary>
public class ContinueOnCapturedContextTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retry_AfterDelay_ResumesOnCapturedContextOnlyWhenRequested(bool continueOnCapturedContext)
    {
        using var pipeline = new AegisPipelineBuilder("sync-ctx")
            .AddTimeout(TimeSpan.FromSeconds(10))
            .AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.FromMilliseconds(20); o.UseJitter = false; })
            .AddCircuitBreaker(o => o.MinimumThroughput = 100)
            .Build();

        using var syncContext = new SingleThreadSynchronizationContext();
        var attempts = new List<SynchronizationContext?>();

        var result = await syncContext.RunAsync(async () =>
        {
            var context = new AegisContext { ContinueOnCapturedContext = continueOnCapturedContext };
            return await pipeline.ExecuteAsync(ctx =>
            {
                attempts.Add(SynchronizationContext.Current);
                return attempts.Count == 1 ? throw new InvalidOperationException() : ValueTask.FromResult(attempts.Count);
            }, context);
        });

        Assert.Equal(2, result);
        Assert.Same(syncContext, attempts[0]); // ilk deneme her zaman çağıranın bağlamında
        if (continueOnCapturedContext)
        {
            Assert.Same(syncContext, attempts[1]);
        }
        else
        {
            Assert.Null(attempts[1]); // varsayılan: iş parçacığı havuzunda devam (eski davranış)
        }
    }

    [Fact]
    public void ContinueOnCapturedContext_FlowsToChildAndIsClearedOnReset()
    {
        var context = new AegisContext { ContinueOnCapturedContext = true };

        Assert.True(context.CreateChild(CancellationToken.None).ContinueOnCapturedContext);

        context.Reset();
        Assert.False(context.ContinueOnCapturedContext);
    }

    /// <summary>Tüm gönderileri tek bir ayrılmış iş parçacığında çalıştıran bağlam (UI iş parçacığı benzeri).</summary>
    private sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;

        public SingleThreadSynchronizationContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                {
                    callback(state);
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public Task<T> RunAsync<T>(Func<Task<T>> work)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try
                {
                    completion.SetResult(await work());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            }, null);
            return completion.Task;
        }

        public void Dispose() => _queue.CompleteAdding();
    }
}
