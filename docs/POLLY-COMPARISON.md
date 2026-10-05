# 🎯 Aegis vs Polly vs Microsoft Resilience — Özellik Karşılaştırması ve Yol Haritası

Kaynak: Polly 8.8.0 ve Microsoft.Extensions.(Http.)Resilience 10.10.0. Bunlar NuGet API'sinden doğrulanmış son sürümlerdir
(1 Ekim 2026). Kaynaklar `PublicAPI.Shipped.txt` dosyaları, CHANGELOG'lar ve dotnet/extensions kaynak kodudur. Tablolar tahmin
değil, genel API yüzeyinden çıkarılmıştır. Son 10 sürümün incelemesi bölüm 11'dedir.

**Hedef:** Her alanda Polly ve Microsoft'u geçmek — performans (süre + bellek) **ve** özellik. Kural: optimizasyon uğruna
hiçbir özellik, davranış veya genel API kaldırılmaz.

Durum işaretleri: ✅ Aegis'te var / eşit veya üstün · 🟡 kısmen · ❌ yok (eklenecek) · ⭐ yalnızca Aegis'te var

## 1. Çalıştırma API'si

| Özellik | Polly | Aegis 2.0.0 (parantez içi: eklendiği sürüm) | Plan |
|---|---|---|---|
| Async çalıştırma (`ExecuteAsync`) | ✅ 11 biçim | ✅ 8 biçim (1.0.10): bağlam, token, TState, TState+token | — |
| Senkron `Execute` (Action / Func) | ✅ 12 biçim | ✅ 8 biçim (1.0.10) | — |
| `CancellationToken` alan biçimler | ✅ | ✅ (1.0.10) | — |
| Closure'suz `TState` biçimleri | ✅ | ✅ 0 B (1.0.10) | — |
| Fırlatmayan `ExecuteOutcomeAsync` | ✅ | ✅ (1.0.10) | — |
| Tipli boru hattı `ResiliencePipeline<T>` | ✅ | ✅ `IAegisPipeline<T>` ve `Build<T>()` (1.0.12) | — |
| Bağlam havuzu (`ResilienceContextPool`) | ✅ genel | ✅ `AegisContextPool` genel | — |
| Tipli özellik anahtarı `ResiliencePropertyKey<T>` | ✅ | ✅ `AegisPropertyKey<T>` (1.0.10) | — |
| `OperationKey` (işlem bazlı telemetri) | ✅ | ✅ bağlam + `operation.key` etiketi (1.0.11) | — |
| `ContinueOnCapturedContext` | ✅ | ✅ (1.0.11) | — |

## 2. Koşullar (predicate) ve olaylar

