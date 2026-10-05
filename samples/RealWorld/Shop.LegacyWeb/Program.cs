using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Distributed.Redis;
using Aegis.Resilience.Extensions.Http;
using Aegis.Resilience.WebApi;
using Microsoft.Owin.Hosting;
using Owin;
using StackExchange.Redis;

namespace Shop.LegacyWeb;

/// <summary>
/// Eski kurumsal sipariş portalı: .NET Framework 4.8, Web API 2, OWIN self-host. Gelen istek kotası Redis'te (tüm örneklerde
/// ortak), arka uca giden çağrı Aegis ile yeniden denenir.
/// Kullanım: Shop.LegacyWeb.exe {port} {arka uç adresi} {redis} {anahtar öneki}
/// </summary>
public static class Program
{
    public static Uri Backend { get; private set; } = null!;

    public static void Main(string[] args)
    {
        var port = int.Parse(args[0]);
        Backend = new Uri(args[1]);
        var redis = ConnectionMultiplexer.Connect(args[2]);
        var prefix = args[3];

        using (WebApp.Start($"http://localhost:{port}/", app =>
        {
            var config = new HttpConfiguration();
            config.MapHttpAttributeRoutes();
            config.UseAegisRateLimiting(o =>
            {
                o.PartitionByHeader("X-ClientId");
                o.AddRule("GET:/api/ping", 3, TimeSpan.FromSeconds(30));
                o.EndpointWhitelist.Add("GET:/api/health");
                o.KeyPrefix = $"{prefix}:legacy";
            }, new RedisRateLimitStore(redis, $"{prefix}:rl:"));
            app.UseWebApi(config);
        }))
        {
            Console.WriteLine("READY");
            Thread.Sleep(Timeout.Infinite);
        }
    }
}

public sealed class LegacyOrdersController : ApiController
{
    // .NET Framework'te Aegis: boru hattı + HTTP işleyicisi (DI ve HttpClientFactory olmadan).
    private static readonly IAegisPipeline Pipeline = new AegisPipelineBuilder("legacy-portal")
        .AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.FromMilliseconds(20); })
        .AddTimeout(TimeSpan.FromSeconds(5))
        .Build();

    private static readonly HttpClient Client = new(new AegisResilienceHandler(Pipeline) { InnerHandler = new HttpClientHandler() });

    [HttpGet, Route("api/legacy-orders/{id}")]
    public async Task<IHttpActionResult> Get(string id)
    {
        using (var response = await Client.GetAsync(new Uri(Program.Backend, "/svc/legacy-orders")))
        {
            response.EnsureSuccessStatusCode();
        }

        return Ok(new { id, framework = Environment.Version.ToString() });
    }

    [HttpGet, Route("api/ping")]
    public IHttpActionResult Ping() => Ok(new { ok = true });

    [HttpGet, Route("api/health")]
    public IHttpActionResult Health() => Ok(new { status = "Healthy" });
}
