using System.Collections.Concurrent;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Core.Telemetry;
using Aegis.Resilience.Distributed.Abstractions;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.Http;
using Aegis.Resilience.RateLimiting;
using Aegis.Resilience.Testing;

namespace Aegis.CompatibilityTests;

/// <summary>
/// .NET Framework 4.8 üzerinde uçtan uca davranış: stratejiler, çalıştırma biçimleri (eski hedefte genişletme metodu yolu),
/// sahte saat (Microsoft.Bcl.TimeProvider), telemetri, DI, HTTP (HttpRequestMessage.Properties yolu), dağıtık devre.
/// </summary>
public class FrameworkCompatibilityTests
{
    private static ValueTask<T> Done<T>(T value) => new(value);

    [Fact]
    public void RunsOnDotNetFramework()
    {
        Assert.Contains(".NET Framework", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
    }

    // ---------------------------------------------------------------- Stratejiler

    [Fact]
    public async Task Retry_WithPredicateBuilder_RetriesExceptionsAndResults()
    {
        using var pipeline = new AegisPipelineBuilder("fx-retry")
            .AddRetry(o =>
            {
                o.MaxRetryAttempts = 3;
                o.Delay = TimeSpan.Zero;
                o.ShouldHandleOutcome = new AegisPredicateBuilder().Handle<TimeoutException>().HandleResult<int>(r => r < 0);
            })
            .Build();
        var calls = 0;

        var result = await pipeline.ExecuteAsync(_ => ++calls switch
        {
            1 => throw new TimeoutException(),
            2 => Done(-1),
            _ => Done(calls)
        });

        Assert.Equal(3, result);
    }

    [Fact]
    public async Task Timeout_ThrowsAegisTimeoutException()
    {
        using var pipeline = new AegisPipelineBuilder("fx-timeout").AddTimeout(TimeSpan.FromMilliseconds(50)).Build();

        await Assert.ThrowsAsync<AegisTimeoutException>(async () => await pipeline.ExecuteAsync(async ctx =>
        {
            await Task.Delay(5_000, ctx.CancellationToken);
            return 1;
        }));
    }

    [Fact]
    public async Task FakeTimeProvider_DrivesRetryDelayAndBreakDuration()
    {
        var clock = new FakeTimeProvider();
        using var pipeline = new AegisPipelineBuilder("fx-clock")
            .AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.FromMinutes(10); o.BackoffType = DelayBackoffType.Constant; o.UseJitter = false; })
            .WithTimeProvider(clock)
            .Build();
        var calls = 0;

