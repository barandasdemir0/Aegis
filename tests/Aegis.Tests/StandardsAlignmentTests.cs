using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Extensions.Http;

namespace Aegis.Tests;

/// <summary>
/// 2.0.0 standart hizalaması: bağımsız doğrulamada bulunan üç sapmanın regresyon testleri.
/// </summary>
public class StandardsAlignmentTests
{
    [Fact]
    public async Task Throwing_OnRetry_DoesNotBreakRetry()
    {
        var calls = 0;
        var pipeline = new AegisPipelineBuilder("r")
            .AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.Zero; o.OnRetry = _ => throw new ApplicationException("kanca"); })
            .Build();

        var result = await pipeline.ExecuteAsync(
            _ => ++calls < 3 ? throw new InvalidOperationException() : ValueTask.FromResult(7), CancellationToken.None);

        Assert.Equal(7, result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Throwing_OnTimeout_StillReportsTimeout()
    {
        var pipeline = new AegisPipelineBuilder("t")
            .AddTimeout(TimeSpan.FromMilliseconds(50), o => o.OnTimeout = (_, _) => throw new ApplicationException("kanca"))
            .Build();

        await Assert.ThrowsAsync<AegisTimeoutException>(async () =>
            await pipeline.ExecuteAsync(async ct => { await Task.Delay(5000, ct); return 1; }, CancellationToken.None));
    }

    [Fact]
    public async Task MaxHedgedAttempts_Counts_Additional_Attempts_Like_Polly()
    {
        var attempts = 0;
        var pipeline = new AegisPipelineBuilder("h")
            .AddHedging(o => { o.MaxHedgedAttempts = 1; o.HedgingDelay = TimeSpan.FromMilliseconds(20); })
            .Build();

        var result = await pipeline.ExecuteAsync(async ct =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                await Task.Delay(5000, ct);
                return "yavas";
            }

            return "hizli";
        }, CancellationToken.None);

        Assert.Equal("hizli", result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void Hedging_Default_Is_One_Additional_Attempt()
    {
        Assert.Equal(1, new Aegis.Resilience.Core.Strategies.Hedging.HedgingOptions().MaxHedgedAttempts);
    }

    [Fact]
    public async Task ConcurrencyLimiter_Rejects_Immediately_By_Default()
    {
        var release = new TaskCompletionSource();
        var pipeline = new AegisPipelineBuilder("c").AddConcurrencyLimiter(1).Build();
        var holder = pipeline.ExecuteAsync(async _ => { await release.Task; return 1; }, CancellationToken.None).AsTask();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<RateLimitRejectedException>(async () =>
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult(2), CancellationToken.None));
        Assert.True(watch.ElapsedMilliseconds < 500, $"red beklemeden gelmeli ({watch.ElapsedMilliseconds} ms)");

        release.SetResult();
        Assert.Equal(1, await holder);
    }

    [Fact]
    public void Standard_Handlers_Leave_Timeout_To_The_Handler_Like_Microsoft()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.HttpClientFactoryServiceCollectionExtensions.AddHttpClient(services, "s")
            .AddStandardAegisHandler(_ => { });
        Microsoft.Extensions.DependencyInjection.HttpClientFactoryServiceCollectionExtensions.AddHttpClient(services, "h")
            .AddStandardAegisHedgingHandler(_ => { });
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var factory = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IHttpClientFactory>(provider);

        // TotalRequestTimeout HttpClient'ın 100 sn'sinden uzun olabilsin; zaman sınırını yalnızca işleyici koyar.
        Assert.Equal(Timeout.InfiniteTimeSpan, factory.CreateClient("s").Timeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, factory.CreateClient("h").Timeout);
    }

    [Fact]
    public async Task Handler_Context_Is_Pooled_But_User_Context_Is_Kept()
    {
        var pipeline = new AegisPipelineBuilder("p").Build();
        string? seenDuringRequest = null;
        using var invoker = new HttpMessageInvoker(new Aegis.Resilience.Extensions.Http.AegisResilienceHandler(pipeline)
        {
            InnerHandler = new InspectingHandler(r => seenDuringRequest = r.GetAegisContext()?.PipelineName)
        });

        using var plain = new HttpRequestMessage(HttpMethod.Get, "http://x/");
        using (await invoker.SendAsync(plain, CancellationToken.None))
        {
        }

        Assert.Equal("p", seenDuringRequest);
        Assert.Null(plain.GetAegisContext()); // işleyicinin kiraladığı bağlam istekten ayrılıp havuza döndü

        var userContext = new Aegis.Resilience.Core.Context.AegisContext(CancellationToken.None, "kullanici") { OperationKey = "op" };
        using var withUserContext = new HttpRequestMessage(HttpMethod.Get, "http://x/");
        withUserContext.SetAegisContext(userContext);
        using (await invoker.SendAsync(withUserContext, CancellationToken.None))
        {
        }

        Assert.Same(userContext, withUserContext.GetAegisContext());
        Assert.Equal("op", userContext.OperationKey);
        Assert.Same(withUserContext, userContext.GetRequestMessage());
    }

    private sealed class InspectingHandler(Action<HttpRequestMessage> inspect) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            inspect(request);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task ConcurrencyLimiter_Queue_Is_Bounded_By_QueueLimit()
    {
        var release = new TaskCompletionSource();
        var pipeline = new AegisPipelineBuilder("c")
            .AddConcurrencyLimiter(1, o => { o.QueueLimit = 2; o.QueueTimeout = TimeSpan.FromSeconds(10); })
            .Build();

        var running = pipeline.ExecuteAsync(async _ => { await release.Task; return 0; }, CancellationToken.None).AsTask();
        var queued = Enumerable.Range(1, 2)
            .Select(i => pipeline.ExecuteAsync(_ => ValueTask.FromResult(i), CancellationToken.None).AsTask())
            .ToArray();

        // Kuyruk dolu (2/2): dördüncü çağrı beklemeden reddedilir.
        await Assert.ThrowsAsync<RateLimitRejectedException>(async () =>
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult(9), CancellationToken.None));

        release.SetResult();
        Assert.Equal(0, await running);
        Assert.Equal([1, 2], (await Task.WhenAll(queued)).Order());
    }
}
