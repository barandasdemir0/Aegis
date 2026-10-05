using System.Net.Http.Json;
using System.Text.Json;
using Shop.Api;
using Shop.Backends;

// Zamanlamaya duyarlı senaryolar (hedging, adaptif limit, kilitlenme) birbirinin CPU'sunu çalmasın.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Shop.Tests;

/// <summary>
/// Gerçek ortam: üç arka uç (AB, ABD, kanarya), gerçek Redis ve SQL Server, gerçek ağ üzerinde çalışan Shop.Api. Her test sınıfı
/// taze bir ortam alır (ayrı Redis öneki: devre ve kota durumu sınıflar arasında sızmaz).
/// <para>Bağımlılıklar: <c>SHOP_REDIS</c> (varsayılan localhost:6389) ve <c>SHOP_SQL</c> (varsayılan localhost,14330).</para>
/// </summary>
public sealed class ShopFixture : IAsyncLifetime
{
    public static readonly string Redis = Environment.GetEnvironmentVariable("SHOP_REDIS") ?? "localhost:6389";
    public static readonly string Sql = Environment.GetEnvironmentVariable("SHOP_SQL")
        ?? "Server=localhost,14330;User Id=sa;Password=Aegis!Test2026;TrustServerCertificate=True";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ShopApp? _secondPod;

    public BackendApp Eu { get; private set; } = null!;

    public BackendApp Us { get; private set; } = null!;

    public BackendApp Canary { get; private set; } = null!;

    public ShopApp Api { get; private set; } = null!;

    public ShopSettings Settings { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Eu = await BackendApp.StartAsync("eu");
        Us = await BackendApp.StartAsync("us");
        Canary = await BackendApp.StartAsync("canary");
        Settings = new ShopSettings(
            new BackendEndpoint(Eu.HttpAddress, Eu.GrpcAddress),
            new BackendEndpoint(Us.HttpAddress, Us.GrpcAddress),
            Canary.HttpAddress,
            Redis,
            Sql,
            $"shop-test-{Guid.NewGuid():N}");
        Api = await ShopApp.StartAsync(Settings);
        Client = new HttpClient { BaseAddress = Api.Address, Timeout = TimeSpan.FromSeconds(60) };
        await WaitUntilReadyAsync(Client);
    }

    /// <summary>Aynı Redis önekiyle ikinci bir örnek: aynı kümedeki ikinci pod (ortak devre, ortak kota, ortak önbellek).</summary>
    public async Task<HttpClient> SecondPodAsync()
    {
        _secondPod ??= await ShopApp.StartAsync(Settings);
        var client = new HttpClient { BaseAddress = _secondPod.Address, Timeout = TimeSpan.FromSeconds(60) };
        await WaitUntilReadyAsync(client);
        return client;
    }

    /// <summary>
    /// Pod hazır olana kadar bekler (Kubernetes readiness gibi). Redis bağlantısı arka planda kurulur; bağlanana kadar paylaşılan
    /// durum yerine pod-yerel duruma düşülür ve sağlık kontrolü bunu Degraded raporlar.
    /// </summary>
    private static async Task WaitUntilReadyAsync(HttpClient client)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync("/health");
            if ((await JsonOf(response)).GetProperty("status").GetString() == "Healthy")
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Pod 15 sn içinde hazır olmadı (Redis bağlantısı?).");
    }

    /// <summary>Tüm arka uçların senaryolarını ve çağrı günlüklerini temizler (her testin başında).</summary>
    public void ResetFaults()
    {
        Eu.Faults.Reset();
        Us.Faults.Reset();
        Canary.Faults.Reset();
    }

    public static string NewSku(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    public static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Json);

    public async Task DisposeAsync()
    {
        Client.Dispose();
        if (_secondPod is not null)
        {
            await _secondPod.DisposeAsync();
        }

        await Api.DisposeAsync();
        await Eu.DisposeAsync();
        await Us.DisposeAsync();
        await Canary.DisposeAsync();
    }
}
