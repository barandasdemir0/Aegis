using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Extensions.Http;

namespace Aegis.Tests;

/// <summary>
/// Microsoft.Extensions.Http.Resilience karşısında kapatılan üç boşluk: yöntem bazında yeniden denemeyi kapatma
/// (<c>DisableFor</c>), DI bağlamlı özel işleyici (<c>ResilienceHandlerContext</c>) ve <c>RemoveAllResilienceHandlers</c>.
/// </summary>
public class HttpResilienceGapClosureTests
{
    private sealed class FakeServer : HttpMessageHandler
    {
        public ConcurrentQueue<HttpMethod> Requests { get; } = new();

        public HttpStatusCode Status { get; set; } = HttpStatusCode.ServiceUnavailable;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.Method);
            return Task.FromResult(new HttpResponseMessage(Status));
        }
    }

    private static ServiceProvider BuildProvider(FakeServer server, Action<IHttpClientBuilder> configure, Action<IServiceCollection>? services = null)
    {
        var collection = new ServiceCollection();
        services?.Invoke(collection);
        var builder = collection.AddHttpClient("test", c => c.BaseAddress = new Uri("https://api.local"));
        configure(builder);
        builder.ConfigurePrimaryHttpMessageHandler(() => server);
        return collection.BuildServiceProvider();
    }

    private static HttpClient Client(IServiceProvider provider, string name = "test") =>
        provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

    /// <summary>Gecikmesiz retry; denemeler tükenince son yanıt döner ki testler deneme sayısını yanıt üzerinden ölçebilsin.</summary>
    private static void FastRetry(AegisHttpStandardResilienceOptions o)
    {
        o.Retry.Delay = TimeSpan.Zero;
        o.Retry.UseJitter = false;
        o.ReturnFinalResponse = true;
    }

    // ---------------------------------------------------------------- 0) ReturnFinalResponse (Microsoft davranışı)

    /// <summary>Dispose edildiğini kaydeden içerik: bırakılmayan (sızan) yanıtları yakalamak için.</summary>
    private sealed class TrackedContent(ConcurrentBag<TrackedContent> all) : StringContent("govde")
    {
        public bool IsDisposed { get; private set; }

        public ConcurrentBag<TrackedContent> All { get; } = all;

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class TrackingServer(HttpStatusCode status) : HttpMessageHandler
    {
        public ConcurrentBag<TrackedContent> Contents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new TrackedContent(Contents);
            Contents.Add(content);
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }

    [Fact]
    public async Task ReturnFinalResponse_ReturnsLastTransientResponse_AndReleasesEarlierOnes()
    {
        var server = new TrackingServer(HttpStatusCode.ServiceUnavailable);
        var collection = new ServiceCollection();
        collection.AddHttpClient("test").AddStandardAegisHandler(FastRetry).ConfigurePrimaryHttpMessageHandler(() => server);
        await using var provider = collection.BuildServiceProvider();

        using var response = await Client(provider).GetAsync("https://api.local/a");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("govde", await response.Content.ReadAsStringAsync()); // dönen yanıt canlı
        Assert.Equal(4, server.Contents.Count);
        Assert.Equal(3, server.Contents.Count(c => c.IsDisposed));         // önceki 3 deneme bırakıldı
    }

    [Fact]
    public async Task ReturnFinalResponse_Off_ByDefault_ThrowsWithStatusCode_AndReleasesAllResponses()
    {
        var server = new TrackingServer(HttpStatusCode.ServiceUnavailable);
        var collection = new ServiceCollection();
        collection.AddHttpClient("test")
            .AddStandardAegisHandler(o => { o.Retry.Delay = TimeSpan.Zero; o.Retry.UseJitter = false; })
            .ConfigurePrimaryHttpMessageHandler(() => server);
        await using var provider = collection.BuildServiceProvider();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Client(provider).GetAsync("https://api.local/a"));

        Assert.Equal(typeof(HttpRequestException), ex.GetType()); // istisna tipi (ve telemetri etiketi) değişmedi
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.All(server.Contents, c => Assert.True(c.IsDisposed));
    }

    [Fact]
    public async Task ReturnFinalResponse_OpenCircuitStillThrows_LikeMicrosoft()
    {
        var server = new FakeServer();
        await using var provider = BuildProvider(server, b => b.AddAegisResilienceHandler((pipeline, context) =>
        {
            pipeline.AddCircuitBreaker(o => { o.MinimumThroughput = 2; o.FailureRatio = 0.5; o.BreakDuration = TimeSpan.FromMinutes(1); });
            context.ReturnFinalResponse = true;
        }));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Client(provider).GetAsync("/a")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Client(provider).GetAsync("/a")).StatusCode);
        await Assert.ThrowsAsync<BrokenCircuitException>(() => Client(provider).GetAsync("/a"));
    }

    // ---------------------------------------------------------------- 1) DisableRetryFor

    [Fact]
    public async Task DisableRetryFor_SelectedMethodIsSentOnce_OthersStillRetry()
    {
        var server = new FakeServer();
        await using var provider = BuildProvider(server, b => b.AddStandardAegisHandler(o =>
        {
            FastRetry(o);
            o.DisableRetryFor(HttpMethod.Delete);
        }));

        var delete = await Client(provider).DeleteAsync("/siparis/1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, delete.StatusCode); // gerçek yanıt olduğu gibi döner
        Assert.Single(server.Requests);

        await Client(provider).GetAsync("/siparis/1");
        Assert.Equal(1 + 4, server.Requests.Count); // GET: 1 + 3 yeniden deneme
    }

    [Fact]
    public async Task DisableRetryForUnsafeHttpMethods_IsStricterThanDefault_EvenWithIdempotencyKey()
    {
        var server = new FakeServer();
        await using var provider = BuildProvider(server, b => b.AddStandardAegisHandler(o =>
        {
            FastRetry(o);
            o.AllowNonIdempotentRetry = true; // açık kapatma bu izni de geçersiz kılar
            o.DisableRetryForUnsafeHttpMethods();
        }));

        using var post = new HttpRequestMessage(HttpMethod.Post, "/odeme") { Content = new StringContent("{}") };
        post.Headers.Add("Idempotency-Key", "abc");
        await Client(provider).SendAsync(post);
        await Client(provider).PutAsync("/odeme/1", new StringContent("{}"));
        await Client(provider).SendAsync(new HttpRequestMessage(new HttpMethod("PATCH"), "/odeme/1"));

        Assert.Equal(3, server.Requests.Count); // her biri tek deneme
    }

    [Fact]
    public void DisableRetryFor_RejectsNullArguments()
    {
        var options = new AegisHttpStandardResilienceOptions();
        Assert.Throws<ArgumentNullException>(() => options.DisableRetryFor(null!));
        Assert.Throws<ArgumentException>(() => options.DisableRetryFor(HttpMethod.Get, null!));
    }

    [Fact]
    public async Task DefaultBehavior_IsUnchanged_IdempotentDeleteIsRetried()
    {
        var server = new FakeServer();
        await using var provider = BuildProvider(server, b => b.AddStandardAegisHandler(FastRetry));

        await Client(provider).DeleteAsync("/siparis/1");

        Assert.Equal(4, server.Requests.Count);
    }

    // ---------------------------------------------------------------- 2) DI bağlamlı özel işleyici

    private sealed class RetryBudget
    {
        public int Attempts { get; set; } = 1;
    }

    [Fact]
    public async Task ContextHandler_ResolvesServices_AndAppliesHttpRules()
    {
        var server = new FakeServer();
        string? clientName = null;
        await using var provider = BuildProvider(
            server,
            b => b.AddAegisResilienceHandler((pipeline, context) =>
            {
                clientName = context.ClientName;
                var budget = context.ServiceProvider.GetRequiredService<RetryBudget>();
                pipeline.AddRetry(o => { o.MaxRetryAttempts = budget.Attempts; o.Delay = TimeSpan.Zero; });
                context.ReturnFinalResponse = true;
                context.DisableRetryFor(HttpMethod.Put);
            }),
            s => s.AddSingleton(new RetryBudget { Attempts = 2 }));

        await Client(provider).GetAsync("/a");
        Assert.Equal(3, server.Requests.Count); // DI'dan gelen bütçe: 1 + 2
        Assert.Equal("test", clientName);

        await Client(provider).PutAsync("/a", new StringContent("{}"));
        Assert.Equal(4, server.Requests.Count); // PUT kapatıldı
    }

    [Fact]
    public async Task ContextHandler_PipelineIsSharedAcrossClients_CircuitStateIsKept()
    {
        var server = new FakeServer();
        await using var provider = BuildProvider(server, b => b.AddAegisResilienceHandler((pipeline, _) =>
            pipeline.AddCircuitBreaker(o => { o.MinimumThroughput = 2; o.FailureRatio = 0.5; o.BreakDuration = TimeSpan.FromMinutes(1); })));

        await Assert.ThrowsAsync<HttpRequestException>(() => Client(provider).GetAsync("/a"));
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(provider).GetAsync("/a"));

        // Yeni HttpClient örneği aynı boru hattını kullanır: devre açık, istek sunucuya gitmez.
        await Assert.ThrowsAsync<BrokenCircuitException>(() => Client(provider).GetAsync("/a"));
        Assert.Equal(2, server.Requests.Count);
    }

    private sealed class RetrySettings
    {
        public int MaxRetryAttempts { get; set; }
    }

    [Fact]
    public async Task ContextHandler_EnableReloads_RebuildsOnOptionsChange_AndDisposesOldGeneration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Retry:MaxRetryAttempts"] = "1" })
            .Build();
        var server = new FakeServer();
        var disposedGenerations = 0;
        await using var provider = BuildProvider(
            server,
            b => b.AddAegisResilienceHandler((pipeline, context) =>
            {
                var settings = context.GetOptions<RetrySettings>();
                pipeline.AddRetry(o => { o.MaxRetryAttempts = settings.MaxRetryAttempts; o.Delay = TimeSpan.Zero; });
                context.ReturnFinalResponse = true;
                context.EnableReloads<RetrySettings>();
                context.OnPipelineDisposed(() => Interlocked.Increment(ref disposedGenerations));
            }),
            s => s.Configure<RetrySettings>(configuration.GetSection("Retry")));

        await Client(provider).GetAsync("/a");
        Assert.Equal(2, server.Requests.Count);

        configuration["Retry:MaxRetryAttempts"] = "3";
        configuration.Reload();

        await Client(provider).GetAsync("/a");
        Assert.Equal(2 + 4, server.Requests.Count);
        Assert.Equal(1, disposedGenerations); // eski nesil boşalınca bırakıldı

        await provider.DisposeAsync();
        Assert.Equal(2, disposedGenerations); // güncel nesil sağlayıcıyla birlikte bırakıldı
    }

    [Fact]
    public async Task ContextHandler_InvalidReload_KeepsServingWithPreviousGeneration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Retry:MaxRetryAttempts"] = "1" })
            .Build();
        var server = new FakeServer();
        await using var provider = BuildProvider(
            server,
            b => b.AddAegisResilienceHandler((pipeline, context) =>
            {
                var settings = context.GetOptions<RetrySettings>();
                pipeline.AddRetry(o => { o.MaxRetryAttempts = settings.MaxRetryAttempts; o.Delay = TimeSpan.Zero; });
                context.ReturnFinalResponse = true;
                context.EnableReloads<RetrySettings>();
            }),
            s => s.Configure<RetrySettings>(configuration.GetSection("Retry")));

        await Client(provider).GetAsync("/a");
        configuration["Retry:MaxRetryAttempts"] = "-5"; // geçersiz: kurulum fail-fast hata verir
        configuration.Reload();
        await Client(provider).GetAsync("/a");

        Assert.Equal(4, server.Requests.Count); // iki çağrı da eski (1 yeniden deneme) nesille
    }

    [Fact]
    public async Task ContextHandler_ReportsTelemetryToDiLogger()
    {
        var logs = new ConcurrentQueue<string>();
        var server = new FakeServer();
        await using var provider = BuildProvider(
            server,
            b => b.AddAegisResilienceHandler((pipeline, context) =>
            {
                pipeline.AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.Zero; });
                context.ReturnFinalResponse = true;
            }),
            s => s.AddLogging(l => l.AddProvider(new ListLoggerProvider(logs))));

        await Client(provider).GetAsync("/a");

        Assert.Contains(logs, m => m.Contains("OnRetry") && m.Contains("test_AegisPipeline"));
    }

    [Fact]
    public async Task ContextHandler_FailingDisposeCallback_DoesNotBreakShutdown()
    {
        var server = new FakeServer { Status = HttpStatusCode.OK };
        var secondCalled = false;
        var provider = BuildProvider(server, b => b.AddAegisResilienceHandler((_, context) =>
        {
            context.OnPipelineDisposed(() => throw new InvalidOperationException("bozuk"));
            context.OnPipelineDisposed(() => secondCalled = true);
        }));

        await Client(provider).GetAsync("/a");
        await provider.DisposeAsync();

        Assert.True(secondCalled);
    }

    private sealed class ListLoggerProvider(ConcurrentQueue<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(messages);

        public void Dispose()
        {
        }

        private sealed class ListLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }

    // ---------------------------------------------------------------- 3) RemoveAllAegisHandlers

    private sealed class CountingHandler : DelegatingHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task RemoveAllAegisHandlers_RemovesDefaultsFromOneClient_KeepsOtherHandlersAndClients()
    {
        var server = new FakeServer();
        var counting = new CountingHandler();
        var services = new ServiceCollection();
        services.ConfigureHttpClientDefaults(b => b
            .AddStandardAegisHandler(FastRetry)
            .ConfigurePrimaryHttpMessageHandler(() => server));
        services.AddHttpClient("ham").AddHttpMessageHandler(() => counting).RemoveAllAegisHandlers();
        services.AddHttpClient("korumali");
        await using var provider = services.BuildServiceProvider();

        await Client(provider, "ham").GetAsync("https://api.local/a");
        Assert.Single(server.Requests);   // Aegis kaldırıldı: yeniden deneme yok
        Assert.Equal(1, counting.Calls);   // Aegis dışı işleyici duruyor

        await Client(provider, "korumali").GetAsync("https://api.local/a");
        Assert.Equal(1 + 4, server.Requests.Count); // diğer istemci hâlâ korumalı
    }

    [Fact]
    public async Task RemoveAllAegisHandlers_KeepsAegisHandlersAddedAfterwards()
    {
        var server = new FakeServer();
        var services = new ServiceCollection();
        services.ConfigureHttpClientDefaults(b => b
            .AddStandardAegisHandler(o => { FastRetry(o); o.Retry.MaxRetryAttempts = 5; })
            .ConfigurePrimaryHttpMessageHandler(() => server));
        services.AddHttpClient("ozel")
            .RemoveAllAegisHandlers()
            .AddStandardAegisHandler(o => { FastRetry(o); o.Retry.MaxRetryAttempts = 1; });
        await using var provider = services.BuildServiceProvider();

        await Client(provider, "ozel").GetAsync("https://api.local/a");

        Assert.Equal(2, server.Requests.Count); // yalnızca sonradan eklenen (1 yeniden deneme) işleyici çalıştı
    }
}
