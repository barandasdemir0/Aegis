using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using AspNetServer = Microsoft.AspNetCore.Hosting.Server.IServer;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Aegis.Resilience.AspNetCore;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Distributed.Abstractions;
using Aegis.Resilience.Distributed.Redis;
using Aegis.Resilience.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Aegis.Tests;

/// <summary>
/// Sunucu tarafı koruma gerçek Kestrel üzerinden: gelen istek hız sınırlama (AspNetCoreRateLimit / WebApiThrottle
/// eşdeğeri) ve uç nokta başına Aegis boru hattı.
/// </summary>
public sealed class AspNetCoreInboundTests
{
    private sealed class TestApp(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;

        public HttpClient Client { get; } = client;

        public Task<HttpResponseMessage> GetAsync(string path, string? client = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (client is not null)
            {
                request.Headers.Add("X-ClientId", client);
            }

            return Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }

    private static async Task<TestApp> StartAsync(Action<WebApplicationBuilder> services, Action<WebApplication> pipeline)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        services(builder);
        var app = builder.Build();
        pipeline(app);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<AspNetServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new TestApp(app, new HttpClient { BaseAddress = new Uri(address) });
    }

    private static Task<TestApp> StartRateLimitedAsync(Action<AegisInboundRateLimitOptions> configure, Action<IServiceCollection>? services = null) =>
        StartAsync(
            b =>
            {
                services?.Invoke(b.Services);
                b.Services.AddAegisInboundRateLimiting(configure);
            },
            app =>
            {
                app.UseAegisInboundRateLimiting();
                app.MapGet("/api/urun", () => "urun");
                app.MapPost("/api/siparis", () => "siparis");
                app.MapGet("/health", () => "ok");
            });

    // ---------------------------------------------------------------- Gelen istek hız sınırlama

