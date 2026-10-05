using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Distributed.Abstractions;
using Xunit;

namespace Aegis.Tests;

public class DistributedStateStoreTests
{
    [Fact]
    public async Task InMemoryStore_DefaultState_ShouldBeClosed()
    {
        var store = new InMemoryCircuitBreakerStateStore();
        var state = await store.GetStateAsync("test-circuit");

        Assert.Equal(CircuitState.Closed, state);
    }

    [Fact]
    public async Task InMemoryStore_SetStateOpen_ShouldExpireToHalfOpen_AfterTtl()
    {
        var store = new InMemoryCircuitBreakerStateStore();
        var ttl = TimeSpan.FromMilliseconds(50);

        await store.SetStateAsync("test-circuit", CircuitState.Open, ttl);

        var immediateState = await store.GetStateAsync("test-circuit");
        Assert.Equal(CircuitState.Open, immediateState);

        await Task.Delay(70);

        var expiredState = await store.GetStateAsync("test-circuit");
        Assert.Equal(CircuitState.HalfOpen, expiredState);
    }

    [Fact]
    public async Task InMemoryStore_RecordResult_ShouldAccumulateAndResetWindow()
    {
        var store = new InMemoryCircuitBreakerStateStore();
        var sampling = TimeSpan.FromMilliseconds(50);

        var (s1, f1) = await store.RecordResultAsync("stats-circuit", isSuccess: true, sampling);
        Assert.Equal(1, s1);
        Assert.Equal(0, f1);

        var (s2, f2) = await store.RecordResultAsync("stats-circuit", isSuccess: false, sampling);
        Assert.Equal(1, s2);
        Assert.Equal(1, f2);

        // Pencere süresini bekle
        await Task.Delay(70);

        var (s3, f3) = await store.RecordResultAsync("stats-circuit", isSuccess: true, sampling);
        Assert.Equal(1, s3);
        Assert.Equal(0, f3); // Önceki pencere sıfırlandı
    }

    [Fact]
    public async Task InMemoryStore_ConcurrentAccess_ShouldBeThreadSafe()
    {
        var store = new InMemoryCircuitBreakerStateStore();
        var tasks = new List<Task>();
        const int concurrentTasks = 20;

        for (int i = 0; i < concurrentTasks; i++)
        {
            var isSuccess = i % 2 == 0;
            tasks.Add(Task.Run(async () =>
            {
                await store.RecordResultAsync("concurrent-circuit", isSuccess, TimeSpan.FromSeconds(5));
                await store.GetStateAsync("concurrent-circuit");
            }));
        }

        await Task.WhenAll(tasks);

        var (successes, failures) = await store.RecordResultAsync("concurrent-circuit", isSuccess: true, TimeSpan.FromSeconds(5));
        // 10 success + 10 failures + 1 son success = 11 successes, 10 failures
        Assert.Equal(11, successes);
        Assert.Equal(10, failures);
    }
}
