using System.Net;
using System.Net.Sockets;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Grpc.AspNetCore;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Shop.Backends;

/// <summary>Çalışan bir arka uç örneği: REST (HTTP/1.1) ve gRPC (HTTP/2, düz metin) ayrı portlarda.</summary>
public sealed class BackendApp : IAsyncDisposable
{
    private readonly WebApplication _app;

    private BackendApp(WebApplication app, string instance, Uri http, Uri grpc)
    {
        _app = app;
        Instance = instance;
        HttpAddress = http;
        GrpcAddress = grpc;
    }

    public string Instance { get; }

    public Uri HttpAddress { get; }

    public Uri GrpcAddress { get; }

    public FaultBook Faults => _app.Services.GetRequiredService<FaultBook>();

    /// <summary>Arka ucu başlatır. <paramref name="grpcServerConcurrency"/>: gRPC sunucusunun Aegis eşzamanlılık sınırı.</summary>
    public static async Task<BackendApp> StartAsync(string instance, int grpcServerConcurrency = 64)
    {
        var httpPort = FreePort();
        var grpcPort = FreePort();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, httpPort, o => o.Protocols = HttpProtocols.Http1);
            k.Listen(IPAddress.Loopback, grpcPort, o => o.Protocols = HttpProtocols.Http2);
        });

        builder.Services.AddSingleton<FaultBook>();
        builder.Services.AddSingleton(new BackendInfo(instance));
        // Sunucu koruması: eşzamanlılık sınırı dolunca ResourceExhausted + pushback döner (Aegis.Resilience.Grpc.AspNetCore).
        builder.Services.AddGrpc(o => o.AddAegisResilience(p => p
            .AddConcurrencyLimiter(grpcServerConcurrency)
            .AddTimeout(TimeSpan.FromSeconds(10))));

        var app = builder.Build();
        app.MapGrpcService<InventoryService>();
        RestEndpoints.Map(app, instance);
        await app.StartAsync();
        return new BackendApp(app, instance, new Uri($"http://127.0.0.1:{httpPort}"), new Uri($"http://127.0.0.1:{grpcPort}"));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
