using System.Net;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.Http;
using Xunit;

namespace Aegis.Tests;

public class PipelineLifecycleAndHandlerTests
{
    private sealed class TrackingDisposableStrategy : Aegis.Resilience.Core.Abstractions.IAegisStrategy, IDisposable
    {
        public bool IsDisposed { get; private set; }
        public string Name => "TrackingDisposable";

        public ValueTask<TResult> ExecuteAsync<TResult>(
            Func<Aegis.Resilience.Core.Context.AegisContext, ValueTask<TResult>> callback,
            Aegis.Resilience.Core.Context.AegisContext context) => callback(context);

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    [Fact]
    public void AegisPipeline_Dispose_ShouldDisposeAllDisposableStrategies()
    {
        // Arrange
        var strategy = new TrackingDisposableStrategy();
        var builder = new AegisPipelineBuilder("DisposablePipeline");
        builder.AddStrategy(strategy);
        var pipeline = builder.Build();

        Assert.False(strategy.IsDisposed);

        // Act
        pipeline.Dispose();

        // Assert
        Assert.True(strategy.IsDisposed);
    }

    [Fact]
    public void AegisPipelineRegistry_ShouldThrow_WhenConfiguratorNeedsServiceProviderButNoneProvided()
    {
        // Arrange
        var configurator = new DelegateAegisPipelineConfigurator("ConfiguredPipeline", (b, sp) => { });
        var registry = new AegisPipelineRegistry(null, new[] { configurator });

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => registry.GetPipeline("ConfiguredPipeline"));
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        public TrackingResponse? LastResponse { get; private set; }

        public StubHttpMessageHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastResponse = new TrackingResponse(_statusCode);
            return Task.FromResult<HttpResponseMessage>(LastResponse);
        }
    }

    private sealed class TrackingResponse : HttpResponseMessage
    {
        public bool IsDisposed { get; private set; }

        public TrackingResponse(HttpStatusCode statusCode) : base(statusCode) { }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            IsDisposed = true;
        }
    }

    [Fact]
    public async Task AegisResilienceHandler_ShouldDisposeResponse_OnTransientHttpFailure()
    {
        // Arrange
        var pipeline = new AegisPipelineBuilder("TestHttpPipeline").Build();
        var handler = new AegisResilienceHandler(pipeline, handleHttpFailureStatuses: true);
        var stub = new StubHttpMessageHandler(HttpStatusCode.ServiceUnavailable); // 503

        var invoker = new HttpMessageInvoker(handler);
        // DelegatingHandler innerHandler atanmalı:
        typeof(DelegatingHandler)
            .GetProperty("InnerHandler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(handler, stub);

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.com/data");
            await invoker.SendAsync(request, CancellationToken.None);
        });

        // Response dispose edilmiş olmalı (soket sızıntısı önlendi):
        Assert.NotNull(stub.LastResponse);
        Assert.True(stub.LastResponse.IsDisposed);
    }
}