        var task = pipeline.ExecuteAsync(_ => ++calls == 1 ? throw new InvalidOperationException() : Done(calls)).AsTask();
        await Task.Delay(50);
        Assert.False(task.IsCompleted);

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(2, await WithTimeout(task));
    }

    [Fact]
    public async Task CircuitBreaker_Opens_HalfOpens_AndCloses_WithFakeClock()
    {
        var clock = new FakeTimeProvider();
        var state = new CircuitBreakerStateProvider();
        using var pipeline = new AegisPipelineBuilder("fx-cb")
            .AddCircuitBreaker(o => { o.MinimumThroughput = 2; o.FailureRatio = 0.5; o.BreakDuration = TimeSpan.FromMinutes(1); o.StateProvider = state; })
            .WithTimeProvider(clock)
            .Build();

        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()));
        }

        Assert.Equal(CircuitState.Open, state.CircuitState);
        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await pipeline.ExecuteAsync(_ => Done(1)));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(CircuitState.HalfOpen, state.CircuitState);
        Assert.Equal(1, await pipeline.ExecuteAsync(_ => Done(1)));
        Assert.Equal(CircuitState.Closed, state.CircuitState);
    }

    [Fact]
    public async Task Tracing_ProducesSpanWithRetryEvent_AndRestoresCallersActivity()
    {
        // Aegis iz kaynağı (netstandard2.0 / net462 dahil): her hedefte span, etiket, span olayı ve çağıranın Activity.Current'ı.
        const string name = "fx-trace";
        var stopped = new ConcurrentQueue<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "Aegis",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> options) =>
                options.Name == "Aegis " + name ? System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded : System.Diagnostics.ActivitySamplingResult.None,
            ActivityStopped = stopped.Enqueue
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        using var outerSource = new System.Diagnostics.ActivitySource("Aegis.Compat.Outer");
        using var outerListener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == "Aegis.Compat.Outer",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded
        };
        System.Diagnostics.ActivitySource.AddActivityListener(outerListener);
        using var outer = outerSource.StartActivity("istek");
        using var pipeline = new AegisPipelineBuilder(name)
            .AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.Zero; o.UseJitter = false; })
            .Build();
        var calls = 0;

        var running = pipeline.ExecuteAsync(async _ =>
        {
            await Task.Yield();
            return ++calls < 2 ? throw new InvalidOperationException() : 7;
        });
        Assert.Same(outer, System.Diagnostics.Activity.Current);   // çağıranın bağlamına span sızmaz

        Assert.Equal(7, await running);

        var span = Assert.Single(stopped);
        Assert.Equal(name, span.GetTagItem("pipeline.name"));
        Assert.Equal(outer!.Id, span.ParentId);
        Assert.Contains(span.Events, e => e.Name == "OnRetry");
    }

    [Fact]
    public async Task ManualControl_IsolatesAndCloses()
    {
        var control = new CircuitBreakerManualControl();
        using var pipeline = new AegisPipelineBuilder("fx-manual").AddCircuitBreaker(o => o.ManualControl = control).Build();

        await control.IsolateAsync();
        await Assert.ThrowsAsync<IsolatedCircuitException>(async () => await pipeline.ExecuteAsync(_ => Done(1)));
        await control.CloseAsync();
        Assert.Equal(1, await pipeline.ExecuteAsync(_ => Done(1)));
    }

    [Fact]
    public async Task Hedging_SlowPrimary_SecondaryWins_AndResultBasedHedging()
    {
        using var hedging = new AegisPipelineBuilder("fx-hedge")
            .AddHedging(o => { o.MaxHedgedAttempts = 1; o.HedgingDelay = TimeSpan.FromMilliseconds(20); })
            .Build();
        var attempts = 0;

        var result = await hedging.ExecuteAsync(async ctx =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                await Task.Delay(5_000, ctx.CancellationToken);
                return "birincil";
            }

            return "yedek";
        });
        Assert.Equal("yedek", result);

        using var resultBased = new AegisPipelineBuilder("fx-hedge-result")
            .AddHedging(o => { o.MaxHedgedAttempts = 1; o.HedgingDelay = System.Threading.Timeout.InfiniteTimeSpan; o.ShouldHandleResult = r => r is 503; })
            .Build();
        var calls = 0;
        Assert.Equal(200, await resultBased.ExecuteAsync(_ => Done(++calls == 1 ? 503 : 200)));
    }

    [Fact]
    public async Task Fallback_Chaos_AndTypedPipeline()
    {
        using var pipeline = new AegisPipelineBuilder("fx-fallback")
            .AddFallback(o => o.FallbackAction = _ => new ValueTask<object?>("yedek"))
            .AddChaosFault(1.0, () => new TimeoutException())
            .Build<string>();

        Assert.Equal("yedek", await pipeline.ExecuteAsync(_ => Done("asil")));
        Assert.Equal(["Fallback", "Chaos"], pipeline.GetPipelineDescriptor().Strategies.Select(s => s.Name));
    }

    [Fact]
    public async Task RateLimiters_AegisTokenBucket_And_SystemThreadingBridge()
    {
        using var aegis = new AegisPipelineBuilder("fx-rl").AddRateLimiter(1, TimeSpan.FromMinutes(1)).Build();
        await aegis.ExecuteAsync(_ => Done(1));
        var ex = await Assert.ThrowsAsync<RateLimitRejectedException>(async () => await aegis.ExecuteAsync(_ => Done(1)));
        Assert.NotNull(ex.RetryAfter);

        using var bridge = new AegisPipelineBuilder("fx-bridge")
            .AddFixedWindowRateLimiter(new FixedWindowRateLimiterOptions { PermitLimit = 1, Window = TimeSpan.FromMinutes(1) })
            .Build();
        await bridge.ExecuteAsync(_ => Done(1));
        await Assert.ThrowsAsync<RateLimitRejectedException>(async () => await bridge.ExecuteAsync(_ => Done(1)));
    }

    // ---------------------------------------------------------------- Çalıştırma biçimleri (eski hedefte genişletme metodu yolu)

    [Fact]
    public async Task ExecutionForms_ThroughInterface_UseExtensionPath()
    {
        IAegisPipeline pipeline = new AegisPipelineBuilder("fx-forms").AddRetry(o => o.Delay = TimeSpan.Zero).Build();
        using var cts = new CancellationTokenSource();

        Assert.Equal(5, await pipeline.ExecuteAsync(static (_, s) => new ValueTask<int>(s), 5));
        Assert.Equal(6, await pipeline.ExecuteAsync(_ => Done(6), cts.Token));
        Assert.Equal(7, pipeline.Execute(() => 7));
        Assert.Equal(8, pipeline.Execute(static (_, s) => s, 8));
        var failed = await pipeline.ExecuteOutcomeAsync<int>(_ => throw new FormatException());
        Assert.IsType<FormatException>(failed.Exception);
        pipeline.Dispose();
    }

    [Fact]
    public async Task CustomPipeline_GetsAllForms_ThroughFallbacks()
    {
        IAegisPipeline custom = new MinimalPipeline();

        Assert.Equal(3, await custom.ExecuteAsync(static (_, s) => new ValueTask<int>(s), 3));
        Assert.Equal(4, custom.Execute(() => 4));
        Assert.True((await custom.ExecuteOutcomeAsync(_ => Done(1))).IsSuccess);
    }

    private sealed class MinimalPipeline : IAegisPipeline
    {
        public string Name => "minimal";

        public IReadOnlyList<IAegisStrategy> Strategies => [];

        public ValueTask<TResult> ExecuteAsync<TResult>(Func<AegisContext, ValueTask<TResult>> callback, AegisContext? context = null) =>
            callback(context ?? new AegisContext());

        public ValueTask ExecuteAsync(Func<AegisContext, ValueTask> callback, AegisContext? context = null) =>
            callback(context ?? new AegisContext());

        public void Dispose()
        {
        }
    }

    // ---------------------------------------------------------------- Telemetri + DI + ILogger

    private sealed class CaptureListener : AegisTelemetryListener
    {
        public ConcurrentQueue<string> Events { get; } = new();

        public override void Write(in AegisTelemetryEvent telemetryEvent) => Events.Enqueue(telemetryEvent.EventName);
    }

    private sealed class ListLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new ListLogger(this);

        public void Dispose()
        {
        }

        private sealed class ListLogger(ListLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Messages.Enqueue(formatter(state, exception));
        }
    }

    [Fact]
    public async Task Telemetry_ListenerAndDependencyInjectionLogging()
    {
        var listener = new CaptureListener();
        using (var pipeline = new AegisPipelineBuilder("fx-tel")
                   .AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.Zero; })
                   .WithTelemetry(t => t.Listeners.Add(listener))
                   .Build())
        {
            var calls = 0;
            await pipeline.ExecuteAsync(_ => ++calls == 1 ? throw new TimeoutException() : Done(1));
        }

        Assert.Contains(AegisEventNames.OnRetry, listener.Events);
        Assert.Contains(AegisEventNames.ExecutionAttempt, listener.Events);

        var logs = new ListLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs));
        services.AddAegisPipeline("fx-di", b => b.AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.Zero; }));
        services.AddAegisPipelines<string>((b, tenant, _) => b.AddRetry(o => o.MaxRetryAttempts = tenant == "vip" ? 5 : 1));
        using var sp = services.BuildServiceProvider();

        var diCalls = 0;
        await sp.GetRequiredService<IAegisPipelineRegistry>().GetPipeline("fx-di")
            .ExecuteAsync(_ => ++diCalls == 1 ? throw new TimeoutException() : Done(1));
        Assert.Contains(logs.Messages, m => m.Contains("OnRetry"));
        Assert.Equal(5, sp.GetRequiredService<IAegisPipelineProvider<string>>().GetPipeline("vip").GetPipelineDescriptor().GetOptions<RetryOptions>().MaxRetryAttempts);
    }

    // ---------------------------------------------------------------- HTTP

    private sealed class FakeServer : HttpMessageHandler
    {
        public ConcurrentQueue<string> Hosts { get; } = new();

        public Func<HttpRequestMessage, int, HttpStatusCode> Status { get; set; } = (_, _) => HttpStatusCode.OK;

        private int _count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hosts.Enqueue(request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(Status(request, Interlocked.Increment(ref _count))));
        }
    }

    [Fact]
    public async Task Http_StandardHandler_RetriesTransientStatus_AndCarriesContext()
    {
        var server = new FakeServer { Status = (_, n) => n < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK };
        var services = new ServiceCollection();
        services.AddHttpClient("fx-http")
            .AddStandardAegisHandler(o => { o.Retry.Delay = TimeSpan.Zero; o.Retry.UseJitter = false; o.SelectPipelineByAuthority(); })
            .ConfigurePrimaryHttpMessageHandler(() => server);
        using var sp = services.BuildServiceProvider();

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.local/x");
        var context = request.GetOrCreateAegisContext();
        context.OperationKey = "fx";
        using var response = await sp.GetRequiredService<IHttpClientFactory>().CreateClient("fx-http").SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, server.Hosts.Count);
        Assert.Same(context, request.GetAegisContext()); // HttpRequestMessage.Properties yolu
    }

    [Fact]
    public async Task Http_StandardHedging_OrderedGroups()
    {
        var server = new FakeServer { Status = (r, _) => r.RequestUri!.Host == "eu.local" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK };
        var services = new ServiceCollection();
        services.AddHttpClient("fx-hedge-http")
            .AddStandardAegisHedgingHandler(o =>
            {
                o.HedgingDelay = System.Threading.Timeout.InfiniteTimeSpan;
                o.OrderedGroups.Add(new UriEndpointGroup { Endpoints = { new WeightedUriEndpoint { Uri = new Uri("https://eu.local") } } });
                o.OrderedGroups.Add(new UriEndpointGroup { Endpoints = { new WeightedUriEndpoint { Uri = new Uri("https://us.local") } } });
            })
            .ConfigurePrimaryHttpMessageHandler(() => server);
        using var sp = services.BuildServiceProvider();

        using var response = await sp.GetRequiredService<IHttpClientFactory>().CreateClient("fx-hedge-http").GetAsync("https://orijinal.local/fiyat");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["eu.local", "us.local"], server.Hosts);
    }

    [Fact]
    public async Task Http_NonIdempotentPost_IsNotRetried()
    {
        var server = new FakeServer { Status = (_, _) => HttpStatusCode.ServiceUnavailable };
        var services = new ServiceCollection();
        services.AddHttpClient("fx-post")
            .AddStandardAegisHandler(o => o.Retry.Delay = TimeSpan.Zero)
            .ConfigurePrimaryHttpMessageHandler(() => server);
        using var sp = services.BuildServiceProvider();

        using var response = await sp.GetRequiredService<IHttpClientFactory>().CreateClient("fx-post")
            .PostAsync("https://api.local/odeme", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single(server.Hosts);
    }

    [Fact]
    public async Task Http_ContextHandler_DisableRetryFor_ReturnFinalResponse_AndRemoveAll()
    {
        var server = new FakeServer { Status = (_, _) => HttpStatusCode.ServiceUnavailable };
        var services = new ServiceCollection();
        services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => server));
        services.AddHttpClient("fx-context").AddAegisResilienceHandler((pipeline, context) =>
        {
            pipeline.AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.Zero; });
            context.DisableRetryFor(HttpMethod.Delete);
            context.ReturnFinalResponse = true;
        });
        services.AddHttpClient("fx-raw").AddStandardAegisHandler(o => o.Retry.Delay = TimeSpan.Zero).RemoveAllAegisHandlers();
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

        using (var get = await factory.CreateClient("fx-context").GetAsync("https://api.local/a"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, get.StatusCode); // keyed servis + son yanıt
        }

        Assert.Equal(3, server.Hosts.Count);
        await factory.CreateClient("fx-context").DeleteAsync("https://api.local/a");
        Assert.Equal(4, server.Hosts.Count); // DELETE tek deneme
        await factory.CreateClient("fx-raw").GetAsync("https://api.local/a");
        Assert.Equal(5, server.Hosts.Count); // Aegis kaldırıldı
    }

    // ---------------------------------------------------------------- Dağıtık devre (varsayılan arayüz gövdesi olmadan)

    [Fact]
    public async Task DistributedCircuitBreaker_WithInMemoryStore()
    {
        var store = new InMemoryCircuitBreakerStateStore();
        using var podA = new AegisPipelineBuilder("fx-dist")
            .AddStrategy(new DistributedCircuitBreakerStrategy(store, new DistributedCircuitBreakerOptions
            {
                CircuitKey = "ortak", MinimumThroughput = 2, FailureRatio = 0.5, BreakDuration = TimeSpan.FromMinutes(1),
                StateCacheDuration = TimeSpan.Zero
            }))
            .Build();
        using var podB = new AegisPipelineBuilder("fx-dist")
            .AddStrategy(new DistributedCircuitBreakerStrategy(store, new DistributedCircuitBreakerOptions
            {
                CircuitKey = "ortak", MinimumThroughput = 2, FailureRatio = 0.5, BreakDuration = TimeSpan.FromMinutes(1),
                StateCacheDuration = TimeSpan.Zero
            }))
            .Build();

        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await podA.ExecuteAsync<int>(_ => throw new InvalidOperationException()));
        }

        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await podB.ExecuteAsync(_ => Done(1))); // B hiç hata görmedi
    }

    private static async Task<T> WithTimeout<T>(Task<T> task)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))) != task)
        {
            throw new TimeoutException("Görev 5 sn içinde tamamlanmadı.");
        }

        return await task;
    }
}
