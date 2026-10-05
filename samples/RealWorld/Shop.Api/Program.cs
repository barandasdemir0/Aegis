using Shop.Api;

// Tek başına çalıştırma (bağımlılık adresleri ortam değişkenlerinden). Testler ShopApp.StartAsync ile başlatır.
static Uri Env(string name, string fallback) => new(Environment.GetEnvironmentVariable(name) ?? fallback);

var settings = new ShopSettings(
    new BackendEndpoint(Env("SHOP_EU_HTTP", "http://127.0.0.1:5101"), Env("SHOP_EU_GRPC", "http://127.0.0.1:5102")),
    new BackendEndpoint(Env("SHOP_US_HTTP", "http://127.0.0.1:5201"), Env("SHOP_US_GRPC", "http://127.0.0.1:5202")),
    Env("SHOP_CANARY_HTTP", "http://127.0.0.1:5301"),
    Environment.GetEnvironmentVariable("SHOP_REDIS") ?? "localhost:6389",
    Environment.GetEnvironmentVariable("SHOP_SQL") ?? "Server=localhost,14330;User Id=sa;Password=Aegis!Test2026;TrustServerCertificate=True",
    "shop");

await using var shop = await ShopApp.StartAsync(settings, port: 5000);
Console.WriteLine($"Shop.Api: {shop.Address}  (pano: {shop.Address}aegis)");
await Task.Delay(Timeout.Infinite);
