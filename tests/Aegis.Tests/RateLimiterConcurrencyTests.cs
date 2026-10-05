using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Distributed.Abstractions;

namespace Aegis.Tests;

/// <summary>
/// Zaman tabanlı kotalar eşzamanlı fırtınada kesin: 64 iş parçacığı aynı anda saldırır, tam olarak izin sayısı kadar çağrı geçer
/// (fazlası değil, eksiği değil). Pencere uzun tutulur ki test sırasında jeton dolmasın.
/// </summary>
public sealed class RateLimiterConcurrencyTests
{
    private const int Threads = 64;
    private const int CallsPerThread = 50;

    // Tüm iş parçacıklarını aynı anda başlatır; geçen ve reddedilen çağrıları sayar. Başka bir istisna testi düşürür.
    private static async Task<(int Admitted, int Rejected)> StormAsync(Func<int, ValueTask> call)
    {
        var admitted = 0;
        var rejected = 0;
        using var start = new ManualResetEventSlim(false);
        var workers = Enumerable.Range(0, Threads).Select(t => Task.Run(async () =>
        {
            start.Wait();
            for (var i = 0; i < CallsPerThread; i++)
            {
                try
                {
                    await call(t * CallsPerThread + i);
                    Interlocked.Increment(ref admitted);
                }
                catch (RateLimitRejectedException)
                {
                    Interlocked.Increment(ref rejected);
                }
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll(workers);
        return (admitted, rejected);
    }

    [Fact]
    public async Task TokenBucket_ConcurrentStorm_AdmitsExactlyPermitLimit()
    {
        var pipeline = new AegisPipelineBuilder("tb").AddRateLimiter(100, TimeSpan.FromHours(1)).Build();

        var (admitted, rejected) = await StormAsync(_ => pipeline.ExecuteAsync(_ => default));

        Assert.Equal(100, admitted);
        Assert.Equal(Threads * CallsPerThread - 100, rejected);
    }

    [Fact]
    public async Task SlidingWindow_ConcurrentStorm_AdmitsExactlyPermitLimit()
    {
        var pipeline = new AegisPipelineBuilder("sw").AddSlidingWindowRateLimiter(100, TimeSpan.FromHours(1), segmentsPerWindow: 6).Build();

        var (admitted, _) = await StormAsync(_ => pipeline.ExecuteAsync(_ => default));

        Assert.Equal(100, admitted);
    }

    // Kiracı başına: her kiracı kendi kotasını tam alır, kiracılar birbirinin kotasını yemez.
    [Fact]
    public async Task Partitioned_ConcurrentStorm_EachTenantGetsExactlyItsQuota()
    {
        var pipeline = new AegisPipelineBuilder("pt").AddPartitionedRateLimiter(o =>
        {
            o.PartitionKeySelector = ctx => ctx.TryGetProperty<string>("tenant", out var t) ? t! : "?";
            o.DefaultOptions = new Aegis.Resilience.Core.Strategies.RateLimiter.RateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromHours(1) };
        }).Build();
        var perTenant = new int[4];

        await StormAsync(async n =>
        {
            var tenant = n % 4;
            var context = new AegisContext();
            context.SetProperty("tenant", $"kiraci-{tenant}");
            await pipeline.ExecuteAsync(_ => default, context);
            Interlocked.Increment(ref perTenant[tenant]);
        });

        Assert.All(perTenant, count => Assert.Equal(30, count));
    }

    // Dağıtık sınırlayıcının bellek içi deposu (Redis kesintisinde de kullanılan yerel sınır): üç algoritma da kesin.
    [Theory]
    [InlineData(DistributedRateLimitAlgorithm.TokenBucket)]
    [InlineData(DistributedRateLimitAlgorithm.FixedWindow)]
    [InlineData(DistributedRateLimitAlgorithm.SlidingWindow)]
    public async Task DistributedInMemoryStore_ConcurrentStorm_AdmitsExactlyPermitLimit(DistributedRateLimitAlgorithm algorithm)
    {
        var pipeline = new AegisPipelineBuilder("dist").AddDistributedRateLimiter(new InMemoryDistributedRateLimitStore(), o =>
        {
            o.LimiterKey = "ortak";
            o.Algorithm = algorithm;
            o.PermitLimit = 100;
            o.TokensPerPeriod = 100;
            o.Window = TimeSpan.FromHours(1);
        }).Build();

        var (admitted, _) = await StormAsync(_ => pipeline.ExecuteAsync(_ => default));

        Assert.Equal(100, admitted);
    }
}
