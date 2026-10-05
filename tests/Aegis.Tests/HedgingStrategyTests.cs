using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Hedging;
using Xunit;

namespace Aegis.Tests;

public class HedgingStrategyTests
{
    [Fact]
    public async Task Hedging_PrimaryFast_ShouldReturnFastResult_WithoutHedging()
    {
        // Arrange
        var invocationCount = 0;
        var builder = new AegisPipelineBuilder("FastHedgingPipeline");
        builder.AddHedging(opt =>
        {
            opt.HedgingDelay = TimeSpan.FromMilliseconds(200);
            opt.MaxHedgedAttempts = 1;
        });

        var pipeline = builder.Build();

        // Act
        var result = await pipeline.ExecuteAsync(async _ =>
        {
            Interlocked.Increment(ref invocationCount);
            await Task.Delay(10); // HedgingDelay'den çok daha kısa
            return "PrimarySuccess";
        });

        // Assert
        Assert.Equal("PrimarySuccess", result);
        Assert.Equal(1, invocationCount); // Sadece birincil çalıştı
    }

    [Fact]
    public async Task Hedging_PrimarySlow_HedgedWins_ShouldReturnHedgedResult()
    {
        // Arrange
        var invocationCount = 0;
        var builder = new AegisPipelineBuilder("SlowHedgingPipeline");
        builder.AddHedging(opt =>
        {
            opt.HedgingDelay = TimeSpan.FromMilliseconds(50);
            opt.MaxHedgedAttempts = 1;
        });

        var pipeline = builder.Build();

        // Act
        var result = await pipeline.ExecuteAsync(async ctx =>
        {
            var id = Interlocked.Increment(ref invocationCount);
            if (id == 1)
            {
                // İlk istek takıldı/yavaşladı (300ms)
                await Task.Delay(300, ctx.CancellationToken);
                return "SlowPrimary";
            }
            else
            {
                // İkinci (Hedged) istek anında döndü (10ms)
                await Task.Delay(10, ctx.CancellationToken);
                return "FastHedged";
            }
        });

        // Assert
        Assert.Equal("FastHedged", result);
        Assert.True(invocationCount >= 2);
    }

    [Fact]
    public async Task Hedging_PrimaryFaults_HedgedSucceeds_ShouldReturnHedgedResult()
    {
        // Arrange
        var invocationCount = 0;
        var builder = new AegisPipelineBuilder("FaultToleranceHedgingPipeline");
        builder.AddHedging(opt =>
        {
            opt.HedgingDelay = TimeSpan.FromMilliseconds(30);
            opt.MaxHedgedAttempts = 1;
        });

        var pipeline = builder.Build();

        // Act
        var result = await pipeline.ExecuteAsync(async ctx =>
        {
            var id = Interlocked.Increment(ref invocationCount);
            if (id == 1)
            {
                await Task.Delay(50, ctx.CancellationToken);
                throw new InvalidOperationException("Birincil istek çöktü!");
            }

            await Task.Delay(10, ctx.CancellationToken);
            return "HedgedSuccess";
        });

        // Assert
        Assert.Equal("HedgedSuccess", result);
    }

    [Fact]
    public async Task Hedging_ShouldRespectCancellationToken()
    {
        // Arrange
        var builder = new AegisPipelineBuilder("CancelHedgingPipeline");
        builder.AddHedging(opt =>
        {
            opt.HedgingDelay = TimeSpan.FromMilliseconds(50);
            opt.MaxHedgedAttempts = 1;
        });

        var pipeline = builder.Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        var context = Aegis.Resilience.Core.Context.AegisContext.Create(cts.Token);

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await pipeline.ExecuteAsync(async ctx =>
            {
                await Task.Delay(500, ctx.CancellationToken);
                return "Completed";
            }, context);
        });
    }
}
