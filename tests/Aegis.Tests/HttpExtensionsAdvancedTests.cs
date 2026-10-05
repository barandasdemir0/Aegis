using System.Net;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.Http;
using Xunit;

namespace Aegis.Tests;

public class HttpExtensionsAdvancedTests
{
    [Fact]
    public void HttpRequestMessage_GetAndSetAegisContext_ShouldPreserveContextAndCorrelationId()
    {
        // Arrange
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.com/v1/quotes");
        var customContext = new AegisContext(CancellationToken.None, "CustomPipeline");
        customContext.CorrelationId = "custom-corr-12345";
        customContext.SetProperty("UserKey", "Baran");

        // Act: Context ata
        request.SetAegisContext(customContext);

        // Assert: Context geri oku
        var retrieved = request.GetAegisContext();
        Assert.NotNull(retrieved);
        Assert.Equal("CustomPipeline", retrieved.PipelineName);
        Assert.Equal("custom-corr-12345", retrieved.CorrelationId);
        Assert.True(retrieved.TryGetProperty<string>("UserKey", out var user));
        Assert.Equal("Baran", user);
    }

    [Fact]
    public void HttpRequestMessage_GetOrCreateAegisContext_ShouldCreateIfAbsent()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.com/v1/ping");

        var context1 = request.GetOrCreateAegisContext("DefaultHttp");
        Assert.NotNull(context1);
        Assert.Equal("DefaultHttp", context1.PipelineName);

        // İkinci çağrıda aynı nesne dönmeli
        var context2 = request.GetOrCreateAegisContext("Other");
        Assert.Same(context1, context2);
    }

    private sealed class EchoHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        /// <summary>İstek sürerken bağlamın boru hattı adı (işleyici bağlamı istek bitince havuza iade eder).</summary>
        public string? LastPipelineName { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastPipelineName = request.GetAegisContext()?.PipelineName;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task AegisDynamicResilienceHandler_ShouldRouteToCorrectPipeline_BasedOnHost()
    {
        // Arrange: 2 farklı host için 2 farklı pipeline
        var registry = new AegisPipelineRegistry();

        var fastPipeline = new AegisPipelineBuilder("fast.api.com")
            .AddRetry(opt => opt.MaxRetryAttempts = 1)
            .Build();
        registry.RegisterPipeline("fast.api.com", fastPipeline);

        var slowPipeline = new AegisPipelineBuilder("slow.api.com")
            .AddRetry(opt => opt.MaxRetryAttempts = 2)
            .Build();
        registry.RegisterPipeline("slow.api.com", slowPipeline);

        // Dynamic Handler oluştur
        var dynamicHandler = new AegisDynamicResilienceHandler(registry, req => req.RequestUri?.Host ?? "default");
        var echo = new EchoHandler();
        typeof(DelegatingHandler)
            .GetProperty("InnerHandler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(dynamicHandler, echo);

        var invoker = new HttpMessageInvoker(dynamicHandler);

        // Act: fast.api.com'a istek
        using var fastReq = new HttpRequestMessage(HttpMethod.Get, "https://fast.api.com/data");
        var res1 = await invoker.SendAsync(fastReq, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        Assert.Equal("fast.api.com", echo.LastPipelineName);

        // Act: slow.api.com'a istek
        using var slowReq = new HttpRequestMessage(HttpMethod.Get, "https://slow.api.com/data");
        var res2 = await invoker.SendAsync(slowReq, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        Assert.Equal("slow.api.com", echo.LastPipelineName);
    }
}