| Özellik | Polly | Aegis | Plan |
|---|---|---|---|
| Bağlam + deneme no + sonuç alan **async** predicate | ✅ `XPredicateArguments<T>` | ✅ `ShouldHandleOutcome`: Retry, CB, Fallback, Hedging (1.0.12) | — |
| Akıcı `PredicateBuilder` (`Handle<T>().HandleResult(...)`) | ✅ | ✅ ⭐ `AegisPredicateBuilder`: tipsiz pipeline'da kutulamasız, `HandleInner` (AggregateException dahil) | — |
| `OnRetry` (deneme, gecikme, süre, sonuç) | ✅ | ✅ + `ExecutionAttempt` olayı (1.0.11) | — |
| `OnTimeout`, `OnOpened/Closed/HalfOpened` | ✅ | ✅ | — |
| Devre olayında `HalfOpenAttempts`, `IsManual`, tetikleyen `Outcome` | ✅ (8.x) | ✅ yerel + dağıtık; elle geçiş de olay üretir (1.3.0) | — |
| Fırlatan olay geri çağrısı | ❌ çağrıya yayılır; fırlatan `OnHalfOpened` devreyi kilitler (batırma testi) | ✅ ⭐ (2.0.0'dan itibaren `OnRetry`/`OnTimeout` dahil) yutulur ve sayılır, devre çalışmaya devam eder | — |
| `OnFallback`, `OnHedging`, `OnRejected` (rate limit), `OnXInjected` (kaos) | ✅ | ✅ (1.0.11) | — |

## 3. Stratejiler

| Strateji / seçenek | Polly | Aegis | Plan |
|---|---|---|---|
| Retry: backoff türleri, `MaxDelay`, jitter, `DelayGenerator` | ✅ | ✅ | — |
| Retry: `Randomizer` (deterministik test) | ✅ | ✅ Retry + Chaos (1.0.10) | — |
| Retry: HTTP `Retry-After` | 🟡 (MS HTTP'de) | ✅ ⭐ çekirdekte | — |
| Circuit Breaker: kayan pencere, `BreakDurationGenerator` | ✅ | ✅ | — |
| Circuit Breaker: açılma süresi üreticisinde sağlık bilgisi (`HealthInfo`) | ✅ | ✅ `e.Health` yerel + dağıtık (1.1.0) | — |
| Circuit Breaker: yavaş çağrı oranı | ❌ | ✅ ⭐ | — |
| Circuit Breaker: `ManualControl` (kurulumdan önce, birden çok devre) | ✅ | ✅ yerel + dağıtık (1.0.12) | — |
| Circuit Breaker: `StateProvider` (kurulumdan önce) | ✅ | ✅ (1.0.12) | — |
| Circuit Breaker: dağıtık (Redis, pod'lar arası) | ❌ | ✅ ⭐ | — |
| Circuit Breaker: red istisnasında `RetryAfter`, izolasyona özel `IsolatedCircuitException` | ✅ (8.x) | ✅ (1.3.0) | — |
| Timeout: `TimeoutGenerator`, `OnTimeout` | ✅ | ✅ | — |
| Timeout: kötümser (token'a saygısız kod) | ❌ (v8'de kaldırıldı) | ✅ ⭐ | — |
| Fallback: sonuca göre (istisnasız) fallback, `OnFallback` | ✅ | ✅ `FallbackAction`, `ShouldHandleResult`, `ShouldHandleOutcome` (1.0.11) | — |
| Hedging: `ActionGenerator` (deneme başına farklı eylem) | ✅ | ✅ (1.0.12) | — |
| Hedging: `DelayGenerator`, sonuca göre hedging | ✅ | ✅ (1.0.11 / 1.0.12); atılan kötü sonuçlar dispose edilir | — |
| Hedging: istisnaya göre koşul (ele alınmayan istisna yedeklemeyi bitirir) | ✅ | ✅ `HedgingOptions.ShouldHandle` (1.3.0) | — |
| Rate limit: `System.Threading.RateLimiting` ile birlikte çalışma (her `RateLimiter`) | ✅ | ✅ `Aegis.Resilience.RateLimiting` (1.0.12) + kendi sınırlayıcıları | — |
| Rate limit: `RetryAfter` bilgisi reddinde | ✅ | ✅ token bucket + kayan pencere (1.0.11) | — |
| Rate limit: kayan pencere, kiracı bazlı | ✅ (RateLimiting ile) | ✅ | — |
| Kaos: fault / outcome / latency / behavior | ✅ ayrı stratejiler | ✅ ayrı (`AddChaosFault` ...) + birleşik `AddChaos` (1.0.12) | — |
| Kaos: bağlam bazlı `EnabledGenerator` / `InjectionRateGenerator` | ✅ | ✅ (1.0.11) | — |
| Kaos: ağırlıklı sonuç üretici (`OutcomeGenerator`) | ✅ | ✅ `ChaosOutcomeGenerator` (1.1.0) | — |
| Kaos: `LatencyGenerator`, null `FaultGenerator` = enjeksiyon yok, boş üretici = geçir | ✅ (8.6–8.8) | ✅ (1.3.0) | — |
| Zamanlayıcı sınırını aşan süre (`TimeSpan.MaxValue`) | ✅ kurulumda reddeder | ✅ ⭐ sonsuz sayılır (1.3.0; önceden çalışma anında fırlatıyordu, batırma testi buldu) | — |
| Request Collapser, Stale-While-Revalidate, Cache, Adaptive Concurrency | ❌ | ✅ ⭐ | — |

## 4. Zaman, test edilebilirlik, doğrulama

| Özellik | Polly | Aegis | Plan |
|---|---|---|---|
| `TimeProvider` (sahte saatle deterministik test) | ✅ tüm stratejilerde | ✅ 10 stratejide (1.0.10) | — |
| `Polly.Testing`: boru hattı tanımlayıcısı (`GetPipelineDescriptor`) | ✅ | ✅ `Aegis.Resilience.Testing` + `GetOptions<T>()` (1.0.12) | — |
| Seçenek doğrulama | ✅ DataAnnotations | ✅ ⭐ fail-fast + açık mesaj | — |

## 5. Kayıt defteri (registry) ve DI

| Özellik | Polly | Aegis | Plan |
|---|---|---|---|
| Adlandırılmış boru hatları + DI | ✅ | ✅ | — |
| Generic anahtar (`Registry<TKey>`), dinamik anahtarlı boru hatları (ör. kiracı başına) | ✅ | ✅ ⭐ + kardinalite sınırı (1.0.12) | — |
| Boru hattının tamamını yeniden yükleme (`EnableReloads`) | ✅ | ✅ uçuştaki çağrılar korunur, hatalı yeni ayar yüklenmez (1.0.12) | — |
| Kurulum bağlamı: `GetOptions`, `EnableReloads(IOptionsMonitor)`, `AddReloadToken`, `OnPipelineDisposed` | ✅ (8.8) | ✅ `AddAegisPipelineWithContext` (1.3.0); yeniden kurma kaynağı yoksa sarmalayıcısız | — |
| `await using` kayıt defteri, `ResiliencePipeline.Empty`, `Outcome` yardımcıları | ✅ | ✅ (1.3.0) | — |
| AOP `[AegisPolicy]` | ❌ | ✅ ⭐ | — |

## 6. Telemetri

| Özellik | Polly / MS | Aegis | Plan |
|---|---|---|---|
| Metrikler | ✅ `resilience.polly.*` | ✅ `aegis.*` | — |
| Standart etiketler (`pipeline.name`, `strategy.name`, `operation.key`, `event.name`, `event.severity`, `exception.type`, `attempt.number`) | ✅ | ✅ aynı adlar (1.0.11) | — |
| `ILogger` ile olay günlüğü (önem düzeyiyle) | ✅ | ✅ Polly ile aynı mesaj/EventId; DI'da otomatik (1.0.11) | — |
| Zenginleştiriciler (enricher), `TelemetryListener`, `SeverityProvider` | ✅ | ✅ (1.0.11) | — |
| Dinleyici yoksa sıfır maliyet | ✅ | ✅ (1.0.9) | — |
| İz (trace): boru hattı başına `Activity`, strateji olayları span olayı, iç içe span, `AddSource` / Aspire `AddTracing` | ❌ genel API'de yok (Polly 8.8.0 Core/Extensions/RateLimiting/Testing) | ✅ ⭐ `ActivitySource "Aegis"` (1.5.0); gerçek Collector'a gRPC + HTTP ile doğrulandı | — |
| Boru hattı örnek adı (`pipeline.instance`), `PipelineExecuting` olayı, reddin `TelemetrySource`'u | ✅ | ✅ (1.3.0) | — |
| Fırlatan telemetri dinleyicisi | ❌ çağrıya yayılır, tüm trafiği düşürür (batırma testi) | ✅ ⭐ yutulur ve sayılır | — |
| `error.type` (hata özetleyici) ve `request.name` / `request.dependency.name` etiketleri (MS `AddResilienceEnricher`) | ✅ | ✅ `Aegis.Resilience.Extensions.Telemetry` (1.1.0) | — |
| Health check, web panosu | ❌ | ✅ ⭐ | — |

## 7. HTTP (Microsoft.Extensions.Http.Resilience ile)

| Özellik | MS | Aegis | Plan |
|---|---|---|---|
| Standart handler (rate limit + toplam timeout + retry + CB + deneme timeout) | ✅ | ✅ aynı zincir ve varsayılanlar (1.0.12) | — |
| Tutarlılık doğrulaması (deneme timeout < toplam; CB örnekleme ≥ 2× deneme) | ✅ | ✅ fail-fast (1.0.12) | — |
| `DisableForUnsafeHttpMethods` / `DisableFor(...)` | ✅ | ✅ `DisableRetryForUnsafeHttpMethods()` / `DisableRetryFor(...)` (1.2.0) + ⭐ varsayılan Idempotency-Key farkındalığı | — |
| Özel boru hattında DI bağlamı (`ResilienceHandlerContext`: `ServiceProvider`, `GetOptions`, `EnableReloads`, `OnPipelineDisposed`) | ✅ | ✅ `AddAegisResilienceHandler((pipeline, context) => ...)` (1.2.0); boru hattı sağlayıcıyla dispose edilir | — |
| `RemoveAllResilienceHandlers()` | ✅ (9.0+) | ✅ `RemoveAllAegisHandlers()` (1.2.0) | — |
| Denemeler tükenince son yanıtı döndürme | ✅ (varsayılan) | ✅ `ReturnFinalResponse` (1.2.0; Aegis varsayılanı istisna, belgelenmiş) | — |
| Host bazlı boru hattı (`SelectPipelineByAuthority`) | ✅ | ✅ standart handler'da `SelectPipelineByAuthority()` / `SelectPipelineBy(...)` + kardinalite sınırı (1.1.0); `AddAegisHandlerByHost` | — |
| Standart hedging + yönlendirme grupları (sıralı / ağırlıklı) + **uç nokta başına devre kesici** | ✅ | ✅ (1.0.12) | — |
| `IConfiguration`'dan bağlama ve değişince yeniden kurma | ✅ | ✅ standart (1.0.12) + standart hedging (1.1.0); uçuştaki istekler korunur | — |
| Gövde tekrar oynatma, Retry-After, sticky kanarya | 🟡 | ✅ ⭐ | — |
| Geçici hata tanımı (`IsTransient`: tüm 5xx + 408 + 429, ağ hatası, bağlantı zaman aşımı) ve koşul oluşturucu | ✅ | ✅ `AegisHttpTransientErrors`, `HandleTransientHttpErrors()` (1.3.0) | — |
| `ShouldRetryAfterHeader`; saçma `Retry-After` (2147483647 sn, 9999 yılı) | 🟡 seçenek var; saçma değer isteği `ArgumentOutOfRangeException` ile düşürür (batırma testi) | ✅ ⭐ seçenek + `MaxDelay` ile sınırlı (1.3.0) | — |
| Senkron `HttpClient.Send` | ✅ | ✅ tüm işleyicilerde (1.3.0; önceden atlanıyordu) | — |
| Standart işleyicide `Configure((o, sp) => ...)`, özel işleyicide `SelectPipelineBy` + `InstanceName`, `GetRequestMessage()` | ✅ | ✅ (1.3.0) | — |

## 8. Performans (1.2.0, BenchmarkDotNet, oran = Aegis/Polly; ayrıntı `docs/BENCHMARKS.md`)

Tek iş parçacığı: Eşzamanlılık 0,31× · Hedging 0,36× · CB 0,41× · Boş 0,50× · Retry 0,51× · TState/Token/Senkron/Outcome 0,65–0,75× · Standart 0,66×
· Retry-1hata 0,86× · Timeout 0,86× · Açık devre 0,96×. 64 iş parçacığı altında: Retry 0,08× · Eşzamanlılık 0,14× · Standart async 0,49× · CB 0,49× · Standart senkron 0,59×.
19 senaryonun 19'unda önde. Çekirdek boru hattında bellekte Polly'den fazla değil; 16 senaryoda 0 B. **Düzeltme (2.0.0):** HTTP
standart işleyicisi 1.5.0'a kadar Microsoft'tan 1,72× fazla bellek ayırıyordu (bağımsız doğrulamada bulundu); 2.0.0'da eşit.
Hedef: yeni eklenen her API ve strateji için de karşılaştırmalı benchmark; hiçbir alanda Polly'nin gerisinde kalmamak.

## 9. Platform ve paket kalitesi

| Özellik | Polly / MS | Aegis | Plan |
|---|---|---|---|
| .NET Framework 4.6.2+ ve netstandard2.0 | ✅ | ✅ aynı kaynak, dahili polyfill'ler; gerçek .NET Framework 4.8'de uçtan uca test (1.1.0) | — |
| Native AOT / trimming uyumu | ✅ | ✅ `IsAotCompatible`, sıfır uyarı; çalışma zamanısız imajda duman testi (1.1.0) | — |
| Strong naming | ✅ | ✅ (1.1.0) | — |
| Genel API sözleşmesi (`PublicApiAnalyzers`) | ✅ | ✅ 10 paket (1.1.0) | — |
| Çekirdek paketin dış bağımlılığı | Polly.Core: yok (.NET 8+) | ✅ yok (.NET 8+) | — |

## 10. NuGet'teki ilk 20 resilience paketi (1.2.0)

**Yöntem:**
- NuGet arama API'sinde 15 anahtar kelimeyle arama yapıldı: resilience, retry, circuit breaker, fault tolerance,
  transient fault, rate limit, bulkhead, hedging, chaos, fallback, polly, backoff, throttling ve diğerleri.
- 518 aday toplam indirme sayısına göre sıralandı (30 Eylül 2026).
- **Elenenler:**
  - resilience dışı paketler: AWS'nin metin-okuma servisi "Amazon Polly", Refit, Umbraco "BackOffice"
  - test-yeniden-deneme eklentileri: xRetry vb.
  - alana özgü çatılar: Brighter, KafkaFlow, Ocelot adaptörü
  - imzalı kopyalar: Polly-Signed
- Her paketin yeteneği Aegis'in genel API'siyle karşılaştırıldı.
- Performans: ilgili kategoride BenchmarkDotNet, `EcosystemBenchmarks`.

| # | Paket | İndirme | Yetenek | Aegis |
|---|---|---:|---|---|
| 1 | Polly | 1,68 mr | v7 politikaları; art arda N hatada açılan devre | ✅ `ConsecutiveFailureThreshold` (1.2.0) |
| 2 | Polly.Core | 675 mn | v8 | ✅ (bölüm 1–8) |
| 3 | Microsoft.Extensions.Http.Polly | 611 mn | HttpClient + politika, istek başına seçim | ✅ `AddAegisResilienceHandler`, `AddAegisDynamicHandler` |
| 4 | Polly.Extensions.Http | 530 mn | geçici HTTP hataları | ✅ (+429, Retry-After) |
| 5 | System.Threading.RateLimiting | 223 mn | sınırlayıcılar | ✅ köprü + kendi sınırlayıcıları |
| 6 | Polly.Extensions | 175 mn | DI + telemetri | ✅ |
| 7 | Polly.Contrib.WaitAndRetry | 174 mn | decorrelated jitter | ✅ `DecorrelatedJitter` |
| 8 | Microsoft.Extensions.Resilience | 161 mn | zenginleştirici | ✅ `Extensions.Telemetry` |
| 9 | Microsoft.Extensions.Http.Resilience | 158 mn | standart / hedging handler | ✅ (bölüm 7) |
| 10 | Polly.RateLimiting | 153 mn | köprü | ✅ `Aegis.Resilience.RateLimiting` |
| 11 | AspNetCoreRateLimit | 46 mn | sunucu tarafı IP/istemci kuralları, kota başlıkları | ✅ `Aegis.Resilience.AspNetCore` (1.2.0) |
| 12 | Polly.Caching.Memory | 28 mn | kayan / sonuca göre TTL | ✅ `SlidingExpiration`, `TtlGenerator` (1.2.0) |
| 13 | EnterpriseLibrary.TransientFaultHandling | 22 mn | tanıyıcılı retry | ✅ koşul oluşturucu + `Data.SqlClient` |
| 14 | EnterpriseLibrary.TransientFaultHandling.Data | 17 mn | SQL geçici hata tanıma | ✅ `Aegis.Resilience.Data.SqlClient` (1.2.0; güncel liste + iç istisna taraması) |
| 15 | EnterpriseLibrary.TransientFaultHandling.WindowsAzure.Storage | 10 mn | eski Azure Storage SDK hataları | ➖ güncel Azure SDK'ları kendi retry'ına sahip; gerekirse koşul oluşturucuyla |
| 16 | AspNetCoreRateLimit.Redis | 9 mn | küme genelinde kota | ✅ `RedisRateLimitStore` (1.2.0) |
| 17 | EnterpriseLibrary.TransientFaultHandling.Configuration | 9 mn | yapılandırmadan retry | ✅ `IConfiguration` bağlama + yeniden yükleme |
| 18 | TransientFaultHandling.Core | 8 mn | Topaz'ın .NET Core portu | ✅ (13–14 ile aynı) |
| 19 | Polly.Caching.Distributed | 5,5 mn | `IDistributedCache` | ✅ `Aegis.Resilience.Extensions.Caching` (1.2.0) |
| 20 | WebApiThrottle | 5,3 mn | ASP.NET Web API 2 (.NET Framework) sunucu kısıtlaması | ✅ `Aegis.Resilience.WebApi` (1.4.0): aynı kural/beyaz liste/başlık modeli, Redis ile küme geneli kota |

**Performans (Aegis / rakip, aynı koşu):**

| Kategori | Süre | Bellek |
|---|---|---|
| Sunucu tarafı hız sınırı (AspNetCoreRateLimit) | 0,22× | 0,34× |
| Cache isabeti (Polly.Caching.Memory) | 0,70× | 0 B (384 B'ye karşı) |
| Token bucket (Polly.RateLimiting) | 0,73× | eşit (0 B) |
| Dağıtık sınırlayıcı, bellek içi depo (Polly'nin yerel kovasına karşı) | 0,80× | 0 B |
| Art arda hata devresi (Polly v7) | eşit: 0,94–1,08× (dört koşu) | 0 B (496 B'ye karşı) |

**Dürüst notlar:**
- Art arda hata devresinde hız eşit, bellekte Aegis önde. Aegis aynı çağrıda kayan pencere oranını, yavaş çağrı
  oranını ve art arda sayacı birlikte tutar; Polly v7'nin devresi yalnızca sayacı tutar.
- WebApiThrottle (20.) için `Aegis.Resilience.WebApi` (1.4.0) eklendi: Web API 2 `DelegatingHandler`'ı, gerçek .NET Framework 4.8
  üzerinde bellek içi `HttpServer` ile test edilir (`WebApiRateLimitingTests`).
- EnterpriseLibrary Azure Storage tanıyıcısı (15.) artık bakımı yapılmayan eski Azure Storage SDK'sı içindir. Güncel
  Azure SDK'ları (`Azure.Core`) kendi retry'ına sahiptir. Gerekirse Aegis koşul oluşturucusuyla tanımlanır.
- Gerçek SQL Server kilitlenme testi (`RealSqlServerTransientTests`) hazırdır. Microsoft SQL Server lisans
  sözleşmesinin (`ACCEPT_EULA`) kullanıcı tarafından kabul edilmesini gerektirdiği için otomatik koşuda yoktur;
  komutlar `docs/TEST-INFRASTRUCTURE.md`'de. SQL tanıyıcı gerçek `SqlException` nesneleriyle 12 testle doğrulanmıştır.

## 11. Rakiplerin son 10 sürümü (1.3.0 incelemesi)

Sürümler NuGet API'sinden doğrulandı (1 Ekim 2026): Polly ailesi en son **8.8.0**, Microsoft.Extensions.Resilience ve
Http.Resilience en son **10.10.0** (GitHub'daki v10.10.1 etiketi resilience paketleri için yayımlanmadı).

| Kaynak | İncelenen | Aegis'e eklenen |
|---|---|---|
| Polly 8.5.2 → 8.8.0 genel API farkı (`PublicAPI.*.txt`) | `HedgingPredicateArguments.AttemptNumber`, `EnableReloads(IOptionsMonitor)` | Hedging koşulunda gerçek deneme numarası testle doğrulandı; `AegisPipelineContext.EnableReloads(monitor)` |
| Polly 8.x CHANGELOG davranış düzeltmeleri | null `FaultGenerator`, hedging/timeout'ta çağıran token'ı, `TimeoutRejectedException.Timeout`, olay önem düzeyi geçersiz kılma | Kaos davranışları hizalandı; token yayılımı batırma testiyle doğrulandı (iki kütüphane de geçer) |
| Polly 8.x genel API (önceden eksik kalanlar) | `RetryAfter`, `IsolatedCircuitException`, `HalfOpenAttempts`, `IsManual`, olaydaki `Outcome`, `LatencyGenerator`, `InstanceName`, `PipelineExecuting`, `TelemetrySource`, `Outcome` yardımcıları, `ResiliencePipeline.Empty`, `IAsyncDisposable` kayıt defteri, kiralama meta verisi, kurulum bağlamı | Hepsi (1.3.0) |
| Microsoft 10.1 → 10.10 | Yeni özellik yok (yalnızca bağımlılık güncellemeleri) | — |
| Microsoft tam genel API ve kaynak (`HttpClientResiliencePredicates`) | `IsTransient`, bağlantı zaman aşımı, `ShouldRetryAfterHeader`, `Configure((o, sp))`, `SelectPipelineBy` + `InstanceName`, `GetRequestMessage`, senkron `Send` | Hepsi (1.3.0) |

**Bilinçli olarak eklenmeyen:** Polly `PredicateResult.True()/False()`. Aegis'te senkron koşul doğrudan yazılır
(`AegisPredicate.Create(args => true)`), bu yüzden `ValueTask<bool>` yardımcısına gerek yok (YAGNI).

## 12. Batırma (torture) testleri — `tests/Aegis.TortureTests`

Aynı 15 acımasız senaryo, her kütüphaneye kendi yerel API'siyle uygulanır. Farklı tohumlarla defalarca koşulur.
Kilitlenme, beklenmeyen istisna ve gözlenmeyen görev istisnası da "kaldı" sayılır. Sonuçlar (10 tur, tohum 777):

| Senaryo | Aegis | Polly 8.8.0 | MS Http.Resilience 10.10.0 |
|---|---|---|---|
| Thread fırtınası + rastgele iptal (çapraz karışma, sızan iptal) | ✅ 10/10 | ✅ 10/10 | — |
| Eşzamanlılık sınırı: izin sızıntısı / fazla kabul | ✅ 10/10 | ✅ 10/10 | — |
| Tek iş parçacıklı bağlamda sync-over-async (kilitlenme) | ✅ 10/10 | ✅ 10/10 | — |
| Çağıran iptali: token yayılımı, yetim deneme | ✅ 10/10 | ✅ 10/10 | — |
| Uç seçenek değerleri (taşma) | ✅ 10/10 | ✅ 10/10 | — |
| Fırlatan geri çağrılar (boru hattı zehirlenmesi) | ✅ 10/10 | ❌ 0/10 | — |
| Çağrı sürerken dispose yarışı | ✅ 10/10 | ✅ 10/10 | — |
| Geçersiz değerli yeniden yükleme fırtınası | ✅ 10/10 | ✅ 10/10 | — |
| Saat sıçraması (100 / 1000 yıl) | ✅ 10/10 | ✅ 10/10 | — |
| Hız sınırı: tam 100 kabul (12.800 eşzamanlı çağrı) | ✅ 10/10 | ✅ 10/10 | — |
| Bellek dayanıklılığı (1M çağrı; çağrı başına tahsis) | ✅ 10/10 (≈25 B) | ✅ 10/10 (≈73 B) | — |
| Hedging: kaybeden yanıt sızıntısı | ✅ 10/10 | ✅ 10/10 | ✅ 10/10 |
| HTTP senkron `Send` yeniden denenir | ✅ 10/10 | — | ✅ 10/10 |
| Bozuk `Retry-After` başlıkları | ✅ 10/10 | — | ❌ 0/10 |
| Geri sarılamayan gövdeli POST (veri bozulması) | ✅ 10/10 | — | ✅ 10/10 |

**İz açıkken fırtına (Aegis'e özel, 16. senaryo):** Polly ve Microsoft iz üretmediği için yalnızca Aegis'e uygulanır. 32×300 çağrı, tüm
örnekleme açık; hata, zaman aşımı, boru hattına girmeden iptal ve yolda iptal karışık. Değişmezler: her başlayan span kapanır (sızıntı
yok), her alt span kendi çağrısının Aegis span'ının çocuğudur (çapraz ebeveyn karışması yok), çağrıdan sonra çağıranın
`Activity.Current` değeri yerindedir. Sonuç: 10/10. (İlk sürümde 4 turda 1 span eksik çıktı: sebep, `CancelAfter` zamanlayıcısının çağrıdan
önce tetiklenmesiydi; boru hattına girmeden iptal edilen çağrı bilinçli olarak span üretmez. İptal belirlenimci yapılınca kayboldu.)

**Bulunan hatalar (dürüst kayıt):**
- **Aegis (düzeltildi, 1.3.0):** `TimeSpan.MaxValue` zaman aşımı çalışma anında `ArgumentOutOfRangeException`
  fırlatıyordu. Zamanlayıcı sınırını aşan süreler artık sonsuz sayılıyor. Tek kural `AegisTimers`'ta; regresyon testi
  `TimerLimitTests`.
- **Polly 8.8.0:** fırlatan `OnHalfOpened` deneme isteğini her seferinde düşürür ve devre bir daha kapanamaz. Fırlatan
  bir telemetri dinleyicisi her çağrıyı düşürür. Polly geri çağrı hatasını çağrıya yayar; Aegis yutar ve
  `aegis.callback.errors.total` metriğiyle sayar.
- **Microsoft.Extensions.Http.Resilience 10.10.0:** `Retry-After: 2147483647` (saniye) ya da 9999 yılı tarihi
  `ArgumentOutOfRangeException` ile isteği düşürür. Aegis süreyi `MaxDelay` ile sınırlar.
- "—": kütüphane bu yeteneği sunmuyor ya da senaryo ona uygulanmıyor (ör. Polly'nin HTTP işleyicisi yok; Microsoft
  çekirdekte Polly'yi kullanır).
- Kapsam dışı (ayrı test altyapısında): Redis kesintisi/yavaşlığı Toxiproxy ile, sürüm matrisi Redis 6.2–8 ve
  Valkey'de (`docs/TEST-INFRASTRUCTURE.md`).

## Durum (1.5.0)

Aşama 1–5 ve 1.1.0 – 1.5.0 turları tamamlandı.
- 1.3.0: Polly 8.8.0 ve Microsoft 10.10.0'ın son 10 sürümünde eksik kalan her genel API ve davranış kapandı (bölüm 11).
  Batırma testlerinde Aegis 15/15 senaryoyu her turda geçti; Polly ve Microsoft'ta birer kırılma bulundu (bölüm 12).
- Microsoft.Extensions.Http.Resilience'ta satır satır denetimle bulunan üç boşluk ve son-yanıt davranışı kapandı
  (bölüm 7).
- NuGet'teki ilk 20 resilience paketinin 18'inde tam karşılık var, 20.'de kısmi karşılık var, 15.'si için kasıtlı
  olarak karşılık yok (bölüm 10).
- 1.4.0: klasik ASP.NET Web API 2 sunucu hattı (`Aegis.Resilience.WebApi`) ve .NET Aspire entegrasyonu (`Aegis.Resilience.Extensions.Aspire`)
  eklendi; teknik olarak kısmi alan kalmadı. Polly'den geçiş için `docs/MIGRATION.md`.
- 1.5.0: iz (trace) desteği (`ActivitySource "Aegis"`): boru hattı başına span, strateji olayları span olayı, Aspire `AddTracing`.
  Gerçek OpenTelemetry Collector'a gRPC ve HTTP/protobuf ile native AOT dahil doğrulandı (bölüm 6, `docs/TEST-INFRASTRUCTURE.md` 6.1).

Polly ve Microsoft'un Aegis'e göre üstün olduğu alan teknik değil:

- **Olgunluk:** Polly yıllardır çok sayıda üretim projesinde kullanılıyor ve geniş bir topluluğa sahip. Aegis'in henüz
  üretim geçmişi yok. Bu fark kodla değil, zamanla ve kullanımla kapanır.

## Yol haritası (tamamlandı)

1. **Aşama 1 — Çekirdek API eşitliği:** tüm `Execute`/`ExecuteAsync` biçimleri (senkron, `CancellationToken`, `TState`,
   `ExecuteOutcomeAsync`), `TimeProvider` + `Randomizer`, bağlam farkındalıklı async predicate'ler ve `PredicateBuilder`,
   tipli özellik anahtarları, `ContinueOnCapturedContext`.
2. **Aşama 2 — Telemetri:** `ILogger` olay günlüğü, standart etiketler, `OperationKey`, zenginleştiriciler, tüm olay
   geri çağrıları, `RetryAfter`.
3. **Aşama 3 — Strateji eşitliği:** sonuca göre fallback/hedging, hedging `ActionGenerator`/`DelayGenerator`, CB
   `ManualControl`/`StateProvider`, `System.Threading.RateLimiting` köprüsü, ayrık kaos stratejileri, tipli boru hattı.
4. **Aşama 4 — Registry ve test:** generic/dinamik anahtarlı boru hatları, tam yeniden yükleme, `Aegis.Resilience.Testing`.
5. **Aşama 5 — HTTP:** MS standart handler ve standart hedging eşdeğerleri (yönlendirme grupları, uç nokta başına CB).
6. **Her aşamada:** Polly'nin ilgili testlerinden uyarlanan eşitlik testleri + BenchmarkDotNet karşılaştırması.

## 13. gRPC

| Yetenek | Polly / Microsoft | Grpc.Net.Client (yerleşik) | Aegis |
|---|---|---|---|
| gRPC hatasını görmek (`grpc-status` trailer'da, HTTP 200) | ❌ HTTP katmanında çalışır | ✅ | ✅ interceptor |
| Retry (yalnızca `Unavailable`), commit kuralı, deadline tüm denemeleri kapsar | ❌ | ✅ | ✅ |
| `grpc-retry-pushback-ms`, `grpc-previous-rpc-attempts` | ❌ | ✅ | ✅ |
| Hedging | ❌ | ✅ | ✅ (unary + sunucu akışı) |
| Retry throttling (bütçe) | ❌ | ✅ | ✅ `RetryBudget` (retry ve hedging) |
| Devre kesici (yalnızca sunucu tarafı hatalar) | ❌ | ❌ | ✅ ⭐ |
| Deneme zaman aşımı (iptal çevrimi ile) | ❌ | ❌ | ✅ ⭐ |
| Telemetri (metot bazında `operation.key`) | ❌ | 🟡 | ✅ ⭐ |
| Sunucu koruması (eşzamanlılık, hız, zaman aşımı, devre) + gRPC durum ve pushback | ❌ | ❌ | ✅ ⭐ |
| Sunucu zaman aşımı servis kodunu keser | — | ❌ | ✅ ⭐ |
| HTTP işleyicilerinin gRPC'yi bozmaması | 🟡 sürüm uyarısı | — | ✅ dokunmadan geçirir |
| Gelen istek hız sınırı gRPC yanıtı | ❌ HTTP 429 | — | ✅ ⭐ trailers-only + pushback |
| İstemci / çift yönlü akış retry (tamponlu yeniden oynatma, `MaxRetryBufferBytes`) | ❌ | ✅ | ✅ |
| Uç nokta ayıklama (outlier detection, Envoy varsayılanları) | ❌ | ❌ yalnızca taşıma hatası | ✅ ⭐ `grpc-status` ile |

**Interceptor maliyeti (.NET 10, BenchmarkDotNet, bellek içi çağrı, unary):**

| Senaryo | Süre | Bellek |
|---|---:|---:|
| Çıplak çağrı | 63 ns | 208 B |
| Aegis, boş boru hattı | 388 ns | 688 B |
| Polly interceptor (standart zincir) | 2.095 ns | 720 B |
| Aegis standart | 2.000 ns (0,95×) | 688 B (0,96×) |

Aegis standart zinciri Polly ile aynı yükte hem daha hızlı hem daha az bellek kullanır (durum nesneli Grpc.Core yapıcıları; closure yok).
