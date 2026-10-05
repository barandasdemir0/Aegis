using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Extensions.Http;

namespace Aegis.Tests;

/// <summary>
/// Microsoft.Extensions.Http.Resilience 10.x karşısında kapatılan boşluklar: geçici hata tanımı (tüm 5xx + 408 + 429),
/// bağlantı kurma zaman aşımının yeniden denenmesi, <c>ShouldRetryAfterHeader</c>, servis sağlayıcılı standart işleyici,
/// anahtar başına DI bağlamlı boru hattı (<c>SelectPipelineBy</c> + <c>InstanceName</c>), <c>GetRequestMessage</c>.
/// </summary>
public class MicrosoftHttpParityTests
{
    /// <summary>Sıradaki yanıtları/istisnaları sırayla döner; tükenince sonuncuyu tekrarlar.</summary>
    private sealed class ScriptedServer(params Func<HttpRequestMessage, HttpResponseMessage>[] script) : HttpMessageHandler
    {
        private int _calls;

        public ConcurrentQueue<Uri?> Requests { get; } = new();

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri);
            var index = Math.Min(Interlocked.Increment(ref _calls), script.Length) - 1;
            return Task.FromResult(script[index](request));
        }
    }

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    private static HttpResponseMessage ConnectTimeout(HttpRequestMessage _) =>
        throw new TaskCanceledException("bağlantı zaman aşımı", new TimeoutException("ConnectTimeout"));

    private static HttpClient Client(ScriptedServer server, Action<IHttpClientBuilder> configure, Action<IServiceCollection>? services = null)
    {
        var collection = new ServiceCollection();
        services?.Invoke(collection);
        var builder = collection.AddHttpClient("test", c => c.BaseAddress = new Uri("https://api.local"));
        configure(builder);
        builder.ConfigurePrimaryHttpMessageHandler(() => server);
        return collection.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient("test");
    }

    private static void FastRetry(AegisHttpStandardResilienceOptions o)
    {
        o.Retry.Delay = TimeSpan.Zero;
        o.Retry.UseJitter = false;
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(501, true)]
    [InlineData(507, true)]
    [InlineData(520, true)] // Cloudflare: kaynak sunucu hatası
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(404, false)]
    [InlineData(400, false)]
    [InlineData(200, false)]
    public void TransientStatus_MatchesMicrosoftDefinition(int status, bool transient)
    {
        Assert.Equal(transient, AegisHttpTransientErrors.IsTransient((HttpStatusCode)status));
        Assert.Equal(transient, AegisResilienceHandler.IsTransientHttpFailure((HttpStatusCode)status));
    }

    [Fact]
    public void TransientExceptions_MatchMicrosoftDefinition()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var connectTimeout = new TaskCanceledException("x", new TimeoutException());

        Assert.True(AegisHttpTransientErrors.IsTransient(new HttpRequestException("ağ")));
        Assert.True(AegisHttpTransientErrors.IsTransient(new AegisTimeoutException("t", TimeSpan.FromSeconds(1))));
        Assert.True(AegisHttpTransientErrors.IsTransient(connectTimeout));
        Assert.False(AegisHttpTransientErrors.IsTransient(connectTimeout, cancelled.Token)); // çağıran iptali geçici değildir
        Assert.False(AegisHttpTransientErrors.IsTransient(new OperationCanceledException()));
        Assert.False(AegisHttpTransientErrors.IsTransient(new BrokenCircuitException("açık")));
        Assert.True(AegisHttpTransientErrors.IsTransientForHedging(new BrokenCircuitException("açık")));
    }

    [Fact]
    public void HandleTransientHttpErrors_PredicateCoversResponsesAndExceptions()
    {
        var predicate = new AegisPredicateBuilder().HandleTransientHttpErrors();

        Assert.True(predicate.ShouldHandle(Outcome.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway))));
        Assert.False(predicate.ShouldHandle(Outcome.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        Assert.True(predicate.ShouldHandle(Outcome.FromException<HttpResponseMessage>(new HttpRequestException())));
        Assert.False(predicate.ShouldHandle(Outcome.FromException<HttpResponseMessage>(new InvalidOperationException())));
    }

    [Fact]
    public async Task Status520_IsRetried()
    {
        var server = new ScriptedServer(_ => Status((HttpStatusCode)520), _ => Status(HttpStatusCode.OK));
        using var client = Client(server, b => b.AddStandardAegisHandler(FastRetry));

        using var response = await client.GetAsync("/x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, server.Calls);
    }

    [Fact]
    public async Task ConnectTimeout_IsRetried_LikeMicrosoft()
    {
        var server = new ScriptedServer(ConnectTimeout, _ => Status(HttpStatusCode.OK));
        using var client = Client(server, b => b.AddStandardAegisHandler(FastRetry));

        using var response = await client.GetAsync("/x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, server.Calls);
    }

    [Fact]
    public async Task ConnectTimeout_RetriesExhausted_SurfacesOriginalCancellation()
    {
        var server = new ScriptedServer(ConnectTimeout);
        using var client = Client(server, b => b.AddStandardAegisHandler(FastRetry));

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync("/x"));

        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.Equal(4, server.Calls); // 1 + 3 yeniden deneme
    }

    [Fact]
    public async Task CallerCancellation_IsNeverRetried()
    {
        using var cts = new CancellationTokenSource();
        var server = new ScriptedServer(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException("iptal", new TimeoutException(), cts.Token);
        });
        using var client = Client(server, b => b.AddStandardAegisHandler(FastRetry));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("/x", cts.Token));
        Assert.Equal(1, server.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShouldRetryAfterHeader_ControlsServerRequestedDelay(bool honour)
    {
        var server = new ScriptedServer(
            _ => new HttpResponseMessage((HttpStatusCode)429) { Headers = { RetryAfter = new(TimeSpan.FromSeconds(1)) } },
            _ => Status(HttpStatusCode.OK));
        using var client = Client(server, b => b.AddStandardAegisHandler(o =>
        {
            FastRetry(o);
            o.Retry.ShouldRetryAfterHeader = honour;
        }));

        var watch = Stopwatch.StartNew();
        using var response = await client.GetAsync("/x");
        watch.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (honour)
        {
            Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(900), watch.Elapsed.ToString());
        }
        else
        {
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(900), watch.Elapsed.ToString());
        }
    }

    [Fact]
    public async Task AbsurdRetryAfter_IsCappedByMaxDelay()
    {
        var server = new ScriptedServer(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Headers = { RetryAfter = new(TimeSpan.FromDays(365)) } },
            _ => Status(HttpStatusCode.OK));
        using var client = Client(server, b => b.AddStandardAegisHandler(o =>
        {
            FastRetry(o);
            o.Retry.MaxDelay = TimeSpan.FromMilliseconds(50);
        }));

        using var response = await client.GetAsync("/x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class RetryCounter
    {
        public int Count;
    }

    [Fact]
    public async Task StandardHandler_WithServiceProvider_ResolvesServices_AndSeesRequestMessage()
    {
        var server = new ScriptedServer(_ => Status(HttpStatusCode.BadGateway), _ => Status(HttpStatusCode.OK));
        var seenUris = new ConcurrentQueue<Uri?>();
        RetryCounter? counter = null;
        using var client = Client(server,
            b => b.AddStandardAegisHandler((o, sp) =>
            {
                FastRetry(o);
                counter = sp.GetRequiredService<RetryCounter>();
                o.Retry.OnRetry = a =>
                {
                    Interlocked.Increment(ref counter.Count);
                    seenUris.Enqueue(a.Context.GetRequestMessage()?.RequestUri);
                    return default;
                };
            }),
            s => s.AddSingleton<RetryCounter>());

        using var response = await client.GetAsync("/siparis/42");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, counter!.Count);
        Assert.Equal("/siparis/42", Assert.Single(seenUris)!.AbsolutePath);
    }

    [Fact]
    public async Task HedgingHandler_WithServiceProvider_IsBuiltOncePerProvider()
    {
        var builds = 0;
        var server = new ScriptedServer(_ => Status(HttpStatusCode.OK));
        var collection = new ServiceCollection();
        collection.AddHttpClient("test", c => c.BaseAddress = new Uri("https://api.local"))
            .AddStandardAegisHedgingHandler((o, _) => Interlocked.Increment(ref builds))
            .ConfigurePrimaryHttpMessageHandler(() => server)
            .SetHandlerLifetime(TimeSpan.FromSeconds(1));
        using var provider = collection.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        for (var i = 0; i < 2; i++)
        {
            using var client = factory.CreateClient("test");
            using var response = await client.GetAsync("/x");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await Task.Delay(TimeSpan.FromSeconds(1.2)); // işleyici yenilensin (en kısa ömür 1 sn)
        }

        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task KeyedContextualHandler_IsolatesCircuitPerAuthority_AndExposesInstanceName()
    {
        var instances = new ConcurrentQueue<string?>();
        var server = new ScriptedServer(r => r.RequestUri!.Host == "down.local" ? Status(HttpStatusCode.ServiceUnavailable) : Status(HttpStatusCode.OK));
        using var client = Client(server, b => b.AddAegisResilienceHandler(
            (pipeline, context) =>
            {
                instances.Enqueue(context.InstanceName);
                pipeline.AddCircuitBreaker(o => { o.MinimumThroughput = 2; o.FailureRatio = 0.5; o.BreakDuration = TimeSpan.FromMinutes(1); });
            },
            selectPipelineBy: AegisHttpPipelineSelectors.ByAuthority));

        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://down.local/x"));
        }

        await Assert.ThrowsAsync<BrokenCircuitException>(() => client.GetAsync("https://down.local/x"));
        using var healthy = await client.GetAsync("https://up.local/x");

        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        Assert.Equal(["https://down.local", "https://up.local"], instances);
    }

    [Fact]
    public async Task KeyedContextualHandler_EnforcesCardinalityLimit()
    {
        var server = new ScriptedServer(_ => Status(HttpStatusCode.OK));
        using var client = Client(server, b => b.AddAegisResilienceHandler(
            (pipeline, _) => pipeline.AddTimeout(TimeSpan.FromSeconds(5)),
            selectPipelineBy: AegisHttpPipelineSelectors.ByAuthority,
            maxPipelines: 2));

        (await client.GetAsync("https://a.local/")).Dispose();
        (await client.GetAsync("https://b.local/")).Dispose();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://c.local/"));
        (await client.GetAsync("https://a.local/")).Dispose(); // mevcut anahtarlar çalışmaya devam eder
    }

    [Fact]
    public async Task KeyedContextualHandler_FailedBuild_IsRetriedOnNextRequest()
    {
        var attempts = 0;
        var server = new ScriptedServer(_ => Status(HttpStatusCode.OK));
        using var client = Client(server, b => b.AddAegisResilienceHandler(
            (pipeline, _) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new InvalidOperationException("geçici kurulum hatası");
                }

                pipeline.AddTimeout(TimeSpan.FromSeconds(5));
            },
            selectPipelineBy: AegisHttpPipelineSelectors.ByAuthority));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://a.local/"));
        using var response = await client.GetAsync("https://a.local/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PerAuthorityStandardHandler_KeepsConsecutiveFailureThreshold()
    {
        var server = new ScriptedServer(_ => Status(HttpStatusCode.ServiceUnavailable));
        using var client = Client(server, b => b.AddStandardAegisHandler(o =>
        {
            FastRetry(o);
            o.Retry.MaxRetryAttempts = 1;
            o.CircuitBreaker.ConsecutiveFailureThreshold = 2; // MinimumThroughput 100 beklenmeden açılmalı
            o.SelectPipelineByAuthority();
        }));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://svc.local/x"));
        await Assert.ThrowsAsync<BrokenCircuitException>(() => client.GetAsync("https://svc.local/x"));
    }
}