    [Fact]
    public async Task RateLimit_RejectsOverLimit_WithRetryAfterAndRateLimitHeaders()
    {
        await using var app = await StartRateLimitedAsync(o => o.PartitionByHeader("X-ClientId").AddRule("GET:/api/*", 2, TimeSpan.FromMinutes(1)));

        var first = await app.GetAsync("/api/urun", "mobil");
        Assert.Equal("2", first.Headers.GetValues("RateLimit-Limit").Single());
        Assert.Equal("1", first.Headers.GetValues("RateLimit-Remaining").Single());
        await app.GetAsync("/api/urun", "mobil");

        var rejected = await app.GetAsync("/api/urun", "mobil");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.InRange(rejected.Headers.RetryAfter!.Delta!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        Assert.Equal("0", rejected.Headers.GetValues("RateLimit-Remaining").Single());

        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("/api/urun", "web")).StatusCode); // başka istemci: ayrı kota
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("/health", "mobil")).StatusCode);  // kurala uymayan uç nokta
    }

    [Fact]
    public async Task RateLimit_AllMatchingRulesMustPass()
    {
        await using var app = await StartRateLimitedAsync(o => o
            .PartitionByHeader("X-ClientId")
            .AddRule("*", 3, TimeSpan.FromMinutes(1))
            .AddRule("POST:/api/siparis", 1, TimeSpan.FromMinutes(1)));

        Task<HttpResponseMessage> PostAsync() =>
            app.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/siparis") { Headers = { { "X-ClientId", "a" } } });

        Assert.Equal(HttpStatusCode.OK, (await PostAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAsync()).StatusCode);                       // POST kuralı: dakikada 1
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("/api/urun", "a")).StatusCode);      // genel kural: 3'ün 3.'sü
        Assert.Equal(HttpStatusCode.TooManyRequests, (await app.GetAsync("/api/urun", "a")).StatusCode);
    }

    [Fact]
    public async Task RateLimit_Whitelists_IpNetworkClientAndEndpoint()
    {
        await using var ipWhitelisted = await StartRateLimitedAsync(o =>
        {
            o.AddRule("*", 1, TimeSpan.FromMinutes(1));
            o.IpWhitelist.Add("127.0.0.0/8");
        });
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await ipWhitelisted.GetAsync("/api/urun")).StatusCode);
        }

        await using var clientAndEndpoint = await StartRateLimitedAsync(o =>
        {
            o.PartitionByHeader("X-ClientId").AddRule("*", 1, TimeSpan.FromMinutes(1));
            o.ClientWhitelist.Add("ic-servis");
            o.EndpointWhitelist.Add("GET:/health");
        });
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await clientAndEndpoint.GetAsync("/api/urun", "ic-servis")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await clientAndEndpoint.GetAsync("/health", "dis")).StatusCode);
        }
    }

    [Fact]
    public async Task RateLimit_CustomRejectionResponse()
    {
        await using var app = await StartRateLimitedAsync(o =>
        {
            o.PartitionByHeader("X-ClientId").AddRule("*", 1, TimeSpan.FromMinutes(1));
            o.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
            o.OnRejected = (context, rejection) => context.Response.WriteAsync($"kota:{rejection.PartitionKey}:{rejection.Rule.Limit}");
        });

        await app.GetAsync("/api/urun", "x");
        var rejected = await app.GetAsync("/api/urun", "x");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.Equal("kota:x:1", await rejected.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RateLimit_BindsFromConfiguration_ReloadsOnChange_AndKeepsOldRulesOnInvalidChange()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kota:Rules:0:Endpoint"] = "*",
            ["Kota:Rules:0:Limit"] = "1",
            ["Kota:Rules:0:Period"] = "00:01:00"
        }).Build();

        await using var app = await StartAsync(
            b => b.Services.AddAegisInboundRateLimiting(configuration.GetSection("Kota"), o => o.PartitionByHeader("X-ClientId")),
            a =>
            {
                a.UseAegisInboundRateLimiting();
                a.MapGet("/api/urun", () => "urun");
            });

        await app.GetAsync("/api/urun", "a");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await app.GetAsync("/api/urun", "a")).StatusCode);

        configuration["Kota:Rules:0:Limit"] = "5"; // yeni kural: yeni sayaç
        configuration.Reload();
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("/api/urun", "a")).StatusCode);

        configuration["Kota:Rules:0:Endpoint"] = "gecersiz-desen"; // geçersiz: yüklenmez, 5'lik kural sürer
        configuration.Reload();
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("/api/urun", "a")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await app.GetAsync("/api/urun", "a")).StatusCode);
    }

    [Fact]
    public void RateLimitOptions_FailFastOnInvalidRules()
    {
        Assert.Throws<ArgumentException>(() => new AegisInboundRateLimitOptions().AddRule("api/eksik-slash", 1, TimeSpan.FromSeconds(1)).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AegisInboundRateLimitOptions().AddRule("*", 0, TimeSpan.FromSeconds(1)).Validate());
        Assert.Throws<ArgumentException>(() => new AegisInboundRateLimitOptions { IpWhitelist = ["999.1.1.1"] }.Validate());
    }

    [SkippableFact]
    public async Task RateLimit_WithRedisStore_TwoServerInstancesShareOneQuota()
    {
        IConnectionMultiplexer? redis = null;
        try
        {
            var options = ConfigurationOptions.Parse(Environment.GetEnvironmentVariable("AEGIS_TEST_REDIS") ?? "localhost:6379");
            options.ConnectTimeout = 2000;
            options.AbortOnConnectFail = false;
            redis = await ConnectionMultiplexer.ConnectAsync(options);
        }
        catch (RedisException)
        {
        }

        Skip.If(redis is not { IsConnected: true }, "Redis yok - test atlandı.");
        using var _ = redis;
        var prefix = $"aegistest:inbound:{Guid.NewGuid():N}:";
        void Services(IServiceCollection s) => s.AddAegisRedisRateLimitStore(redis!, prefix, TimeSpan.FromSeconds(5));
        await using var podA = await StartRateLimitedAsync(o => o.PartitionByHeader("X-ClientId").AddRule("*", 3, TimeSpan.FromMinutes(1)), Services);
        await using var podB = await StartRateLimitedAsync(o => o.PartitionByHeader("X-ClientId").AddRule("*", 3, TimeSpan.FromMinutes(1)), Services);

        var accepted = 0;
        for (var i = 0; i < 3; i++)
        {
            accepted += (await podA.GetAsync("/api/urun", "k")).IsSuccessStatusCode ? 1 : 0;
            accepted += (await podB.GetAsync("/api/urun", "k")).IsSuccessStatusCode ? 1 : 0;
        }

        Assert.Equal(3, accepted); // 6 istekten 3'ü: iki sunucu örneği tek kotayı paylaşır
    }

    // ---------------------------------------------------------------- Uç nokta başına boru hattı

    private static Task<TestApp> StartWithPipelinesAsync(Action<WebApplication> endpoints, Action<IServiceCollection> pipelines) =>
        StartAsync(
            b => pipelines(b.Services),
            app =>
            {
                app.UseRouting();
                app.UseAegisInboundPipelines();
                endpoints(app);
                app.MapGet("/korumasiz", () => "ok");
            });

    [Fact]
    public async Task InboundPipeline_ConcurrencyLimit_Returns429()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartWithPipelinesAsync(
            a => a.MapGet("/rapor", async () => { await gate.Task; return "rapor"; }).RequireAegisPipeline("rapor"),
            s => s.AddAegisPipeline("rapor", b => b.AddConcurrencyLimiter(1, o => o.QueueTimeout = TimeSpan.Zero)));

        var first = app.GetAsync("/rapor");
        await Task.Delay(200); // ilk istek uç noktada bekliyor
        var second = await app.GetAsync("/rapor");
        gate.SetResult();

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("/korumasiz")).StatusCode);
    }

    [Fact]
    public async Task InboundPipeline_Timeout_Returns504_AndCancelsTheEndpoint()
    {
        var endpointCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartWithPipelinesAsync(
            a => a.MapGet("/yavas", async (HttpContext context) =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    endpointCancelled.TrySetResult(true);
                    throw;
                }

                return "gec";
            }).RequireAegisPipeline("yavas"),
            s => s.AddAegisPipeline("yavas", b => b.AddTimeout(TimeSpan.FromMilliseconds(200))));

        var response = await app.GetAsync("/yavas");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.True(await endpointCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task InboundPipeline_OpenCircuit_Returns503()
    {
        var control = new Aegis.Resilience.Core.Strategies.CircuitBreaker.CircuitBreakerManualControl();
        await using var app = await StartWithPipelinesAsync(
            a => a.MapGet("/odeme", () => "odeme").RequireAegisPipeline("odeme"),
            s => s.AddAegisPipeline("odeme", b => b.AddCircuitBreaker(o => o.ManualControl = control)));

        await control.IsolateAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await app.GetAsync("/odeme")).StatusCode);
    }

    [Fact]
    public async Task InboundPipeline_WithRetry_IsRejectedWithClearError()
    {
        await using var app = await StartWithPipelinesAsync(
            a => a.MapGet("/tekrar", () => "x").RequireAegisPipeline("tekrarli"),
            s => s.AddAegisPipeline("tekrarli", b => b.AddRetry()));

        Assert.Equal(HttpStatusCode.InternalServerError, (await app.GetAsync("/tekrar")).StatusCode); // uç noktayı yeniden çalıştırabilecek boru hattı reddedilir
    }
}
