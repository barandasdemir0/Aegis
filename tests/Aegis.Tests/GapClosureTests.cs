using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Chaos;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Extensions.Http;

namespace Aegis.Tests;

/// <summary>
/// Polly / Microsoft ile kalan farkları kapatan özellikler: CB sağlık bilgisi, ağırlıklı kaos sonucu, standart işleyicide
/// authority başına boru hattı, hedging işleyicisinde yapılandırma yeniden yükleme, genel <see cref="Reloadable{T}"/>.
/// </summary>
public class GapClosureTests
{
    // ---------------------------------------------------------------- CB sağlık bilgisi

    [Fact]
    public async Task BreakDurationGenerator_ReceivesWindowHealth()
    {
        CircuitHealth? seen = null;
        using var pipeline = new AegisPipelineBuilder("cb-health")
            .AddCircuitBreaker(o =>
            {
                o.MinimumThroughput = 4;
                o.FailureRatio = 0.5;
                o.BreakDurationGenerator = e =>
                {
                    seen = e.Health;
                    return e.Health.FailureRate >= 0.75 ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30);
                };
            })
            .Build();

        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1));
        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()));
        }

        Assert.Equal(new CircuitHealth(1, 3, 0), seen);
        Assert.Equal(4, seen!.Value.Throughput);
        Assert.Equal(0.75, seen.Value.FailureRate);
    }

    // ---------------------------------------------------------------- Ağırlıklı kaos

    [Fact]
    public async Task ChaosOutcomeGenerator_PicksByWeight()
    {
        var generator = new ChaosOutcomeGenerator()
            .AddResult(_ => "sahte", weight: 70)
            .AddException<TimeoutException>(weight: 30);

        async Task<object> Run(double random)
        {
            using var pipeline = new AegisPipelineBuilder("chaos-weighted")
                .AddChaosOutcome(1.0, generator, o => o.Randomizer = () => random)
                .Build();
            try
            {
                return await pipeline.ExecuteAsync(_ => ValueTask.FromResult("gercek"));
            }
            catch (TimeoutException ex)
            {
                return ex;
            }
        }

        Assert.Equal("sahte", await Run(0.10));             // [0, 0.70) → sonuç
        Assert.IsType<TimeoutException>(await Run(0.90));   // [0.70, 1) → istisna
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChaosOutcomeGenerator().AddResult(_ => 1, weight: 0));
    }

    // ---------------------------------------------------------------- Authority başına boru hattı

    private sealed class FakeServer : HttpMessageHandler
    {
        public ConcurrentQueue<string> Hosts { get; } = new();
        public Func<HttpRequestMessage, HttpStatusCode> Status { get; set; } = _ => HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hosts.Enqueue(request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(Status(request)));
        }
    }

    private static (HttpClient Client, ServiceProvider Provider) CreateClient(FakeServer server, Action<IHttpClientBuilder> configure)
    {
        var services = new ServiceCollection();
        var builder = services.AddHttpClient("gap");
        configure(builder);
        builder.ConfigurePrimaryHttpMessageHandler(() => server);
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IHttpClientFactory>().CreateClient("gap"), provider);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StandardHandler_SelectPipelineByAuthority_IsolatesFailingHost(bool perAuthority)
    {
        var server = new FakeServer { Status = r => r.RequestUri!.Host == "cokuk.local" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK };
        var (client, provider) = CreateClient(server, b => b.AddStandardAegisHandler(o =>
        {
            o.Retry.MaxRetryAttempts = 0;
            o.CircuitBreaker.MinimumThroughput = 2;
            o.CircuitBreaker.FailureRatio = 0.5;
            o.CircuitBreaker.BreakDuration = TimeSpan.FromMinutes(1);
            if (perAuthority)
            {
                o.SelectPipelineByAuthority();
            }
        }));
        await using var _ = provider;

        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://cokuk.local/x"));
        }

        if (perAuthority)
        {
            // Çöken host'un devresi açık; sağlıklı host etkilenmez (Microsoft SelectPipelineByAuthority ile aynı)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("https://saglam.local/x")).StatusCode);
        }
        else
        {
            // Paylaşılan tek devre: sağlıklı host'a giden istek de reddedilir (eski davranış korunur)
            await Assert.ThrowsAsync<Aegis.Resilience.Core.Exceptions.BrokenCircuitException>(() => client.GetAsync("https://saglam.local/x"));
        }
    }

    [Fact]
    public void StandardOptions_StateProviderWithPerAuthority_FailsFast()
    {
        var options = new AegisHttpStandardResilienceOptions().SelectPipelineByAuthority();
        options.CircuitBreaker.StateProvider = new CircuitBreakerStateProvider();

        Assert.Throws<ArgumentException>(options.Validate);
    }

    // ---------------------------------------------------------------- Hedging işleyicisinde yeniden yükleme

    [Fact]
    public async Task StandardHedging_ReloadsRoutingFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hedge:OrderedGroups:0:Endpoints:0:Uri"] = "https://eu.local"
            })
            .Build();
        var server = new FakeServer();
        var (client, provider) = CreateClient(server, b => b.AddStandardAegisHedgingHandler(config.GetSection("Hedge")));
        await using var _ = provider;

        await client.GetAsync("https://orijinal.local/a");
        config["Hedge:OrderedGroups:0:Endpoints:0:Uri"] = "https://us.local";
        config.Reload();
        await client.GetAsync("https://orijinal.local/b");

        Assert.Equal(["eu.local", "us.local"], server.Hosts);
    }

    // ---------------------------------------------------------------- Reloadable<T>

    private sealed class Resource(int generation, List<int> disposed) : IDisposable
    {
        public int Generation { get; } = generation;

        public void Dispose() => disposed.Add(Generation);
    }

    [Fact]
    public void Reloadable_LeaseKeepsOldGenerationAliveUntilReleased()
    {
        var disposed = new List<int>();
        var next = 0;
        using var reloadable = new Reloadable<Resource>(() => new Resource(++next, disposed));

        var lease = reloadable.Acquire();
        Assert.True(reloadable.Reload());
        Assert.Equal(2, reloadable.Current.Generation);
        Assert.Empty(disposed); // eski nesil hâlâ kiralı

        lease.Dispose();
        Assert.Equal([1], disposed);

        reloadable.Dispose();
        Assert.Equal([1, 2], disposed);
    }
}
