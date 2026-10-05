using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Xunit;

namespace Aegis.Tests;

public class TimeoutAndConcurrencyTests
{
    [Fact]
    public async Task TimeoutStrategy_ShouldThrowAegisTimeoutException_WhenExecutionExceedsLimit()
    {
        // Arrange
        var builder = new AegisPipelineBuilder("TimeoutPipeline");
        builder.AddTimeout(TimeSpan.FromMilliseconds(50));
        var pipeline = builder.Build();

        // Act & Assert
        await Assert.ThrowsAsync<AegisTimeoutException>(async () =>
        {
            await pipeline.ExecuteAsync(async ctx =>
            {
                await Task.Delay(200, ctx.CancellationToken);
                return "Bitemedi";
            });
        });
    }

    [Fact]
    public async Task ConcurrencyLimiter_ShouldThrowRateLimitRejectedException_WhenMaxConcurrencyExceeded()
    {
        // Arrange
        var builder = new AegisPipelineBuilder("ConcurrencyPipeline");
        builder.AddConcurrencyLimiter(1, opt => opt.QueueTimeout = TimeSpan.FromMilliseconds(20));
        var pipeline = builder.Build();

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // İlk işlemi başlat ve beklet
        var task1 = Task.Run(async () =>
        {
            await pipeline.ExecuteAsync(async _ =>
            {
                firstAcquired.SetResult();
                await barrier.Task;
                return 1;
            });
        });

        // Zamana değil, sinyale dayalı bekleme: ilk işlem izni aldığında devam et (deterministik)
        await firstAcquired.Task;

        // İkinci işlem hemen kuyruğu aşmalı ve hata fırlatmalı
        await Assert.ThrowsAsync<RateLimitRejectedException>(async () =>
        {
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult(2));
        });

        barrier.SetResult();
        await task1;
    }

    [Fact]
    public async Task FallbackStrategy_ShouldReturnFallbackValue_OnException()
    {
        // Arrange
        var builder = new AegisPipelineBuilder("FallbackPipeline");
        builder.AddFallback(opt =>
        {
            opt.FallbackHandler = (_, _) => ValueTask.FromResult<object?>("Kurtarılan Varsayılan Değer");
        });
        var pipeline = builder.Build();

        // Act
        var result = await pipeline.ExecuteAsync<string>(_ => throw new InvalidOperationException("Servis çöktü"));

        // Assert
        Assert.Equal("Kurtarılan Varsayılan Değer", result);
    }
}
