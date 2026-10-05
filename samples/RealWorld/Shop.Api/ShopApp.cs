using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Aegis.Resilience.AspNetCore;
using Aegis.Resilience.Extensions.Aspire;
using Aegis.Resilience.Extensions.Dashboard;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Shop.Api;

/// <summary>Çalışan bir Shop.Api örneği (gerçek Kestrel, gerçek ağ).</summary>
public sealed class ShopApp : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ShopApp(WebApplication app, Uri address, AdminConfigurationProvider admin)
    {
        _app = app;
        Address = address;
        Admin = admin;
    }

    public Uri Address { get; }

    public IServiceProvider Services => _app.Services;

    public AdminConfigurationProvider Admin { get; }

    public static async Task<ShopApp> StartAsync(ShopSettings settings, int port = 0)
    {
        port = port == 0 ? FreePort() : port;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port, o => o.Protocols = HttpProtocols.Http1));

        var admin = new AdminConfigurationSource();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Chaos:Enabled"] = "false",
            ["Chaos:Rate"] = "0",
            ["Retries:Attempts"] = "1",
            ["Fees:TimeoutMs"] = "1000",
            // Aspire ServiceDefaults'un tüm HttpClient'lara verdiği standart işleyicinin ayarı (testte hızlı yeniden deneme).
            ["Aegis:Http:Retry:Delay"] = "00:00:00.020",
        });
        ((IConfigurationBuilder)builder.Configuration).Add(admin);

        builder.Services.AddSingleton(settings);
        builder.Services.Configure<ChaosSettings>(builder.Configuration.GetSection("Chaos"));
        builder.Services.Configure<RetrySettings>(builder.Configuration.GetSection("Retries"));

        // .NET Aspire ServiceDefaults: AddAegis + tüm HttpClient'lara standart işleyici + metrik + iz + sağlık kontrolü.
        builder.AddAegisServiceDefaults();
        builder.Services.AddShopResilience(settings);
        builder.Services.AddAdvancedFeatures(settings, builder.Configuration);
        builder.Services.AddCoverageFeatures(settings);
        builder.Logging.AddFilter("Aegis", LogLevel.Debug); // Aegis olay günlüğü (otomatik, Polly ile aynı biçim)

        var app = builder.Build();
        app.UseShopErrorMapping();
        app.UseAegisInboundRateLimiting();
        app.UseRouting();
        app.UseAegisInboundPipelines();

        ShopEndpoints.Map(app);
        app.MapAdvancedFeatures();
        app.MapCoverageFeatures();
        app.MapAegisDashboard("/aegis");
        // Canlılık (açık devre Degraded) ve hazır olma (açık devre Unhealthy) ayrı uç noktalar.
        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteHealthAsync, Predicate = r => r.Name != "aegis_ready" });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { ResponseWriter = WriteHealthAsync, Predicate = r => r.Name == "aegis_ready" });

        await app.Services.GetRequiredService<OrderStore>().EnsureCreatedAsync(CancellationToken.None);
        await app.StartAsync();
        return new ShopApp(app, new Uri($"http://127.0.0.1:{port}"), admin.Provider);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static Task WriteHealthAsync(HttpContext context, Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => new { status = e.Value.Status.ToString(), e.Value.Description })
        }));
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
