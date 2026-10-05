using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.Http;

namespace Aegis.Tests;

/// <summary>
/// Senkron <c>HttpClient.Send</c> (.NET 5+) de Aegis'ten geçmelidir (Microsoft: <c>ResilienceHandler.Send</c> ezmesi).
/// <c>DelegatingHandler.Send</c> varsayılanı iç işleyicinin <c>Send</c>'ini çağırır; ezilmezse dayanıklılık sessizce atlanır.
/// </summary>
public class HttpSyncSendTests
{
    private sealed class FlakyServer(int failuresBeforeSuccess) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ConcurrentQueue<string> Hosts { get; } = new();

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) => Respond(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Respond(request));

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            Hosts.Enqueue(request.RequestUri!.Host);
            return new HttpResponseMessage(Interlocked.Increment(ref _calls) <= failuresBeforeSuccess
                ? HttpStatusCode.ServiceUnavailable
                : HttpStatusCode.OK);
        }
    }

    private static HttpClient Client(FlakyServer server, Action<IHttpClientBuilder> configure)
    {
        var services = new ServiceCollection();
        var builder = services.AddHttpClient("sync");
        configure(builder);
        builder.ConfigurePrimaryHttpMessageHandler(() => server);
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient("sync");
    }

    public static TheoryData<string> Handlers => ["standard", "named", "inline", "context", "dynamic"];

    [Theory]
    [MemberData(nameof(Handlers))]
    public void SyncSend_GoesThroughTheResiliencePipeline(string handler)
    {
        var server = new FlakyServer(failuresBeforeSuccess: 2);
        var client = Client(server, b =>
        {
            b.Services.AddAegisPipeline("adli", p => p.AddRetry(o => o.Delay = TimeSpan.Zero));
            _ = handler switch
            {
                "standard" => b.AddStandardAegisHandler(o => { o.Retry.Delay = TimeSpan.Zero; o.Retry.UseJitter = false; }),
                "named" => b.AddAegisResilienceHandler("adli"),
                "inline" => b.AddAegisResilienceHandler(p => p.AddRetry(o => o.Delay = TimeSpan.Zero)),
                "context" => b.AddAegisResilienceHandler((p, _) => p.AddRetry(o => o.Delay = TimeSpan.Zero)),
                _ => b.AddAegisDynamicHandler(_ => "adli")
            };
        });

        using var response = client.Send(new HttpRequestMessage(HttpMethod.Get, "https://api.local/a"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, server.Calls);
    }

    [Fact]
    public void SyncSend_StandardHedging_UsesRoutingGroups()
    {
        var server = new FlakyServer(failuresBeforeSuccess: 1);
        var client = Client(server, b => b.AddStandardAegisHedgingHandler(o =>
        {
            o.HedgingDelay = Timeout.InfiniteTimeSpan;
            o.OrderedGroups.Add(new UriEndpointGroup { Endpoints = { new WeightedUriEndpoint { Uri = new Uri("https://eu.local") } } });
            o.OrderedGroups.Add(new UriEndpointGroup { Endpoints = { new WeightedUriEndpoint { Uri = new Uri("https://us.local") } } });
        }));

        using var response = client.Send(new HttpRequestMessage(HttpMethod.Get, "https://orijinal.local/fiyat"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["eu.local", "us.local"], server.Hosts);
    }

    [Fact]
    public void SyncSend_ReplayHandler_BuffersAndRetries()
    {
        var server = new FlakyServer(failuresBeforeSuccess: 0);
        var client = Client(server, b => b.AddHttpRequestReplayHandler());

        using var response = client.Send(new HttpRequestMessage(HttpMethod.Post, "https://api.local/a") { Content = new StringContent("{}") });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
