# Özellik Haritası — her Aegis özelliği nerede kuruldu, nerede denendi

Bu belge `samples/RealWorld` projesinin denetim kaydıdır: Aegis'in 17 paketindeki her özellik için **kodun nerede yazıldığı**,
**hangi gerçek senaryoda kullanıldığı** ve **hangi testin doğruladığı**.

- **Tüketim biçimi:** Aegis kaynak kod olarak değil, `dotnet pack` ile üretilen 2.0.0 NuGet paketleri olarak kullanılır.
- **Ortam:** gerçek Redis, gerçek SQL Server, gerçek Kestrel (HTTP/1.1 + HTTP/2), .NET 10 ve .NET Framework 4.8 süreçleri.
- **Kapsam ölçümü:** genel API'deki 75 kurulum/çalıştırma genişletme metodunun **75'i** bu projede kullanılır. Ölçüm yöntemi: her
  paketin `PublicAPI.*.txt` dosyasındaki `Add/Map/Use/With/Execute/Get/Set/...` metotları RealWorld kodunda aranır.
- **Son doğrulama:** 79/79 test geçti, art arda 3 koşuda kararlı (bkz. [Doğrulama](#doğrulama)).

Kod yerleri `Shop.Api/` altındaki dosyalardır:
- `ShopResilience.cs`: ana dayanıklılık kurulumu.
- `ShopEndpoints.cs`: iş akışları.
- `AdvancedFeatures.cs`: ileri seçenekler.
- `CoverageFeatures.cs`: genel API'nin kalanı.
- `OrderStore.cs`: SQL.
- `LegacyGateway.cs`: AOP.

Testler `Shop.Tests/` altındadır.

## Aegis.Resilience.Core — stratejiler

| Özellik | Gerçek senaryo | Kod | Test |
|---|---|---|---|
| Retry (üstel, jitter, DecorrelatedJitter) | Sipariş yazma, ürün okuma, eski sistem | `ShopResilience.AddPipelines` ("orders-db", "products", "legacy") | `OrdersTests.Sql_RealDeadlock_*`, `CatalogTests.Products_BackendDown_*` |
| Retry bütçesi (`RetryBudget`) | Çöken bağımlılıkta retry fırtınası | `AdvancedFeatures` ("budget") | `AdvancedTests.Budget_RetryThrottling_LimitsRetryStorm` |
| Circuit Breaker (oran, art arda hata) | Ürün servisi, kiracı başına devre | "products", `AddAegisPipelines<string>` | `ObservabilityTests.Dashboard_*`, `PlatformTests.Tenants_*` |
| Gölge kip, sayı penceresi, yarı açık eşiği, yavaş çağrı, süre üreticisi | Yeni arama, depo, raporlama | `AdvancedFeatures` ("search-shadow", "warehouse", "slow-calls") | `AdvancedTests.SearchV2_*`, `Warehouse_*`, `Reporting_*` |
| `CircuitBreakerManualControl` / `StateProvider` | Ödeme bakım modu, durum ekranı | `ShopResilience.AddHttpClients` ("payment") | `OrdersTests.Maintenance_*`, `Payment_CircuitOpens_*` |
| Timeout (iyimser, kötümser, `TimeoutGenerator`) | Ürün, eski sistem (token'sız senkron kod) | "products", "legacy" | `PlatformTests.Legacy_AopProxySync_PessimisticTimeout*` |
| Concurrency Limiter (kuyruksuz / kuyruklu) | Rapor (gelen istek), bildirim | "reports", "notifications" | `PlatformTests.Reports_*`, `AdvancedTests.Notifications_BoundedQueue` |
| Token bucket / kayan pencere / kiracı başına kota | SMS, arama, iş ortağı | "sms", "search", "partners" | `AdvancedTests.Sms_*`, `CatalogTests.Search_*`, `PartnersTests.PartitionedQuota_*` |
| Adaptive Concurrency | Kapasitesi bilinmeyen bağımlılık | "adaptive" | `PlatformTests.Adaptive_DependencySlowsDown_LimitShrinks` |
| Hedging (+ `ActionGenerator`) | Teklif: AB yavaşsa ABD | "quote" | `CatalogTests.Quote_CoreHedging_*` |
| Request Collapser | Popüler ürüne 20 eşzamanlı istek | "products" | `CatalogTests.Products_ConcurrentRequests_*` |
| Fallback | Ürün servisi çökünce yedek değer | "products" | `CatalogTests.Products_BackendDown_FallbackValueAfterRetries` |
| Stale-While-Revalidate | Döviz kuru | "fx" | `CatalogTests.Fx_StaleWhileRevalidate_*` |
| Cache (bellek içi + dış depo) | Öneri (bellek), ürün (Redis) | "recommend-cache", "products" | `AdvancedTests.RecommendCache_*`, `CatalogTests.Products_CachedInRedis_*` |
| Chaos (`AddChaos` + ayrık Fault/Latency/Outcome/Behavior, `OptionsProvider`) | Canlı kill switch, kaos laboratuvarı | "chaos", "chaos-lab" | `PlatformTests.Chaos_*`, `AdvancedTests.ChaosLab_*`, `CoverageTests.ChaosFault_*` |
| `AddStandardResilience` (5'i 1 arada) | Stok içe aktarma | "inventory-sync" | `AdvancedTests.InventorySync_*` |
| Kompozisyon (`AddPipeline`) | Ödeme sayfası ortak politikayı kullanır | "checkout" | `CoverageTests.Checkout_ComposedPipeline` |
| Tipli boru hattı (`Build<T>`, `AsTyped<T>`) | Ürün, kur, teklif | `AdvancedFeatures`, `ShopEndpoints` | `AdvancedTests.Typed_*`, `CatalogTests.Fx_*` |
| Çalıştırma biçimleri (bağlamlı, token, `TState`, senkron, `ExecuteOutcomeAsync`) | Çeşitli uç noktalar | `ShopEndpoints`, `AdvancedFeatures` | `AdvancedTests.Budget_*`, `Carrier_*`, `CatalogTests.Search_*`, `PlatformTests.Tenants_*` |
| `WithTimeProvider` | Mutabakat politikası sahte saatle | `CoverageFeatures.SettlementPolicy` | `CoverageTests.SettlementPolicy_FakeClock_*` |
| `WithInstanceName`, `WithTelemetry` | Kiracı örneği, özel dinleyici | "tenant-api", "typed-products" | `AdvancedTests.InstanceName_*`, `CoverageTests.CustomTelemetryListener_*` |

## Aegis.Resilience.Extensions.DependencyInjection

| Özellik | Kod | Test |
|---|---|---|
| `AddAegis`, `AddAegisPipeline` (ad, `IServiceProvider`'lı) | `ShopResilience`, Aspire | tüm testler |
| `AddAegisPipelines<TKey>` (dinamik, kiracı başına) | "tenant" boru hatları | `PlatformTests.Tenants_PerTenantCircuit_IsolatesFailures` |
| Statik anahtarlı `AddAegisPipeline(key, ...)` | `CarrierKey` | `AdvancedTests.Carrier_KeyedPipeline_*` |
| `AddAegisPipelineWithContext` + `EnableReloads` | "reloadable" | `PlatformTests.Reloadable_RetryCountChangesLive` |
| `AddAegisPipeline<TOptions>` (seçenek değişince yeniden kur) | "fees" | `AdvancedTests.Fees_PipelineRebuiltWhenOptionsChange` |
| AOP: `AddAegisProxiedScoped/Singleton/Transient`, `[AegisPolicy]` (Task, ValueTask, senkron) | `LegacyGateway.cs`, `CoverageFeatures.cs` | `PlatformTests.Legacy_*`, `CoverageTests.Proxies_*` |
| `ConfigureAegisTelemetry` (otomatik günlük, sonuç maskeleme) | `AdvancedFeatures` | `AdvancedTests.InstanceName_InTrace_AndAutomaticLogging` |

## Aegis.Resilience.Extensions.Http

| Özellik | Kod | Test |
|---|---|---|
| `AddStandardAegisHandler` (retry, devre, deneme/toplam süre, Idempotency-Key, Retry-After, gövde replay) | "payment" | `OrdersTests.Payment_*` |
| `SelectPipelineByAuthority`, `DisableRetryFor`, senkron `Send` | "shipping" | `AdvancedTests.Shipping_*` |
| `ReturnFinalResponse` | "shipping-quote" | `AdvancedTests.ShippingQuote_*` |
| `AddStandardAegisHedgingHandler` (yönlendirme grupları) | "pricing" | `CatalogTests.Pricing_*` |
| `AddMultiEndpointHedgingHandler` (+ idempotent olmayan istek koruması) | "catalog" | `CatalogTests.Catalog_MultiEndpointHedging_*` |
| `AddWeightedCanaryHandler` (yapışkan oturum) | "recommend" | `CatalogTests.Recommend_WeightedCanary_*` |
| `AddAegisResilienceHandler` (ad / satır içi) | "partners", "coupons-http" | `PartnersTests.*`, `CoverageTests.Coupons_TransientHttpErrorPredicate` |
| `AddAegisDynamicHandler`, `AddAegisHandlerByHost` | "priority", "by-host" | `AdvancedTests.Priority_*`, `ByHost_*` |
| `AddHttpRequestReplayHandler` | "upload" + ekibin retry işleyicisi | `AdvancedTests.Upload_ReplayHandler_*` |
| `RemoveAllAegisHandlers` | Aspire varsayılanı olan her özel istemci | dolaylı, tüm HTTP testleri |
| `SetAegisContext` / `GetAegisContext` / `GetOrCreateAegisContext` | "tracked" + `CorrelationHandler` | `CoverageTests.Tracked_*` |
| `HandleTransientHttpErrors`, `GetRequestMessage` | "coupons-http", "partners" | `CoverageTests.Coupons_*`, `PartnersTests.*` |

## Aegis.Resilience.Grpc ve Aegis.Resilience.Grpc.AspNetCore

| Özellik | Kod | Test |
|---|---|---|
| `AddStandardAegisGrpcResilience`: unary retry, `grpc-previous-rpc-attempts`, pushback, deneme süresi | "inventory" | `InventoryGrpcTests.Reserve_*`, `OrdersTests.Inventory_*` |
| Sunucu akışı commit kuralı | "inventory" | `InventoryGrpcTests.Watch_*` |
| İstemci / çift yönlü akış retry (tamponlu yeniden oynatma, protobuf marshaller) | "inventory" | `InventoryGrpcTests.BulkReserve_*`, `Sync_*` |
| `AddAegisGrpcResilience` (özel boru hattı: gRPC hedging) | "inventory-hedged" | `CoverageTests.Grpc_CustomPipeline_Hedging` |
| `AddAegisGrpcOutlierDetection` | "inventory-pool" (iki sunucu) | `InventoryGrpcTests.Pool_OutlierDetection_*` |
| Sunucu koruması (`AddAegisResilience`) | `Shop.Backends/BackendApp.cs` | `InventoryGrpcTests.Server_ConcurrencyLimit_*` |

## Dağıtık: Aegis.Resilience.Distributed.Redis ve Aegis.Resilience.Extensions.Caching

| Özellik | Kod | Test |
|---|---|---|
| `AddDistributedCircuitBreaker` + `AddAegisRedisStateStore` | "partners" | `PartnersTests.DistributedCircuit_OpenedOnOnePod_RejectsOnTheOther` |
| `AddDistributedRateLimiter` + `AddAegisRedisRateLimitStore` | "partners" | `PartnersGlobalQuotaTests.DistributedQuota_SharedAcrossPods` |
| `AddAegisDistributedCacheStore` (Redis, kaynak üreticili JSON) | "products" | `CatalogTests.Products_CachedInRedis_SharedAcrossPods` |
| Redis'e geç bağlanan pod (fail-open, `/health` Degraded) | `ShopFixture.WaitUntilReadyAsync` | tüm iki pod'lu testler |

## Sunucu tarafı: Aegis.Resilience.AspNetCore ve Aegis.Resilience.WebApi

| Özellik | Kod | Test |
|---|---|---|
| `AddAegisInboundRateLimiting` + `UseAegisInboundRateLimiting` (istemci başına, Redis ile ortak) | `ShopResilience`, `ShopApp` | `PlatformTests.Inbound_RateLimitPerClient_SharedAcrossPods` |
| `UseAegisInboundPipelines` + `RequireAegisPipeline` | "reports" | `PlatformTests.Reports_InboundPipeline_*` |
| `UseAegisRateLimiting` (Web API 2, .NET Framework 4.8, Redis ile ortak, beyaz liste) | `Shop.LegacyWeb/Program.cs` | `LegacyWebTests.WebApi2_*` |
| .NET Framework'te çekirdek + `AegisResilienceHandler` | `Shop.LegacyWeb/Program.cs` | `LegacyWebTests.NetFramework_OutgoingCall_Retried` |

## İşletme: HealthChecks, Dashboard, Telemetry, Aspire, Testing, Data.SqlClient, RateLimiting

| Özellik | Kod | Test |
|---|---|---|
| `AddAegisServiceDefaults` (Aspire: varsayılan HTTP işleyicisi, metrik, iz, sağlık) | `ShopApp` | `PlatformTests.AspireServiceDefaults_*` |
| `AddAegisCheck` (canlılık Degraded + hazır olma Unhealthy) | Aspire + `CoverageFeatures` | `CoverageTests.OpsStatus_AndReadinessCheck`, `ObservabilityTests.Dashboard_*` |
| `MapAegisDashboard`, `MapAegisStatus` (izole/sıfırla, CSRF) | `ShopApp`, `CoverageFeatures` | `ObservabilityTests.Dashboard_*` |
| Metrikler (Polly etiketleri), iz (span), `AddAegisResilienceEnricher`, `SetRequestMetadata` | `ShopResilience`, `ShopEndpoints` | `ObservabilityTests.Metrics_*`, `Tracing_*` |
| `Aegis.Resilience.Testing`: `GetPipelineDescriptor` | — | `ObservabilityTests.PipelineDescriptors_*` |
| `HandleSqlTransientErrors` (gerçek 1205 kilitlenmesi) | `OrderStore.cs`, "orders-db" | `OrdersTests.Sql_RealDeadlock_VictimIsRetriedTransparently` |
| `AddTokenBucketRateLimiter`, `AddFixedWindowRateLimiter` (.NET köprüsü) | "search-bcl", "coupons" | `CatalogTests.Search_*`, `CoverageTests.Coupons_FixedWindow` |

## Bu projenin bulduğu kütüphane hataları

Kütüphanenin kendi testleri bunları yakalamamıştı; hepsi düzeltildi ve regresyon testi eklendi:

| Hata | Etki | Düzeltme |
|---|---|---|
| gRPC akış tamponu basit `Serializer` ile ölçülüyordu | Protobuf (Grpc.Tools) ile istemci/çift yönlü akış hiç çalışmıyordu | `MessageSizer`: bağlamsal serileştirici |
| `WriteAsync(mesaj, ct)` uygulanmamıştı | Token'lı akış yazma `NotSupportedException` | `ReplayingRequestWriter` aşırı yüklemesi |
| `AddAegisCheck` ayarları globaldi | Hazır olma ayarı canlılığı da Unhealthy yapıyordu (gereksiz pod yeniden başlatma) | Ayar kontrol adına bağlı |

Belge düzeltmesi: gRPC sunucusunun eşzamanlılık reddi pushback taşımaz (bekleme süresi bilinmez).

## Doğrulama

```bash
# Bağımlılıklar ve paketler: README.md "Çalıştırma" bölümü
dotnet test samples/RealWorld/Shop.Tests -c Release
```

| Koşu | Sonuç |
|---|---|
| RealWorld | 79/79, art arda 3 koşu |
| Kütüphane `tests/Aegis.Tests` | 617 test × net8.0, net9.0, net10.0; Redis, SQL Server ve Toxiproxy senaryoları ilgili gerçek servisler sağlandığında çalışır |
| Kütüphane `tests/Aegis.CompatibilityTests` | 29/29 (.NET Framework 4.8) |
