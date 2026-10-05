using System.Text.Json.Serialization;

namespace Shop.Api;

public sealed record OrderRequest(string Sku, int Quantity, string Customer);

public sealed record OrderReply(string OrderId, string PaymentId, decimal Price, string ReservedBy, string PricedBy);

public sealed record Product(string Sku, string Name, int Stock, string Source);

public sealed record FxRate(string Symbol, decimal Rate, int Version);

public sealed record FxReply(string Symbol, decimal Rate, int Version, bool Stale);

public sealed record Priced(string Sku, decimal Price, string ServedBy);

public sealed record CatalogItem(string Sku, string Name, string ServedBy);

public sealed record Recommendation(string Variant);

public sealed record Payment(string PaymentId, string ServedBy);

public sealed record ChaosSettings
{
    public bool Enabled { get; init; }
    public double Rate { get; init; }
}

public sealed record RetrySettings
{
    public int Attempts { get; init; } = 1;
}

/// <summary>Kaynak üreticili JSON (Redis önbellek deposu yansımasız serileştirir; Native AOT ile uyumlu).</summary>
[JsonSerializable(typeof(Product))]
[JsonSerializable(typeof(FxRate))]
[JsonSerializable(typeof(Priced))]
[JsonSerializable(typeof(CatalogItem))]
[JsonSerializable(typeof(Recommendation))]
[JsonSerializable(typeof(Payment))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
public sealed partial class ShopJsonContext : JsonSerializerContext;
