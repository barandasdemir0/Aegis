using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Retry;
using Xunit;

namespace Aegis.Tests;

public class RetryStrategyTests
{
    [Fact]
    public async Task RetryStrategy_ShouldRetry_UpToMaxAttempts()
    {
        // Arrange
        var attempts = 0;
        var onRetryCount = 0;

        var builder = new AegisPipelineBuilder("TestPipeline");
        builder.AddRetry(options =>
        {
            options.MaxRetryAttempts = 3;
            options.Delay = TimeSpan.FromMilliseconds(10);
            options.UseJitter = false;
            options.OnRetry = _ =>
            {
                onRetryCount++;
                return ValueTask.CompletedTask;
            };
        });

        var pipeline = builder.Build();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await pipeline.ExecuteAsync<string>(_ =>
            {
                attempts++;
                throw new InvalidOperationException("Geçici Hata");
            });
        });

        // 1 ilk deneme + 3 retry = toplam 4 deneme
        Assert.Equal(4, attempts);
        Assert.Equal(3, onRetryCount);
    }

    [Fact]
    public async Task RetryStrategy_ShouldSucceed_WhenTemporaryErrorResolves()
    {
        // Arrange
        var attempts = 0;
        var builder = new AegisPipelineBuilder("TestPipeline");
        builder.AddRetry(options =>
        {
            options.MaxRetryAttempts = 3;
            options.Delay = TimeSpan.FromMilliseconds(5);
        });

        var pipeline = builder.Build();

        // Act
        var result = await pipeline.ExecuteAsync(_ =>
        {
            attempts++;
            if (attempts < 3)
            {
                throw new HttpRequestException("Ağ koptu");
            }
            return ValueTask.FromResult("Başarılı");
        });

        // Assert
        Assert.Equal("Başarılı", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RetryStrategy_ShouldNotRetry_WhenShouldHandleReturnsFalse()
    {
        // Arrange
        var attempts = 0;
        var builder = new AegisPipelineBuilder("TestPipeline");
        builder.AddRetry(options =>
        {
            options.MaxRetryAttempts = 3;
            options.ShouldHandle = ex => ex is TimeoutException; // Sadece TimeoutException
        });

        var pipeline = builder.Build();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await pipeline.ExecuteAsync<int>(_ =>
            {
                attempts++;
                throw new ArgumentException("Kalıcı hata");
            });
        });

        Assert.Equal(1, attempts);
    }
}
