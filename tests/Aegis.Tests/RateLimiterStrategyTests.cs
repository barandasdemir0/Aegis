using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Xunit;

namespace Aegis.Tests;

public class RateLimiterStrategyTests
{
    [Fact]
    public async Task RateLimiter_ShouldAllowRequests_WithinPermitLimit()
    {
        // Arrange: 3 permit limit, 1 second window
        var builder = new AegisPipelineBuilder("RateLimitAllowedPipeline");
        builder.AddRateLimiter(permitLimit: 3, window: TimeSpan.FromSeconds(1));
        var pipeline = builder.Build();

        // Act & Assert
        for (int i = 0; i < 3; i++)
        {
            var res = await pipeline.ExecuteAsync(_ => ValueTask.FromResult(i));
            Assert.Equal(i, res);
        }
    }

    [Fact]
    public async Task RateLimiter_ShouldThrowRateLimitRejectedException_WhenPermitLimitExceeded()
    {
        // Arrange: 2 permit limit
        var builder = new AegisPipelineBuilder("RateLimitExceededPipeline");
        builder.AddRateLimiter(permitLimit: 2, window: TimeSpan.FromSeconds(1));
        var pipeline = builder.Build();

        // İlk 2 istek geçer
        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1));
        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(2));

        // 3. istek RateLimitRejectedException fırlatmalı:
        await Assert.ThrowsAsync<RateLimitRejectedException>(async () =>
        {
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult(3));
        });
    }

    [Fact]
    public async Task RateLimiter_ShouldReplenish_AfterWindowElapses()
    {
        // Bu test iki bağımsız koşulu doğrular. Yük altında zamanlama kayması yaşanmaması için
        // her koşul kendi penceresiyle ölçülür:
        //  (a) Pencere dolmadan gelen istek reddedilir  -> geniş pencere (jeton erken dolmaz)
        //  (b) Pencere dolduktan sonra kota yenilenir    -> dar pencere (beklemenin uzaması sonucu bozmaz)

        // (a) Kota tükendiğinde reddetme
        var strictBuilder = new AegisPipelineBuilder("RateLimitRejectPipeline");
        strictBuilder.AddRateLimiter(permitLimit: 1, window: TimeSpan.FromSeconds(30));
        var strictPipeline = strictBuilder.Build();

        var first = await strictPipeline.ExecuteAsync(_ => ValueTask.FromResult("OK1"));
        Assert.Equal("OK1", first);

        await Assert.ThrowsAsync<RateLimitRejectedException>(async () =>
        {
            await strictPipeline.ExecuteAsync(_ => ValueTask.FromResult("FAIL"));
        });

        // (b) Pencere dolduktan sonra yenilenme
        var replenishBuilder = new AegisPipelineBuilder("RateLimitReplenishPipeline");
        replenishBuilder.AddRateLimiter(permitLimit: 1, window: TimeSpan.FromMilliseconds(100));
        var replenishPipeline = replenishBuilder.Build();

        await replenishPipeline.ExecuteAsync(_ => ValueTask.FromResult("OK1"));
        await Task.Delay(500);

        var second = await replenishPipeline.ExecuteAsync(_ => ValueTask.FromResult("OK2"));
        Assert.Equal("OK2", second);
    }

    [Fact]
    public async Task RateLimiter_ShouldRespectCancellationToken()
    {
        var builder = new AegisPipelineBuilder("CancelRateLimitPipeline");
        builder.AddRateLimiter(permitLimit: 1, window: TimeSpan.FromSeconds(10));
        var pipeline = builder.Build();

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Önceden iptal edildi

        var context = Aegis.Resilience.Core.Context.AegisContext.Create(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1), context);
        });
    }
}
