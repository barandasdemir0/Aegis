using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.Http;

namespace Aegis.Tests;

/// <summary>
/// Aşama 5 — Microsoft.Extensions.Http.Resilience eşitliği: standart handler seçenekleri + tutarlılık doğrulaması +
/// IConfiguration bağlama/yeniden yükleme, standart hedging + sıralı/ağırlıklı yönlendirme + uç nokta başına devre kesici.
/// </summary>
public class HttpStandardParityStage5Tests
{
    /// <summary>Host başına programlanabilir sahte sunucu.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public ConcurrentQueue<(string Host, string PathAndQuery, HttpMethod Method)> Requests { get; } = new();
        public Func<HttpRequestMessage, int, Task<HttpResponseMessage>> Behavior { get; set; } =
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        private int _count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue((request.RequestUri!.Host, request.RequestUri.PathAndQuery, request.Method));
            return Behavior(request, Interlocked.Increment(ref _count));
        }
    }

    private static (HttpClient Client, ServiceProvider Provider) CreateClient(FakeServer server, Action<IHttpClientBuilder> configure)
    {
        var services = new ServiceCollection();
        var builder = services.AddHttpClient("test", c => c.BaseAddress = new Uri("https://primary.local"));
        configure(builder);
        builder.ConfigurePrimaryHttpMessageHandler(() => server);
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IHttpClientFactory>().CreateClient("test"), provider);
    }

    // ---------------------------------------------------------------- Standart handler

    [Fact]
    public void StandardOptions_ConsistencyValidation_FailsFastLikeMicrosoft()
    {
        var attemptTooLong = new AegisHttpStandardResilienceOptions();
        attemptTooLong.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60); // toplam 30 sn
        Assert.Contains("toplam zaman aşımından", Assert.Throws<ArgumentException>(attemptTooLong.Validate).Message);

        var samplingTooShort = new AegisHttpStandardResilienceOptions();
        samplingTooShort.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(15); // < 2 × 10 sn
        Assert.Contains("iki katı", Assert.Throws<ArgumentException>(samplingTooShort.Validate).Message);

        new AegisHttpStandardResilienceOptions().Validate(); // varsayılanlar tutarlı

        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddHttpClient("x").AddStandardAegisHandler(o => o.AttemptTimeout.Timeout = TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task StandardHandler_RetriesTransientStatus_ThenSucceeds()
    {
        var server = new FakeServer
        {
            Behavior = (_, n) => Task.FromResult(new HttpResponseMessage(n < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK))
        };
        var (client, provider) = CreateClient(server, b => b.AddStandardAegisHandler(o =>
        {
            o.Retry.Delay = TimeSpan.Zero;
            o.Retry.UseJitter = false;
        }));
        await using var _ = provider;

        var response = await client.GetAsync("/siparis");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, server.Requests.Count);
    }

    [Fact]
    public async Task StandardHandler_BindsFromConfiguration_AndReloadsOnChange()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aegis:Retry:MaxRetryAttempts"] = "1",
                ["Aegis:Retry:Delay"] = "00:00:00",
                ["Aegis:Retry:UseJitter"] = "false",
                ["Aegis:AttemptTimeout:Timeout"] = "00:00:05",
                ["Aegis:CircuitBreaker:MinimumThroughput"] = "1000"
            })
            .Build();
        var server = new FakeServer { Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) };
        var (client, provider) = CreateClient(server, b => b.AddStandardAegisHandler(config.GetSection("Aegis")));
        await using var _ = provider;

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/a"));
        Assert.Equal(2, server.Requests.Count); // 1 + 1 retry (appsettings'ten)

        config["Aegis:Retry:MaxRetryAttempts"] = "3";
        config.Reload(); // appsettings.json değişti

        server.Requests.Clear();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/b"));
        Assert.Equal(4, server.Requests.Count); // yeni boru hattı: 1 + 3 retry
    }

    [Fact]
    public async Task StandardHandler_InvalidConfiguration_FailsAtStartup()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Aegis:AttemptTimeout:Timeout"] = "00:05:00" })
            .Build();
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddHttpClient("x").AddStandardAegisHandler(config.GetSection("Aegis")));
        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------- Standart hedging

    [Fact]
    public async Task StandardHedging_OrderedGroups_SecondRegionWinsWhenPrimaryFails()
    {
        var server = new FakeServer
        {
            Behavior = (req, _) => Task.FromResult(new HttpResponseMessage(
                req.RequestUri!.Host == "eu.local" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(req.RequestUri.Host)
            })
        };
        var (client, provider) = CreateClient(server, b => b.AddStandardAegisHedgingHandler(o =>
        {
            o.HedgingDelay = System.Threading.Timeout.InfiniteTimeSpan; // yalnızca başarısızlıkta sonraki bölge
            o.MaxHedgedAttempts = 2;
            o.OrderedGroups.Add(new UriEndpointGroup { Endpoints = { new WeightedUriEndpoint { Uri = new Uri("https://eu.local") } } });
            o.OrderedGroups.Add(new UriEndpointGroup { Endpoints = { new WeightedUriEndpoint { Uri = new Uri("https://us.local") } } });
        }));
        await using var _ = provider;

        var response = await client.GetAsync("/fiyat?id=7");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("us.local", await response.Content.ReadAsStringAsync());
        Assert.Equal(["eu.local", "us.local"], server.Requests.Select(r => r.Host)); // grup sayısı kadar deneme
        Assert.All(server.Requests, r => Assert.Equal("/fiyat?id=7", r.PathAndQuery)); // yol ve sorgu korunur
    }

    [Fact]
    public async Task StandardHedging_PerEndpointCircuitBreaker_IsolatesFailingRegion()
    {
        var server = new FakeServer
        {
            Behavior = (req, _) => Task.FromResult(new HttpResponseMessage(
                req.RequestUri!.Host == "eu.local" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK))
        };
        var (client, provider) = CreateClient(server, b => b.AddStandardAegisHedgingHandler(o =>
        {
            o.HedgingDelay = System.Threading.Timeout.InfiniteTimeSpan;
            o.EndpointCircuitBreaker.MinimumThroughput = 2;
            o.EndpointCircuitBreaker.FailureRatio = 0.5;
            o.EndpointCircuitBreaker.BreakDuration = TimeSpan.FromMinutes(1);
            o.OrderedGroups.Add(new UriEndpointGroup { Endpoints = { new WeightedUriEndpoint { Uri = new Uri("https://eu.local") } } });
            o.OrderedGroups.Add(new UriEndpointGroup { Endpoints = { new WeightedUriEndpoint { Uri = new Uri("https://us.local") } } });
        }));
        await using var _ = provider;

        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/x")).StatusCode);
        }

        server.Requests.Clear();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/x")).StatusCode);
        // eu devresi açık: eu'ya hiç istek gitmedi, doğrudan us kazandı
        Assert.Equal(["us.local"], server.Requests.Select(r => r.Host));
    }

    [Fact]
    public async Task StandardHedging_NonIdempotentPost_IsNotHedged()
    {
        var server = new FakeServer { Behavior = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) };
        var (client, provider) = CreateClient(server, b => b.AddStandardAegisHedgingHandler(o =>
        {
            o.HedgingDelay = System.Threading.Timeout.InfiniteTimeSpan;
            o.OrderedGroups.Add(new UriEndpointGroup { Endpoints = { new WeightedUriEndpoint { Uri = new Uri("https://eu.local") } } });
            o.OrderedGroups.Add(new UriEndpointGroup { Endpoints = { new WeightedUriEndpoint { Uri = new Uri("https://us.local") } } });
        }));
        await using var _ = provider;

        var response = await client.PostAsync("/odeme", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); // gerçek yanıt, tek deneme
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task StandardHedging_WeightedGroups_RespectWeights()
    {
        var server = new FakeServer();
        var (client, provider) = CreateClient(server, b => b.AddStandardAegisHedgingHandler(o =>
        {
            o.WeightedGroups.Add(new WeightedUriEndpointGroup { Weight = 1, Endpoints = { new WeightedUriEndpoint { Uri = new Uri("https://kanarya.local") } } });
            o.WeightedGroups.Add(new WeightedUriEndpointGroup { Weight = 99, Endpoints = { new WeightedUriEndpoint { Uri = new Uri("https://kararli.local") } } });
        }));
        await using var _ = provider;

        for (var i = 0; i < 200; i++)
        {
            await client.GetAsync("/");
        }

        var stable = server.Requests.Count(r => r.Host == "kararli.local");
        Assert.InRange(stable, 180, 200); // ~%99
        Assert.Equal(200, server.Requests.Count); // başarılı ilk deneme: hedging yok
    }

    [Fact]
    public void StandardHedging_BindsGroupsFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hedge:MaxHedgedAttempts"] = "3",
                ["Hedge:OrderedGroups:0:Endpoints:0:Uri"] = "https://eu.local",
                ["Hedge:OrderedGroups:1:Endpoints:0:Uri"] = "https://us.local",
                ["Hedge:OrderedGroups:1:Endpoints:0:Weight"] = "10"
            })
            .Build();

        var options = new AegisHttpStandardHedgingOptions();
        config.GetSection("Hedge").Bind(options);
        options.Validate();

        Assert.Equal(3, options.MaxHedgedAttempts);
        Assert.Equal(2, options.OrderedGroups.Count);
        Assert.Equal(new Uri("https://us.local"), options.OrderedGroups[1].Endpoints[0].Uri);
        Assert.Equal(10, options.OrderedGroups[1].Endpoints[0].Weight);
    }
}
