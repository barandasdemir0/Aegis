using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Chaos;
using Aegis.Resilience.Core.Strategies.Retry;
using Xunit;

namespace Aegis.Tests;

public class DynamicOptionsAndChaosTests
{
    [Fact]
    public async Task DynamicOptions_ShouldReloadAtRuntime_WithoutRebuildingPipeline()
    {
        // Arrange
        // Başlangıçta MaxRetryAttempts = 1
        var currentOptions = new RetryOptions
        {
            MaxRetryAttempts = 1,
            Delay = TimeSpan.FromMilliseconds(5),
            UseJitter = false
        };

        var builder = new AegisPipelineBuilder("DynamicPipeline");
        // OptionsProvider delegasyonu veriyoruz:
        builder.AddRetry(new RetryOptions
        {
            OptionsProvider = () => currentOptions
        });

        var pipeline = builder.Build();

        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await pipeline.ExecuteAsync<int>(_ =>
            {
                attempts++;
                throw new InvalidOperationException();
            });
        });

        // İlk çalışmada: 1 ilk çağrı + 1 retry = 2
        Assert.Equal(2, attempts);

        // Canlıda ayarları değiştiriyoruz (Zero-Downtime Hot Reloading):
        currentOptions = new RetryOptions
        {
            MaxRetryAttempts = 4,
            Delay = TimeSpan.FromMilliseconds(5),
            UseJitter = false
        };

        attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await pipeline.ExecuteAsync<int>(_ =>
            {
                attempts++;
                throw new InvalidOperationException();
            });
        });

        // Yeni ayar pipeline rebuild edilmeden ANINDA devreye girdi:
        // 1 ilk çağrı + 4 retry = 5
        Assert.Equal(5, attempts);
    }

    [Fact]
    public async Task ChaosStrategy_ShouldInjectFault_WhenEnabled()
    {
        // Arrange
        var builder = new AegisPipelineBuilder("ChaosPipeline");
        builder.AddChaos(opt =>
        {
            opt.Enabled = true;
            opt.InjectionRate = 1.0; // %100 kesin hata enjeksiyonu
            opt.FaultGenerator = () => new ChaosInjectedException("Kaos Testi");
        });

        var pipeline = builder.Build();

        // Act & Assert
        await Assert.ThrowsAsync<ChaosInjectedException>(async () =>
        {
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult("Asla dönmemeli"));
        });
    }
}
