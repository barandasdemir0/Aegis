namespace Shop.Api;

/// <summary>Bir bağımlılık örneğinin adresleri (REST ve gRPC).</summary>
public sealed record BackendEndpoint(Uri Http, Uri Grpc);

/// <summary>
/// Uygulamanın dış bağımlılıkları. <see cref="KeyPrefix"/> paylaşılan durumu (Redis anahtarları) bu kuruluma ayırır: aynı önekle
/// başlatılan iki örnek tek bir kümedeki iki pod gibi davranır (ortak devre, ortak kota).
/// </summary>
public sealed record ShopSettings(
    BackendEndpoint Eu,
    BackendEndpoint Us,
    Uri Canary,
    string Redis,
    string Sql,
    string KeyPrefix);
