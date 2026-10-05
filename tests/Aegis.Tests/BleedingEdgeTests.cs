using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Fallback;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Xunit;

namespace Aegis.Tests;

public class BleedingEdgeTests
{
    [Fact]
    public async Task StaleFallback_ShouldReturnCachedData_WhenTargetServiceFails()
    {
        // Arrange
        var pipeline = new AegisPipelineBuilder("ExchangeRatePipeline")
            .AddStaleFallback(opt =>
            {
                opt.MaxStaleAge = TimeSpan.FromMinutes(10);
            })
            .Build();

        var shouldFail = false;
        var fetchRates = (AegisContext ctx) =>
        {
            if (shouldFail)
            {
                throw new HttpRequestException("Merkez Bankası API çöktü");
            }
            return ValueTask.FromResult(34.85); // USD/TRY
        };

        // 1. İlk çağrı: Taze veri alınır ve önbelleğe yazılır
        var context1 = new AegisContext(CancellationToken.None, "ExchangeRatePipeline");
        var rate1 = await pipeline.ExecuteAsync(fetchRates, context1);

        Assert.Equal(34.85, rate1);
        Assert.True(context1.TryGetProperty<bool>(StaleFallbackOptions.IsStaleDataKey, out var isStale1));
        Assert.False(isStale1); // Veri taze

        // 2. İkinci çağrı: Servis çöktü!
        shouldFail = true;
        var context2 = new AegisContext(CancellationToken.None, "ExchangeRatePipeline");
        var rate2 = await pipeline.ExecuteAsync(fetchRates, context2);

        // Assert: Kullanıcı hata almadı, son bilinen kur döndü!
        Assert.Equal(34.85, rate2);
        Assert.True(context2.TryGetProperty<bool>(StaleFallbackOptions.IsStaleDataKey, out var isStale2));
        Assert.True(isStale2); // Veri bayat (Stale) olduğunu bildirdi
    }

    [Fact]
    public async Task AdaptiveConcurrency_ShouldDynamicallyRegulateLoad()
    {
        // Arrange
        var strategy = new AdaptiveConcurrencyStrategy(new AdaptiveConcurrencyOptions
        {
            InitialConcurrency = 10,
            MinConcurrency = 2,
            MaxConcurrency = 50,
            SmoothingFactor = 0.5
        });

        var pipeline = new AegisPipelineBuilder("AdaptivePipeline")
            .AddStrategy(strategy)
            .Build();

        // 1. Anlık yanıtlar (ideal durum)
        for (var i = 0; i < 5; i++)
        {
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult(true));
        }

        var baselineLimit = strategy.CurrentLimit;
        Assert.True(baselineLimit >= 10);

        // 2. Ağır gecikmeler simüle edildiğinde limit kısılmalı.
        // 100ms, Windows timer çözünürlüğünün (~15.6ms) çok üzerinde olduğu için ölçüm güvenilirdir;
        // anlık (~0ms) taban çizgisine göre tolerans çizgisi (max(5ms, 2x)) net biçimde aşılır.
        for (var i = 0; i < 8; i++)
        {
            await pipeline.ExecuteAsync(async _ =>
            {
                await Task.Delay(100);
                return true;
            });
        }

        var throttledLimit = strategy.CurrentLimit;
        Assert.True(throttledLimit < baselineLimit, $"limit daralmadı: {baselineLimit} -> {throttledLimit}");
        Assert.True(throttledLimit <= 5, $"ciddi yavaşlamada limit yeterince daralmadı: {throttledLimit}");
    }
}
