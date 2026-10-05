using System.Net;
using System.Web.Http;
using Aegis.Resilience.WebApi;

namespace Aegis.CompatibilityTests;

public sealed class PingController : ApiController
{
    [HttpGet, Route("api/ping")]
    public string Ping() => "pong";

    [HttpGet, Route("health")]
    public string Health() => "ok";
}

/// <summary>
/// Klasik ASP.NET Web API 2 gelen istek hız sınırı (WebApiThrottle eşdeğeri): gerçek .NET Framework 4.8 üzerinde,
/// bellek içi <see cref="HttpServer"/> ile tam Web API hattından geçerek.
/// </summary>
public class WebApiRateLimitingTests
{
    private static HttpClient NewClient(Action<AegisWebApiRateLimitOptions> configure)
    {
        var config = new HttpConfiguration();
        config.MapHttpAttributeRoutes();
        config.UseAegisRateLimiting(configure);
        config.EnsureInitialized();
        return new HttpClient(new HttpServer(config)) { BaseAddress = new Uri("http://localhost/") };
    }

    private static Action<AegisWebApiRateLimitOptions> PerClient(int limit) =>
        o => o.PartitionByHeader("X-ClientId").AddRule("GET:/api/*", limit, TimeSpan.FromMinutes(1));

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string clientId = "a")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-ClientId", clientId);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Rejects_With_429_And_Headers_After_Limit()
    {
        using var client = NewClient(PerClient(2));

        using var first = await GetAsync(client, "api/ping");
        using var second = await GetAsync(client, "api/ping");
        using var third = await GetAsync(client, "api/ping");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("1", first.Headers.GetValues("RateLimit-Remaining").Single());
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal((HttpStatusCode)429, third.StatusCode);
        Assert.Equal("0", third.Headers.GetValues("RateLimit-Remaining").Single());
        Assert.NotNull(third.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task Partitions_Are_Independent()
    {
        using var client = NewClient(PerClient(1));

        using var a1 = await GetAsync(client, "api/ping", "a");
        using var a2 = await GetAsync(client, "api/ping", "a");
        using var b1 = await GetAsync(client, "api/ping", "b");

        Assert.Equal(HttpStatusCode.OK, a1.StatusCode);
        Assert.Equal((HttpStatusCode)429, a2.StatusCode);
        Assert.Equal(HttpStatusCode.OK, b1.StatusCode);
    }

    [Fact]
    public async Task Unmatched_And_Whitelisted_Requests_Pass()
    {
        using var client = NewClient(o =>
        {
            PerClient(1)(o);
            o.ClientWhitelist.Add("ic-servis");
        });

        for (var i = 0; i < 5; i++)
        {
            using var health = await GetAsync(client, "health");
            using var internalCall = await GetAsync(client, "api/ping", "ic-servis");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Equal(HttpStatusCode.OK, internalCall.StatusCode);
        }
    }

    [Fact]
    public async Task OnRejected_Can_Write_Body()
    {
        using var client = NewClient(o =>
        {
            PerClient(1)(o);
            o.OnRejected = (_, rejection, response) => response.Content = new StringContent($"kota: {rejection.Rule.Limit}");
        });

        using var _ = await GetAsync(client, "api/ping");
        using var rejected = await GetAsync(client, "api/ping");

        Assert.Equal("kota: 1", await rejected.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("api/ping")]
    [InlineData("10.0.0.0/33")]
    public void Invalid_Options_Fail_Fast(string bad)
    {
        var config = new HttpConfiguration();
        Assert.ThrowsAny<ArgumentException>(() => config.UseAegisRateLimiting(o =>
        {
            if (bad.Contains('/') && bad.Contains('.'))
            {
                o.IpWhitelist.Add(bad);
            }
            else
            {
                o.AddRule(bad, 1, TimeSpan.FromSeconds(1));
            }
        }));
    }
}
