using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Shop.Backends;

namespace Shop.Tests;

/// <summary>
/// İki ayrı .NET Framework 4.8 süreci (Web API 2, OWIN). Aynı Redis önekiyle çalışırlar: ortak gelen istek kotası. Arka uca
/// giden çağrı .NET Framework'te Aegis ile yeniden denenir.
/// </summary>
public sealed class LegacyWebFixture : IAsyncLifetime
{
    private readonly List<Process> _processes = [];

    public BackendApp Eu { get; private set; } = null!;

    public HttpClient First { get; private set; } = null!;

    public HttpClient Second { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Eu = await BackendApp.StartAsync("eu");
        var prefix = $"legacy-test-{Guid.NewGuid():N}";
        First = await StartAsync(prefix);
        Second = await StartAsync(prefix);
    }

    private async Task<HttpClient> StartAsync(string prefix)
    {
        var port = FreePort();
        var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Shop.LegacyWeb", "bin", "Release", "net48", "Shop.LegacyWeb.exe"));
        Assert.True(File.Exists(exe), $".NET Framework uygulaması derlenmemiş: {exe}");
        var processInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(exe, $"{port} {Eu.HttpAddress} {ShopFixture.Redis} {prefix}")
            : new ProcessStartInfo("mono", $"\"{exe}\" {port} {Eu.HttpAddress} {ShopFixture.Redis} {prefix}");
        processInfo.RedirectStandardOutput = true;
        processInfo.UseShellExecute = false;
        processInfo.CreateNoWindow = true;
        var process = Process.Start(processInfo)!;
        _processes.Add(process);

        var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("READY", ready);
        return new HttpClient { BaseAddress = new Uri($"http://localhost:{port}/") };
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async Task DisposeAsync()
    {
        foreach (var process in _processes)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            process.Dispose();
        }

        First.Dispose();
        Second.Dispose();
        await Eu.DisposeAsync();
    }
}

public sealed class LegacyWebTests(LegacyWebFixture legacy) : IClassFixture<LegacyWebFixture>
{
    // .NET Framework 4.8'de Aegis çekirdeği + HTTP işleyicisi: geçici 503 yeniden denenir.
    [Fact]
    public async Task NetFramework_OutgoingCall_Retried()
    {
        legacy.Eu.Faults.Reset();
        legacy.Eu.Faults.SetPlan("legacy-orders", [new FaultStep { Status = 503, Times = 2 }]);

        using var response = await legacy.First.GetAsync("api/legacy-orders/42");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("4.", (await ShopFixture.JsonOf(response)).GetProperty("framework").GetString()); // gerçekten .NET Framework CLR
        Assert.Equal(3, legacy.Eu.Faults.Calls("legacy-orders").Count);
    }

    // Aegis.Resilience.WebApi: istemci başına kota, iki .NET Framework örneği arasında Redis ile ortak; 429 + Retry-After + kota başlıkları.
    [Fact]
    public async Task WebApi2_RateLimit_SharedAcrossTwoInstancesViaRedis()
    {
        HttpRequestMessage Ping(string client)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "api/ping");
            request.Headers.Add("X-ClientId", client);
            return request;
        }

        using var first = await legacy.First.SendAsync(Ping("bayi-1"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("2", first.Headers.GetValues("RateLimit-Remaining").Single());
        Assert.Equal(HttpStatusCode.OK, (await legacy.Second.SendAsync(Ping("bayi-1"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await legacy.First.SendAsync(Ping("bayi-1"))).StatusCode);

        using var rejected = await legacy.Second.SendAsync(Ping("bayi-1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);

        Assert.Equal(HttpStatusCode.OK, (await legacy.Second.SendAsync(Ping("bayi-2"))).StatusCode);
    }

    // Beyaz listedeki uç nokta (sağlık kontrolü) asla sınırlanmaz.
    [Fact]
    public async Task WebApi2_WhitelistedEndpoint_NeverLimited()
    {
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await legacy.First.GetAsync("api/health")).StatusCode);
        }
    }
}
