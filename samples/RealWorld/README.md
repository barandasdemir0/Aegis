# RealWorld — Aegis'in gerçek bir projede doğrulanması

> Paket paket, özellik → kod yeri → test haritası: **[docs/FEATURE-MAP.md](docs/FEATURE-MAP.md)**.

Bir e-ticaret sipariş sistemi. Aegis'i **kaynak kod olarak değil, gerçek 2.0.0 NuGet paketleri olarak** tüketir (yerel akış:
`../../artifacts/packages`). Bağımlılıklar gerçektir: Redis, SQL Server, gerçek Kestrel üzerinde HTTP/1.1 ve HTTP/2 (gRPC), gerçek ağ.

| Proje | Ne |
|---|---|
| `Shop.Contracts` | `inventory.proto` → Grpc.Tools ile üretilen gRPC kodu (unary, sunucu/istemci/çift yönlü akış) |
| `Shop.Backends` | Sahte bağımlılıklar (ödeme, fiyat, katalog, öneri, kur, stok gRPC). Testler rota başına hata senaryosu kurar: durum kodu, gecikme, `Retry-After`, gRPC durumu, pushback |
| `Shop.Api` | Uygulama. Tüm dayanıklılık kurulumu tek dosyada: `ShopResilience.cs` |
| `Shop.LegacyWeb` | Klasik ASP.NET Web API 2, **.NET Framework 4.8**, OWIN self-host: `Aegis.Resilience.WebApi` + .NET Framework'te Aegis çekirdeği ve Redis |
| `Shop.Tests` | 79 uçtan uca test. Her sınıf taze ortam alır: 3 arka uç + Shop.Api (+ dağıtık senaryolarda ikinci pod; Web API 2 için iki .NET Framework süreci) |

**Kapsam:** kütüphanenin genel API'sindeki 75 kurulum/çalıştırma genişletme metodunun **75'i** bu projede gerçek bir senaryoda
kullanılır ve testle doğrulanır (denetim: `PublicAPI.*.txt` içindeki her `Add/Map/Use/With/Execute...` metodu kodda aranır).

## Çalıştırma

```bash
# 1) Paketler (repo kökünde)
dotnet pack Aegis.slnx -c Release -o artifacts/packages -p:Version=2.0.0
# 2) Bağımlılıklar
docker run -d --name aegis-redis-test -p 6389:6379 redis:7-alpine
docker run -d --name aegis-sql -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=Aegis!Test2026" -p 14330:1433 mcr.microsoft.com/mssql/server:2022-latest
# 3) Testler (farklı adres için: SHOP_REDIS, SHOP_SQL)
dotnet test samples/RealWorld/Shop.Tests -c Release
```

## Özellik → test

