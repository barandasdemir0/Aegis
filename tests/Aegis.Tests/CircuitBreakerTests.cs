using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Xunit;

namespace Aegis.Tests;

public class CircuitBreakerTests
{
    [Fact]
    public async Task CircuitBreaker_ShouldTripToOpen_WhenFailureRatioExceeded()
    {
        // Arrange
        var openedCalled = false;
        var cbOptions = new CircuitBreakerOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 4,
            SamplingDuration = TimeSpan.FromSeconds(5),
            BreakDuration = TimeSpan.FromMilliseconds(100),
            OnOpened = _ =>
            {
                openedCalled = true;
                return ValueTask.CompletedTask;
            }
        };

        var cbStrategy = new CircuitBreakerStrategy(cbOptions);
        var builder = new AegisPipelineBuilder("CircuitPipeline");
        builder.AddStrategy(cbStrategy);
        var pipeline = builder.Build();

        // 4 istek: 2 başarılı, 2 başarısız -> %50 failure ratio eşiğine ulaşır ve devre açılır
        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1));
        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(2));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()));

        // Assert - Devre artık Open olmalı
        Assert.True(openedCalled);
        Assert.Equal(CircuitState.Open, cbStrategy.State);

        // Sonraki istek hedef servise gitmeden hemen BrokenCircuitException fırlatmalı:
        var targetCalled = false;
        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
        {
            await pipeline.ExecuteAsync(_ =>
            {
                targetCalled = true;
                return ValueTask.FromResult(99);
            });
        });

        Assert.False(targetCalled);
    }

    [Fact]
    public async Task CircuitBreaker_ShouldRecoverToClosed_AfterBreakDurationAndSuccessfulProbe()
    {
        // Arrange
        var cbOptions = new CircuitBreakerOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 2,
            SamplingDuration = TimeSpan.FromSeconds(5),
            BreakDuration = TimeSpan.FromMilliseconds(50) // Kısa bekleme süresi
        };

        var cbStrategy = new CircuitBreakerStrategy(cbOptions);
        var builder = new AegisPipelineBuilder("RecoveryPipeline");
        builder.AddStrategy(cbStrategy);
        var pipeline = builder.Build();

        // Devreyi zorla aç
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()));

        Assert.Equal(CircuitState.Open, cbStrategy.State);

        // BreakDuration'ın geçmesini bekle
        await Task.Delay(70);

        // Şimdi durum HalfOpen olmalı ve başarılı deneme devreyi tekrar Closed yapmalı
        var result = await pipeline.ExecuteAsync(_ => ValueTask.FromResult("İyileşti"));

        Assert.Equal("İyileşti", result);
        Assert.Equal(CircuitState.Closed, cbStrategy.State);
    }
}
