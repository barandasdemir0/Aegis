using Microsoft.Extensions.Diagnostics.HealthChecks;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.DependencyInjection.Aop;
using Aegis.Resilience.Extensions.HealthChecks;
using Xunit;

namespace Aegis.Tests;

public interface IOrderService
{
    [AegisPolicy("OrderRetryPipeline")]
    Task<string> CreateOrderAsync(string orderId);
}

public class OrderService : IOrderService
{
    public int Calls = 0;

    public Task<string> CreateOrderAsync(string orderId)
    {
        Calls++;
        if (Calls < 3)
        {
            throw new HttpRequestException("Geçici Ağ Hatası");
        }
        return Task.FromResult($"Sipariş Başarılı: {orderId}");
    }
}

public class AdvancedFeaturesTests
{
    [Fact]
    public void ContextPool_ShouldRentAndReturn_WithProperReset()
    {
        // Arrange & Act
        var ctx = AegisContextPool.Rent(CancellationToken.None, "TestPipeline");
        ctx.SetProperty("UserId", 42);

        Assert.Equal("TestPipeline", ctx.PipelineName);
        Assert.True(ctx.TryGetProperty<int>("UserId", out var userId));
        Assert.Equal(42, userId);

        AegisContextPool.Return(ctx);

        var ctx2 = AegisContextPool.Rent(CancellationToken.None, "NewPipeline");

        // Assert - properties sıfırlanmış olmalı
        Assert.Equal("NewPipeline", ctx2.PipelineName);
        Assert.False(ctx2.TryGetProperty<int>("UserId", out _));

        AegisContextPool.Return(ctx2);
    }

    [Fact]
    public async Task AopDispatchProxy_ShouldAutomaticallyApplyResilience_WithoutManualTryCatch()
    {
        // Arrange
        var registry = new AegisPipelineRegistry(null!, Enumerable.Empty<IAegisPipelineConfigurator>());
        var pipeline = new AegisPipelineBuilder("OrderRetryPipeline")
            .AddRetry(opt =>
            {
                opt.MaxRetryAttempts = 3;
                opt.Delay = TimeSpan.FromMilliseconds(5);
                opt.UseJitter = false;
            })
            .Build();

        registry.RegisterPipeline("OrderRetryPipeline", pipeline);

        var targetService = new OrderService();
        var proxy = AegisDispatchProxy<IOrderService>.Create(targetService, registry);

        // Act - Doğrudan arayüz metodunu çağırıyoruz:
        var result = await proxy.CreateOrderAsync("ORD-101");

        // Assert
        Assert.Equal("Sipariş Başarılı: ORD-101", result);
        Assert.Equal(3, targetService.Calls); // 2 hata aldı, 3. denemede başardı!
    }

    [Fact]
    public async Task HealthCheck_ShouldReportDegraded_WhenCircuitBreakerIsOpen()
    {
        // Arrange
        var registry = new AegisPipelineRegistry(null!, Enumerable.Empty<IAegisPipelineConfigurator>());
        var cbStrategy = new CircuitBreakerStrategy(new CircuitBreakerOptions
        {
            MinimumThroughput = 1,
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(5),
            BreakDuration = TimeSpan.FromSeconds(10)
        });

        var pipeline = new AegisPipelineBuilder("PaymentService")
            .AddStrategy(cbStrategy)
            .Build();

        registry.RegisterPipeline("PaymentService", pipeline);

        var healthCheck = new AegisHealthCheck(registry);

        // 1. Durum Kapalıyken Healthy olmalı
        var health1 = await healthCheck.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, health1.Status);

        // 2. Devreyi zorla açıyoruz
        cbStrategy.Isolate();

        // 3. Durum Açıkken Degraded dönmeli
        var health2 = await healthCheck.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, health2.Status);
    }
}
