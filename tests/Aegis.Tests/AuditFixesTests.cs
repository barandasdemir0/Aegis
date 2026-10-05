using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Extensions.Http;
using Xunit;

namespace Aegis.Tests;

public class AuditFixesTests
{
    // 1. Circuit Breaker HalfOpen Single-Probe Koruması
    [Fact]
    public async Task CircuitBreaker_HalfOpen_ShouldAllowOnlySingleProbe_AndRejectConcurrentRequests()
    {
        var pipeline = new AegisPipelineBuilder("HalfOpenProbePipeline")
            .AddCircuitBreaker(opt =>
            {
                opt.MinimumThroughput = 1;
                opt.FailureRatio = 1.0;
                opt.BreakDuration = TimeSpan.FromMilliseconds(50);
            })
            .Build();

        // 1. Devreyi Open durumuna geçir
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await pipeline.ExecuteAsync<string>(_ => throw new HttpRequestException("Fail 1"));
        });

        // 2. BreakDuration (50ms) süresinin dolmasını bekle
        await Task.Delay(80);

        // 3. İlk gelen thread probe isteğini başlatır ve testin kontrolünde (deterministik) tutar
        var probeStartedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeReleaseTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeTask = Task.Run(async () =>
        {
            return await pipeline.ExecuteAsync(async _ =>
            {
                probeStartedTcs.SetResult();
                await probeReleaseTcs.Task;
                return "ProbeSuccess";
            });
        });

        await probeStartedTcs.Task;

        // 4. Probe devam ederken aynı anda gelen 5 istek derhal BrokenCircuitException ile reddedilmelidir
        var concurrentTasks = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
            {
                await pipeline.ExecuteAsync(_ => ValueTask.FromResult("ShouldBeRejected"));
            });
        })).ToArray();

        await Task.WhenAll(concurrentTasks);

        // 5. Probe tamamlanınca devre Closed olmalıdır
        probeReleaseTcs.SetResult();
        var probeResult = await probeTask;
        Assert.Equal("ProbeSuccess", probeResult);

        // Artık yeni istekler normal geçmelidir
        var normalResult = await pipeline.ExecuteAsync(_ => ValueTask.FromResult("NormalOK"));
        Assert.Equal("NormalOK", normalResult);
    }

    // 2. Sayısal Taşma (OverflowException) Koruması Testi
    [Fact]
    public void RetryStrategy_CalculateDelay_ShouldNotThrow_OnLargeAttemptNumbers()
    {
        var options = new RetryOptions
        {
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromMilliseconds(500),
            MaxDelay = TimeSpan.FromSeconds(30),
            UseJitter = false
        };

        // 50. attempt: 2^49 normalde TimeSpan.FromMilliseconds'ı taşırır ve OverflowException fırlatır
        var delay = RetryStrategy.CalculateDelay(50, options);

        Assert.Equal(options.MaxDelay, delay);
    }

    private sealed class MockBackendHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

        public MockBackendHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request, cancellationToken));
        }
    }

    // 3. HttpClient Retry Gövde ve Klonlama Testi (The request message was already sent hatası önlendi)
    [Fact]
    public async Task HttpClient_AegisResilienceHandler_ShouldSuccessfullyRetry_PostRequestWithBody()
    {
        var attempts = 0;
        var mockBackend = new MockBackendHandler((req, ct) =>
        {
            attempts++;
            if (attempts == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError); // İlk deneme 500
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"success\"}", Encoding.UTF8, "application/json")
            };
        });

        var pipeline = new AegisPipelineBuilder("HttpRetryPipeline")
            .AddRetry(opt =>
            {
                opt.MaxRetryAttempts = 2;
                opt.Delay = TimeSpan.FromMilliseconds(5);
            })
            .Build();

        var handler = new AegisResilienceHandler(pipeline, handleHttpFailureStatuses: true);
        typeof(DelegatingHandler).GetProperty("InnerHandler", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(handler, mockBackend);

        var invoker = new HttpMessageInvoker(handler);

        // Gövdeli POST isteği oluştur (Idempotency-Key ile güvenli retry - AEGIS-103)
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.com/v1/orders")
        {
            Content = new StringContent("{\"orderId\":12345}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", "order-12345-idempotency-key");

        // İkinci denemede "The request message was already sent" hatası vermemeli, başarıyla 200 dönmeli
        var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempts);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("{\"status\":\"success\"}", body);
    }

    // 4. Retry Stratejisinin HTTP Retry-After Başlığını Otomatik Uygulaması
    [Fact]
    public async Task RetryStrategy_ShouldAutomaticallyApply_HttpRetryAfterHeader()
    {
        var pipeline = new AegisPipelineBuilder("RetryAfterAutoPipeline")
            .AddRetry(opt =>
            {
                opt.MaxRetryAttempts = 1;
                opt.Delay = TimeSpan.FromSeconds(30); // Statik değer 30 sn
            })
            .Build();

        var context = new AegisContext();
        // HTTP Handler'ın ayrıştırıp bağlama koyduğu Retry-After süresi:
        context.Properties["Aegis.Http.RetryAfterDelay"] = TimeSpan.FromMilliseconds(20);

        var attempts = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var result = await pipeline.ExecuteAsync(ctx =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new HttpRequestException("429 Too Many Requests");
            }
            return ValueTask.FromResult("RetriedWithHeaderDelay");
        }, context);

        sw.Stop();

        Assert.Equal("RetriedWithHeaderDelay", result);
        Assert.Equal(2, attempts);
        // 30 saniye yerine 20ms'lik Retry-After süresi kullanıldığı için 1 saniyeden çok daha kısa sürede biter
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Süre: {sw.ElapsedMilliseconds}ms");
    }
}