| Özellik | Gerçek senaryo | Test |
|---|---|---|
| Standart HTTP işleyicisi: retry | Ödeme 503 → aynı `Idempotency-Key` ve gövdeyle yeniden | `OrdersTests.Payment_TransientFailures_RetriedWithSameIdempotencyKey` |
| `Retry-After` | Ödeme 429 + `Retry-After: 1` → 1 sn beklenir | `Payment_RetryAfterHeader_IsHonored` |
| Idempotency koruması | Anahtarsız POST asla yeniden gönderilmez | `Payment_WithoutIdempotencyKey_IsNeverResent` |
| Deneme zaman aşımı | Takılan ödeme 1 sn'de kesilir, yeniden denenir | `Payment_HangingAttempt_CutByAttemptTimeout_ThenRetried` |
| Devre kesici + `StateProvider` | Açılır, istek iletilmez, Retry-After; HalfOpen → kapanır | `Payment_CircuitOpens_RejectsFast_ThenRecoversThroughHalfOpen` |
| `CircuitBreakerManualControl` | Bakım modu ödemeyi izole eder | `Maintenance_IsolatesPayments_ThenRestores` |
| SQL geçici hata (`Aegis.Resilience.Data.SqlClient`) | **Gerçek 1205 kilitlenmesi**: kurban yeniden denenir, çift yazma yok | `Sql_RealDeadlock_VictimIsRetriedTransparently` |
| gRPC unary retry + `grpc-previous-rpc-attempts` | Stok servisi Unavailable ×2 | `Inventory_Unavailable_RetriedAtGrpcLayer`, `InventoryGrpcTests.*` |
| gRPC pushback | Sunucu "400 ms sonra dene" der | `Reserve_ResourceExhaustedWithPushback_WaitsThenRetries` |
| gRPC commit kuralı | İlk mesajdan önce hata → retry; sonra → retry yok | `Watch_FailsBeforeFirstMessage_Retried`, `Watch_FailsAfterFirstMessage_NotRetried` |
| gRPC istemci / çift yönlü akış retry | Tampon baştan oynatılır | `BulkReserve_ClientStream_ReplayedOnRetry`, `Sync_DuplexStream_ReplayedBeforeFirstReply` |
| gRPC uç nokta ayıklama | Bozuk sunucu havuzdan çıkarılır | `Pool_OutlierDetection_EjectsFailingServer` |
| gRPC sunucu koruması | Eşzamanlılık dolu → ResourceExhausted | `Server_ConcurrencyLimit_RejectsWithResourceExhausted_WithoutPushback` |
| Request Collapser | 20 eşzamanlı istek → 1 arka uç çağrısı | `Products_ConcurrentRequests_CollapsedIntoSingleBackendCall` |
| Dağıtık önbellek (Redis, `Extensions.Caching`) | İkinci pod da aynı önbelleği görür | `Products_CachedInRedis_SharedAcrossPods` |
| Fallback | Arka uç çöktü → yedek değer | `Products_BackendDown_FallbackValueAfterRetries` |
| Stale-While-Revalidate + tipli boru hattı | Bayat değer anında, arkada yenileme, çöküşte bayat | `Fx_StaleWhileRevalidate_*` |
| Standart hedging + yönlendirme grupları | Yavaş/çöken AB → ABD | `Pricing_SlowPrimary_*`, `Pricing_PrimaryFailing_*` |
| Çoklu uç nokta hedging | Yavaş veri merkezi beklenmez | `Catalog_MultiEndpointHedging_FastDatacenterWins` |
| Çekirdek hedging + `ActionGenerator` | Yedek deneme başka bölgede | `Quote_CoreHedging_ActionGeneratorUsesOtherRegion` |
| Ağırlıklı kanarya + yapışkan oturum | ~%10 kanarya, aynı kullanıcı aynı sürüm | `Recommend_WeightedCanary_*` |
| Kayan pencere / .NET `RateLimiting` köprüsü + senkron `Execute` | 429 + Retry-After | `Search_RateLimited_429WithRetryAfter` |
| AOP `[AegisPolicy]` (async + senkron) | Eski sistemde kod değişmeden retry | `Legacy_AopProxyAsync_*` |
| Kötümser zaman aşımı | Token'sız senkron çağrı terk edilir | `Legacy_AopProxySync_PessimisticTimeoutAbandonsHangingCall` |
| Adaptif eşzamanlılık | Bağımlılık yavaşlayınca limit daralır, fazla yük reddedilir | `Adaptive_DependencySlowsDown_LimitShrinks` |
| Kaos + `OptionsProvider` | Yapılandırmadan canlı aç/kapat | `Chaos_ToggledLiveFromConfiguration` |
| `AddAegisPipelineWithContext` + `EnableReloads` | Retry sayısı canlı değişir | `Reloadable_RetryCountChangesLive` |
| Anahtarlı boru hatları + `ExecuteOutcomeAsync` | Kiracı başına ayrı devre | `Tenants_PerTenantCircuit_IsolatesFailures` |
| Gelen istek boru hattı (`RequireAegisPipeline`) | Rapor 300 ms'de kesilir, aynı anda tek | `Reports_InboundPipeline_TimeoutAndConcurrency` |
| Gelen istek kotası (Redis) | İstemci başına, tüm podlarda ortak | `Inbound_RateLimitPerClient_SharedAcrossPods` |
| Aspire `AddAegisServiceDefaults` | Düz HttpClient de korunur | `AspireServiceDefaults_PlainHttpClientIsResilient` |
| Kiracı başına kota | free 2/dk, vip 20/dk | `PartnersTests.PartitionedQuota_PerTenant` |
| Dağıtık devre kesici (Redis) | Pod 1'de açılan devre pod 2'de de açık | `DistributedCircuit_OpenedOnOnePod_RejectsOnTheOther` |
| Dağıtık hız sınırı (Redis) | İki pod birlikte en fazla 10 | `PartnersGlobalQuotaTests.DistributedQuota_SharedAcrossPods` |
| Pano + sağlık kontrolü | İzole → yedek değer + Degraded; sıfırla → Healthy; CSRF | `ObservabilityTests.Dashboard_*` |
| Metrikler (Polly etiketleri) + `AddAegisResilienceEnricher` | `OnRetry`, `operation.key`, `request.name` | `Metrics_RetryEvent_WithPollyCompatibleTagsAndRequestMetadata` |
| İz (trace) | `Aegis products` span'ı, HTTP span'ı çocuğu | `Tracing_PipelineSpan_WithOperationKey_AndChildHttpSpan` |
| `Aegis.Resilience.Testing` | DI ile kurulan boru hatlarının yapılandırması | `PipelineDescriptors_MatchIntendedConfiguration` |
| `SelectPipelineByAuthority` | Tek istemci, iki kargo firması: firma başına devre | `AdvancedTests.Shipping_CircuitPerCarrierAuthority` |
| `DisableRetryFor` + senkron `HttpClient.Send` | DELETE asla yeniden denenmez; senkron GET denenir | `Shipping_DeleteNeverRetried_SyncGetRetried` |
| `ReturnFinalResponse` | Denemeler tükenince son 503 yanıt olarak | `ShippingQuote_ReturnFinalResponse_GivesLast503` |
| `AddAegisDynamicHandler` / `AddAegisHandlerByHost` | Öncelik başlığına / host'a göre boru hattı | `Priority_DynamicHandler_*`, `ByHost_PipelineNamedAfterHost` |
| `AddHttpRequestReplayHandler` | Ekibin kendi retry işleyicisi geri sarılamayan gövdeyi yeniden gönderir | `Upload_ReplayHandler_ResendsNonSeekableBody` |
| `AddStandardResilience` (5'i 1 arada) | Retry + takılan denemenin kesilmesi | `InventorySync_StandardResilienceFiveInOne` |
| Bellek içi önbellek | Süre içinde arka uca gidilmez | `RecommendCache_InMemoryTtl` |
| Kuyruklu eşzamanlılık | 1 çalışan + 2 bekleyen, fazlası red | `Notifications_BoundedQueue` |
| Çekirdek token bucket (`AddRateLimiter`) | 2 izin sonra 429 | `Sms_CoreTokenBucket` |
| Devre gölge kipi | Açık karar verir, reddetmez | `SearchV2_ShadowCircuit_OpensButNeverRejects` |
| `SamplingCount` + `HalfOpenSuccessThreshold` + `BreakDurationGenerator` | Son 4 çağrı; yarı açıkta 2 başarı; 400 → 800 ms | `Warehouse_*` |
| Yavaş çağrı oranı | Hata yok ama yavaş → devre açılır | `Reporting_SlowCallRate_OpensCircuitWithoutErrors` |
| `RetryBudget` | Çöken bağımlılıkta retry fırtınası kesilir | `Budget_RetryThrottling_LimitsRetryStorm` |
| Ayrık kaos (`AddChaosLatency/Outcome/Behavior/Fault`) | Çağrı bazında açılır | `ChaosLab_LatencyOutcomeBehavior`, `ChaosFault_Injected` |
| `AddAegisPipeline<TOptions>` | Seçenek değişince zaman aşımı canlı değişir | `Fees_PipelineRebuiltWhenOptionsChange` |
| Anahtarlı boru hattı + durumlu (`TState`) çalıştırma | Kargo firması + sürüm | `Carrier_KeyedPipeline_StatefulExecution` |
| `Build<T>` + `WithTelemetry` | Tipli boru hattı, özel telemetri dinleyicisi | `Typed_PipelineBuildOfT`, `CustomTelemetryListener_ReceivesEvents` |
| `WithInstanceName` + `ConfigureAegisTelemetry` | `pipeline.instance` iz etiketi, otomatik günlük | `InstanceName_InTrace_AndAutomaticLogging` |
| AOP singleton / transient (`ValueTask`) | İki yaşam süresi de yeniden dener | `Proxies_SingletonAndTransient_Retry` |
| `AddAegisGrpcResilience` | Özel gRPC boru hattı: gRPC hedging | `Grpc_CustomPipeline_Hedging` |
| `AddFixedWindowRateLimiter` | .NET sabit pencere köprüsü | `Coupons_FixedWindow` |
| `AddPipeline` (kompozisyon) | Ortak boru hattı başka birinin içinde | `Checkout_ComposedPipeline` |
| `HandleTransientHttpErrors` | 503 denenir, 404 denenmez | `Coupons_TransientHttpErrorPredicate` |
| `SetAegisContext` / `GetAegisContext` / `GetOrCreateAegisContext` / `GetRequestMetadata` | Tüm denemeler aynı korelasyon kimliği | `Tracked_CallerContextFlowsThroughAllAttempts` |
| `MapAegisStatus` + ikinci `AddAegisCheck` | Hazır olma Unhealthy, canlılık Degraded | `OpsStatus_AndReadinessCheck` |
| `WithTimeProvider` | Uygulamanın politikası sahte saatle birim testinde | `SettlementPolicy_FakeClock_HalfOpenAfterAnHourWithoutWaiting` |
| `Aegis.Resilience.WebApi` (.NET Framework 4.8) | İki Web API 2 örneği arasında Redis ile ortak kota, beyaz liste | `LegacyWebTests.*` |
| .NET Framework'te çekirdek + HTTP işleyicisi | DI'sız `AegisResilienceHandler` ile retry | `NetFramework_OutgoingCall_Retried` |

## Bu projenin bulduğu kütüphane hataları (düzeltildi)

Kütüphanenin kendi testleri (o sırada 600 test) bunları yakalamamıştı; üçü de gerçek kullanımda ortaya çıktı:

1. **gRPC istemci/çift yönlü akış, protobuf ile hiç çalışmıyordu.** Yeniden oynatma tamponu mesaj boyutunu marshaller'ın basit
   `Serializer`'ıyla ölçüyordu; Grpc.Tools'un ürettiği kod yalnızca bağlamsal serileştiriciyi destekler (`NotImplementedException`).
   Kütüphane testleri elle yazılmış marshaller kullanıyordu. Düzeltme: `MessageSizer` (bağlamsal serileştirici, havuzlu karalama
   tamponu). Regresyon testi: `ClientStreaming_ContextualOnlyMarshaller_BuffersAndReplays`.
2. **Token'lı akış yazma (`WriteAsync(mesaj, ct)`) `NotSupportedException` fırlatıyordu.** Düzeltme: `ReplayingRequestWriter`
   aşırı yüklemeyi uygular. Regresyon testi: `ClientStreaming_WriteWithCancellationToken_Supported`.
3. **Farklı ayarlı iki `AddAegisCheck` birbirini eziyordu.** `configureOptions` global kaydediliyordu: hazır olma kontrolüne
   verilen "açık devre Unhealthy" ayarı canlılık kontrolünü de Unhealthy yapıyordu (Kubernetes pod'u yeniden başlatır). Artık
   ayar kontrolün adına bağlıdır. Regresyon testi: `HealthCheckRegistrationTests` (düzeltmesiz kodda başarısız olduğu doğrulandı).

Belge düzeltmesi: gRPC sunucusunun eşzamanlılık reddi pushback **taşımaz** (bekleme süresi bilinmez; istemci hemen yeniden
denemez). Belge önceden "pushback ile" diyordu.

## Kullanım notları (gerçek tüketici gözünden)

- Redis bağlantısı arka planda kurulur; bağlanana kadar pod yerel duruma düşer ve `/health` **Degraded** döner. Pod'u
  trafiğe `Healthy` olunca alın (Kubernetes readiness probe). Testlerdeki `WaitUntilReadyAsync` bunu yapar.
- Aspire `AddAegisServiceDefaults` tüm HttpClient'lara standart işleyici ekler. Kendi işleyicisi ya da kodda boru hattı olan
  istemcilerde önce `RemoveAllAegisHandlers()` çağırın (çift yeniden deneme olmasın).
- `aegis.strategy.events` bir `Counter<int>`'tir (Polly ile aynı): `MeterListener`'da `<int>` ile dinleyin.
