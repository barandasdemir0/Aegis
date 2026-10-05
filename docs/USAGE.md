# 📘 Aegis — Kullanım Kılavuzu

Bu belge **"nasıl kullanırım?"** sorusuna cevap verir. Özelliklerin ne olduğu için [README.md](../README.md),
düzeltme geçmişi için [CHANGELOG.md](../CHANGELOG.md), uçtan uca örnek için [samples/RealWorld](../samples/RealWorld/README.md).

Her bölümde: **ne zaman kullanılır → kurulum kodu → seçenekler ve varsayılanlar → fırlatılan istisna → dikkat**.

---

## İçindekiler

1. [Kurulum](#1-kurulum)
2. [5 dakikada başlangıç](#2-5-dakikada-başlangıç)
3. [Temel kavramlar](#3-temel-kavramlar) — pipeline, builder, context, strateji sırası, istisnalar
4. [Stratejiler (A'dan Z'ye)](#4-stratejiler)
   - 4.1 [Retry](#41-retry) · 4.2 [Circuit Breaker](#42-circuit-breaker) · 4.3 [Timeout](#43-timeout) · 4.4 [Concurrency Limiter](#44-concurrency-limiter-bulkhead)
   - 4.5 [Rate Limiter](#45-rate-limiter-token-bucket) · 4.6 [Sliding Window](#46-sliding-window-rate-limiter) · 4.7 [Partitioned](#47-partitioned-rate-limiter-çok-kiracılı)
   - 4.8 [Hedging](#48-hedging) · 4.9 [Request Collapser](#49-request-collapser-singleflight) · 4.10 [Fallback](#410-fallback) · 4.11 [Stale-While-Revalidate](#411-stale-while-revalidate)
   - 4.12 [Cache](#412-cache-aside) · 4.13 [Adaptive Concurrency](#413-adaptive-concurrency) · 4.14 [Chaos](#414-chaos-engineering) · 4.15 [Standard Resilience](#415-standard-resilience-5-in-1)
5. [İleri seviye](#5-i̇leri-seviye) — Result-based handling, dinamik generator'lar, hot reload, kompozisyon
6. [Dependency Injection](#6-dependency-injection)
7. [HttpClient entegrasyonu](#7-httpclient-entegrasyonu) — handler'lar, replay, Retry-After, canary, multi-endpoint hedging
8. [AOP — `[AegisPolicy]`](#8-aop--aegispolicy)
9. [Dağıtık Circuit Breaker (Redis)](#9-dağıtık-circuit-breaker-redis)
10. [Health Check, Dashboard, Telemetri](#10-health-check-dashboard-telemetri)
11. [Doğrulama kuralları](#11-doğrulama-kuralları-fail-fast)
12. [Sık yapılan hatalar](#12-sık-yapılan-hatalar)
13. [Polly'den ve diğer kütüphanelerden geçiş](#13-pollyden-ve-diğer-kütüphanelerden-geçiş)
14. [Test yazma](#14-test-yazma)
15. [Platform notları](#15-platform-notları-net-framework-native-aot-strong-naming) — .NET Framework, Native AOT, strong naming
16. [Sunucu tarafı koruma (ASP.NET Core)](#16-sunucu-tarafı-koruma-aspnet-core) — gelen istek hız sınırı, uç nokta boru hattı
17. [gRPC](#17-grpc-aegisgrpc-aegisgrpcaspnetcore) — istemci interceptor'ı, akışlarda retry, uç nokta ayıklama, sunucu koruması

---

## 1. Kurulum

Hedef çerçeveler: **net8.0, net9.0, net10.0, netstandard2.0, net462** (.NET Framework 4.6.2+). Beş paket yalnızca .NET 8+ hedefler: ASP.NET Core'a bağlı Dashboard, AspNetCore, Grpc.AspNetCore ve Aspire; dayandığı Microsoft telemetri paketleri .NET Framework'ü desteklemediği için Extensions.Telemetry. Aegis.Resilience.WebApi ise yalnızca .NET Framework'tedir (Web API 2 başka platformda çalışmaz; .NET 8+ karşılığı Aegis.Resilience.AspNetCore).
Tüm derlemeler strong-name imzalıdır; .NET 8+ hedefleri Native AOT uyumludur (bkz. [15](#15-platform-notları-net-framework-native-aot-strong-naming)).
Yalnızca ihtiyacınız olan paketi alın:

```bash
dotnet add package Aegis.Resilience.Core                          # tüm stratejiler, .NET 8+ hedeflerinde sıfır bağımlılık
dotnet add package Aegis.Resilience.Extensions.DependencyInjection # AddAegis(), adlandırılmış pipeline'lar, AOP proxy
dotnet add package Aegis.Resilience.Extensions.Http               # HttpClient handler'ları
dotnet add package Aegis.Resilience.Extensions.HealthChecks       # /health entegrasyonu
dotnet add package Aegis.Resilience.Extensions.Dashboard          # /aegis web panosu
dotnet add package Aegis.Resilience.Distributed.Redis             # Redis destekli dağıtık devre kesici ve hız sınırlayıcı
dotnet add package Aegis.Resilience.RateLimiting                  # .NET System.Threading.RateLimiting köprüsü
dotnet add package Aegis.Resilience.Testing                       # test projeleri için boru hattı tanımlayıcıları
dotnet add package Aegis.Resilience.Extensions.Telemetry          # error.type / request.name metrik etiketleri
dotnet add package Aegis.Resilience.Extensions.Caching            # IDistributedCache (Redis, SQL Server...) cache deposu
dotnet add package Aegis.Resilience.Data.SqlClient                # SQL Server / Azure SQL geçici hata tanıma
dotnet add package Aegis.Resilience.AspNetCore                    # sunucu tarafı: gelen istek hız sınırı, uç nokta boru hattı
dotnet add package Aegis.Resilience.WebApi                        # klasik ASP.NET Web API 2 (.NET Framework): gelen istek hız sınırı
dotnet add package Aegis.Resilience.Extensions.Aspire             # .NET Aspire ServiceDefaults: tek çağrıyla HTTP dayanıklılığı + metrik + sağlık
dotnet add package Aegis.Resilience.Grpc                           # gRPC istemci dayanıklılığı (interceptor)
dotnet add package Aegis.Resilience.Grpc.AspNetCore                # gRPC sunucu koruması (.NET 8+)
```

| Paket | Bağımlılığı |
|---|---|
| `Core` | **yok** (saf BCL; netstandard2.0/net462'de yalnızca resmi `Microsoft.Bcl.TimeProvider` ve `System.*` destek paketleri) |
| `Extensions.DependencyInjection` | `Microsoft.Extensions.DependencyInjection.Abstractions`, `.Options`, `.Logging.Abstractions` |
| `Extensions.Http` | `Microsoft.Extensions.Http`, `Microsoft.Extensions.Configuration.Binder` |
| `Extensions.HealthChecks` | `Microsoft.Extensions.Diagnostics.HealthChecks` |
| `Extensions.Dashboard` | ASP.NET Core (framework reference) |
| `Distributed.Redis` | `StackExchange.Redis` (devre kesici ve hız sınırlayıcı depoları) |
| `RateLimiting` | `System.Threading.RateLimiting` |
| `Testing` | yok (yalnızca `Core`) |
| `Extensions.Telemetry` | `Microsoft.Extensions.Diagnostics.ExceptionSummarization`, `.Telemetry.Abstractions`, `.Http.Diagnostics` |
| `Extensions.Caching` | `Microsoft.Extensions.Caching.Abstractions`, `.DependencyInjection.Abstractions` (eski hedeflerde ayrıca `System.Text.Json`) |
| `Data.SqlClient` | `Microsoft.Data.SqlClient` (≥ 5.2.3) |
| `AspNetCore` | ASP.NET Core (framework reference), `Distributed.Abstractions` |
| `Grpc` | `Grpc.Net.Client`, `Grpc.Net.ClientFactory` |
| `Grpc.AspNetCore` | `Grpc.AspNetCore.Server` |

> Yerel geliştirme için paketler `artifacts/packages` klasöründe; `samples/RealWorld/nuget.config` yerel akış örneğidir.

---

## 2. 5 dakikada başlangıç

```csharp
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Exceptions;

// 1) Bir kez kur (uygulama ömrü boyunca yaşasın — Singleton)
var pipeline = new AegisPipelineBuilder("odeme-servisi")
    .AddTimeout(TimeSpan.FromSeconds(10))                                   // toplam süre sınırı
    .AddRetry(o => { o.MaxRetryAttempts = 3; o.Delay = TimeSpan.FromMilliseconds(200); })
    .AddCircuitBreaker(o => { o.FailureRatio = 0.5; o.MinimumThroughput = 10; o.BreakDuration = TimeSpan.FromSeconds(30); })
    .Build();

// 2) Her çağrıda kullan
try
{
    var sonuc = await pipeline.ExecuteAsync(async ctx =>
    {
        // ctx.CancellationToken'ı MUTLAKA ilet — zaman aşımı ve iptal buradan akar
        return await httpClient.GetStringAsync("https://api.ornek.com/odeme", ctx.CancellationToken);
    });
}
catch (BrokenCircuitException)  { /* hedef servis çökmüş, istek hiç gönderilmedi */ }
catch (AegisTimeoutException)   { /* 10sn aşıldı */ }
catch (HttpRequestException)    { /* 3 retry sonrası da başarısız */ }
```

Çağıranın kendi `CancellationToken`'ı varsa bağlamla verin:

```csharp
var sonuc = await pipeline.ExecuteAsync(ctx => IsYap(ctx.CancellationToken), new AegisContext(httpContext.RequestAborted));
```

### Tüm çalıştırma biçimleri (1.0.10, Polly v8 eşdeğeri)

| Biçim | Örnek | Ne zaman |
|---|---|---|
| Bağlamla | `ExecuteAsync(ctx => ..., ctx)` | Özellik, OperationKey taşımak |
| Yalnızca token | `ExecuteAsync(ct => IsYap(ct), token)` | Bağlam oluşturmadan (havuzdan alınır) |
| Durumlu (closure'suz) | `ExecuteAsync(static (ctx, s) => s.Client.GetAsync(...), (Client: http, Id: 5))` | Sıcak yollarda tahsisi sıfırlamak |
| Durum + token | `ExecuteAsync(static (s, ct) => ..., durum, token)` | Polly'den geçişte birebir karşılık |
| Fırlatmayan | `var o = await ExecuteOutcomeAsync(ctx => ...); if (!o.IsSuccess) ...` | Yüksek hacimde istisna maliyetinden kaçınmak |
| Senkron | `Execute(() => ...)`, `Execute(ct => ..., token)`, `Execute(static (ctx, s) => ..., s)` | Senkron kod tabanları |

- Tüm biçimler aynı çekirdekten geçer: iptal, telemetri, bağlam havuzu ve "çağıranın token'ı" kuralı her birinde aynıdır.
- Async biçimler hatayı **asla eşzamanlı fırlatmaz**; hata dönen görevin içindedir (`Task.WhenAny`, `.AsTask()` güvenli).
- `ExecuteOutcomeAsync` hiçbir durumda fırlatmaz; geri çağrının kendisi fırlatsa bile sonuç olarak döner.
- Senkron `Execute`, stratejiler eşzamanlı tamamlanırsa hiç bloklamaz; Retry gecikmesi gibi durumlarda çağıran iş parçacığı bekler.

---

## 3. Temel kavramlar

### Pipeline ve Builder
- `AegisPipelineBuilder(name)` → `.AddXxx(...)` zinciri → `.Build()` → `IAegisPipeline` (`IDisposable`).
- **Bir builder yalnızca bir kez `Build()` edilir.** İkinci çağrı veya `Build()` sonrası `AddStrategy` → `InvalidOperationException`. Sebep: strateji örnekleri (devre kesici durumu, semaforlar) pipeline'lar arasında paylaşılamaz.
- Pipeline **pahalı ve durum tutan** bir nesnedir: **Singleton** olarak yaşatın, istek başına kurmayın.
- `pipeline.Dispose()` sahip olduğu stratejileri (Cache timer'ı, semaforlar) serbest bırakır; idempotenttir.

### Strateji sırası (dıştan içe)
`Build()`'e eklediğiniz sıra **dıştan içe** sarmalama sırasıdır. İlk eklenen en dışta çalışır:

```csharp
.AddTimeout(30s)      // 1. EN DIŞ: tüm retry'lar dahil toplam süre
.AddRetry(3)          // 2. her deneme aşağıdakileri yeniden çalıştırır
.AddCircuitBreaker()  // 3. her denemede devre kontrol edilir
.AddTimeout(5s)       // 4. EN İÇ: tek bir denemenin süresi
```
Yaygın hata: `Retry`'ı `Timeout`'un **içine** koymak → toplam süre sınırsız olur. Emin değilseniz `AddStandardResilience` kullanın (bölüm 4.15).

### AegisContext
Her çalıştırmaya eşlik eden bağlam:

```csharp
var ctx = new AegisContext(cancellationToken, pipelineName: "odeme");
ctx.SetProperty("TenantId", "acme");          // stratejilere/callback'e veri taşır
ctx.CorrelationId;                             // otomatik üretilir (izleme için)
ctx.TryGetProperty<string>("TenantId", out var t);

// Tipli anahtar (1.0.10, Polly ResiliencePropertyKey<T>): yanlış tip derleme anında yakalanır
static readonly AegisPropertyKey<string> Tenant = new("TenantId");
ctx.SetProperty(Tenant, "acme");
ctx.TryGetProperty(Tenant, out var tenant);                 // string anahtarla aynı sözlük
ctx.OperationKey = "GetOrder";                               // işlem bazlı ayrım (alt bağlamlara da geçer)
```
- `ctx.CancellationToken` stratejiler tarafından **ikame edilir** (Timeout bağlı token verir). Callback içinde **her zaman `ctx.CancellationToken`** kullanın, dıştaki token'ı değil.
- Bağlam vermezseniz havuzdan alınır ve çağrı sonunda geri verilir; **callback dışında saklamayın**.
- Stratejilerin bağlamdan okuduğu iyi bilinen anahtarlar `AegisContextKeys` sınıfındadır: `CacheKey`, `PartitionKey`, `RetryAfterDelay`, `SuppressAdditionalAttempts`. Metin yerine bu sabitleri kullanın. Örneğin `ctx.Properties[AegisContextKeys.SuppressAdditionalAttempts] = true` ile bir çağrıda Retry ve Hedging'in ek deneme yapmasını engelleyebilirsiniz.
- Çağıran iptal ettiğinde fırlayan `OperationCanceledException.CancellationToken == çağıranın token'ı` garanti edilir (Polly #3086 uyumu).

### İstisnalar
| İstisna | Kim fırlatır | Anlamı |
|---|---|---|
| `BrokenCircuitException` | Circuit Breaker | Devre açık/isolated, istek **iletilmedi** |
| `AegisTimeoutException` | Timeout | Süre aşıldı (`.Timeout` özelliğinde süre) |
| `RateLimitRejectedException` | Rate/Sliding/Partitioned/Concurrency/Adaptive | Kota veya eşzamanlılık reddi |
| `ChaosInjectedException` | Chaos | Yapay arıza |
| `OperationCanceledException` | — | Çağıran iptal etti (token çağıranındır) |
| Orijinal istisna | Retry/Hedging | Denemeler tükendi — **yığın izi korunur**, sarmalanmaz |

Hepsi `AegisException`'dan türer; tek `catch (AegisException)` ile dayanıklılık kaynaklı hataları ayırabilirsiniz.

---

## 4. Stratejiler

### 4.1 Retry
**Ne zaman:** Geçici ağ hataları, 5xx, kilit çakışması.

```csharp
.AddRetry(o =>
{
    o.MaxRetryAttempts = 3;                             // ≥0; 3 = toplam 4 deneme
    o.BackoffType = DelayBackoffType.Exponential;       // Constant | Linear | Exponential | DecorrelatedJitter
    o.Delay = TimeSpan.FromMilliseconds(200);           // taban gecikme
    o.MaxDelay = TimeSpan.FromSeconds(30);              // üstel artış tavanı (taşma koruması)
    o.UseJitter = true;                                 // thundering herd önleme (varsayılan true)
    o.ShouldHandle = ex => ex is HttpRequestException or TimeoutException;   // varsayılan: her istisna
    o.ShouldHandleResult = r => r is HttpResponseMessage { IsSuccessStatusCode: false }; // istisnasız retry (bkz. 5.1)
    o.DelayGenerator = a => ValueTask.FromResult<TimeSpan?>(TimeSpan.FromMilliseconds((a.AttemptNumber + 1) * 100)); // AttemptNumber 0'dan başlar; null → varsayılan hesap
    o.OnRetry = a => { log.Warn($"deneme {a.AttemptNumber}, {a.RetryDelay} sonra, hata: {a.Exception?.Message}"); return default; };
})
```
- **Varsayılanlar (2.0.0):** 3 deneme, 2 sn taban, üstel artış + jitter, en fazla 30 sn gecikme (Microsoft standart işleyicisiyle aynı).
- **Deneme numarası 0'dan başlar** (Polly ile aynı): `OnRetry`/`DelayGenerator`'da ilk yeniden deneme `AttemptNumber == 0`.
- **Asla retry edilmez:** iptal (`OperationCanceledException`) ve retler (`BrokenCircuitException`, `RateLimitRejectedException`) —
  `ShouldHandle` ne derse desin. Ret, isteğin hedefe hiç gitmediği anlamına gelir; hemen yeniden denemek yalnızca boş bir döngü
  üretir (Microsoft standart işleyicisi de retleri yeniden denemez; Polly'nin varsayılanı dener).
- **Retry bütçesi (2.0.0, isteğe bağlı):** `o.Budget = new RetryBudget(maxTokens: 10, tokenRatio: 0.1)` — gRPC retry throttling.
  Ele alınan her hata 1 jeton düşürür, her başarı 0,1 ekler; jetonlar yarının altına inince yeniden deneme yapılmaz. Bağımlılık
  bozulduğunda yeniden denemelerin trafiği katlamasını (retry fırtınası) önler. Aynı bağımlılığa giden tüm boru hatlarında aynı
  örneği paylaşın. Tükenince `OnRetryBudgetExhausted` olayı.
- Retry edilen `IDisposable` sonuçlar (ör. başarısız `HttpResponseMessage`) **otomatik dispose edilir**; son sonuç edilmez.
- HTTP `Retry-After` başlığı bağlamda bulunursa gecikmeyi **ezer** (`MaxDelay` ile sınırlı) — bkz. 7.4.

### 4.2 Circuit Breaker
**Ne zaman:** Çökmüş bir servise istek yığılmasını kesmek, hızlı başarısız olmak.

```csharp
.AddCircuitBreaker(o =>
{
    o.FailureRatio = 0.5;                           // (0,1] — hataların oranı bu eşiği geçince açılır
    o.MinimumThroughput = 10;                       // ≥1 — karar için pencerede en az bu kadar istek
    o.SamplingDuration = TimeSpan.FromSeconds(10);  // oranın hesaplandığı kayan pencere (10 dilim)
    o.BreakDuration = TimeSpan.FromSeconds(30);     // açık kalma süresi; sonra HalfOpen (tek probe)
    o.BreakDurationGenerator = e => e.Context.TryGetProperty<int>("Kritiklik", out var k) && k > 5 ? TimeSpan.FromMinutes(2) : null; // null → statik BreakDuration
    o.SlowCallDurationThreshold = TimeSpan.FromSeconds(2); o.SlowCallRateThreshold = 0.8;      // yavaş çağrı = hata
    o.ShouldHandle = ex => ex is not ArgumentException;   // iş kuralı hatalarını sayma
    o.ShouldHandleResult = r => r is HttpResponseMessage { StatusCode: >= HttpStatusCode.InternalServerError };
    o.OnOpened = e => { alarm.Gonder($"{e.Context.PipelineName} açıldı, {e.BreakDuration}"); return default; };
    o.OnHalfOpened = e => default; o.OnClosed = e => default;
    o.ConsecutiveFailureThreshold = 5;              // (1.2.0, isteğe bağlı) art arda 5 hatada da aç — Polly v7 CircuitBreaker(5, ...)
})
```
- **Art arda hata eşiği (1.2.0):** `ConsecutiveFailureThreshold` oran kuralına ek bir koşuldur. Devre, art arda N işlenen
  hatada `MinimumThroughput` ve pencere beklenmeden açılır; araya giren tek başarı sayacı sıfırlar. Oranın hiç
  hesaplanamadığı düşük trafikli bağımlılıklar için uygundur. Varsayılan `null` (kapalı).
- **Kayan pencere:** Hata oranı son `SamplingDuration` içindeki çağrılardan hesaplanır (10 dilimli pencere, en fazla bir dilim hassasiyetle). Sabit pencerenin aksine, pencere sınırına denk gelen hata patlamaları ikiye bölünüp kaçmaz.
- **Varsayılanlar (2.0.0, Polly ve Microsoft ile aynı):** `FailureRatio` 0,1, `MinimumThroughput` 100, `SamplingDuration` 30 sn,
  `BreakDuration` 5 sn. Az trafikli bağımlılıkta tek tük hata devreyi açmaz; gerekirse `ConsecutiveFailureThreshold` kullanın.
- **Ne hata sayılır:** Retler ve çağıranın kendi iptali sayılmaz. **Çağıran iptal etmediği halde gelen iptal sayılır** (ör.
  `HttpClient.Timeout` → `TaskCanceledException`): bağımlılık yanıt vermiyordur. Polly bunu yok sayar ve devre takılan servise
  karşı hiç açılmaz; bu Aegis'te bilerek farklıdır.
- **Yarı açıkta art arda başarı (2.0.0):** `o.HalfOpenSuccessThreshold = 3` — devre ancak art arda 3 başarılı denemeyle kapanır
  (varsayılan 1, Polly ile aynı).
- **Sayı tabanlı pencere (2.0.0):** `o.SamplingCount = 50` — oran son 50 çağrıdan hesaplanır (varsayılan: zaman penceresi).
- **Gölge ve kapalı kip (2.0.0):** `o.Mode = CircuitBreakerMode.Shadow` — devreyi üretime almadan önce reddetmeden izleyin;
  `Disabled` tamamen geçirgen. Elle izolasyon gölge kipte de uygulanır.
- **Takılan deneme isteği (2.0.0):** HalfOpen'daki deneme bir `BreakDuration`'dan uzun sürerse terk edilir ve yenisine izin verilir.
- **HalfOpen'da yalnızca 1 probe** geçer; eşzamanlı diğer istekler `BrokenCircuitException` alır. Probe başarılıysa Closed (pencere sıfırlanır), başarısızsa yeni `BreakDuration` ile Open. Probe kimlikle izlenir: devre yeniden açılıp yeni bir probe başladıktan sonra biten eski probe, yenisinin kilidini açamaz.
- **Olay hataları sonucu değiştirmez (tüm stratejilerde; `OnRetry` ve `OnTimeout` 2.0.0'dan itibaren):** `OnOpened`/`OnClosed`/`OnHalfOpened` veya `BreakDurationGenerator` istisna fırlatırsa bu istisna yutulur ve `aegis.callback.errors.total` sayacına yazılır. Çağıran her zaman gerçek sonucu veya gerçek istisnayı alır. Patlayan üretici statik `BreakDuration`'a düşer.
- **Durumu okumak yan etkisizdir:** `State` / `LastKnownState` açılma süresi dolmuş devre için `HalfOpen` raporlar ama geçişi yapmaz. Health check ve pano yoklamaları `OnHalfOpened` olayını yutamaz; olay, probe olarak geçen ilk gerçek çağrıda tetiklenir.
- **Red ayrıntısı ve geçiş bilgisi (1.3.0, Polly 8.x eşdeğeri):**
  - Açık devre reddi `BrokenCircuitException.RetryAfter` taşır: deneme isteğinin kabul edileceği ana kalan süre.
  - Elle izole edilmiş devre `IsolatedCircuitException` fırlatır. Bu bir `BrokenCircuitException` alt tipidir.
  - Olaylarda `e.HalfOpenAttempts` (art arda başarısız deneme isteği), `e.IsManual` (ManualControl/pano ile geçiş) ve
    `e.Exception` / `e.Result` (geçişi tetikleyen sonuç) bulunur.
  - Elle `IsolateAsync` / `CloseAsync` artık `OnOpened` / `OnClosed`'u `IsManual = true` ile tetikler.

```csharp
o.BreakDurationGenerator = e => TimeSpan.FromSeconds(5 * Math.Pow(2, e.HalfOpenAttempts)); // 5, 10, 20 sn...
o.OnOpened = e => { log.Warn($"açıldı (elle: {e.IsManual}): {e.Exception?.Message}"); return default; };

catch (IsolatedCircuitException) { /* bakım modu */ }
catch (BrokenCircuitException ex) { Response.Headers.RetryAfter = ((int)(ex.RetryAfter ?? TimeSpan.Zero).TotalSeconds).ToString(); }
```
- Durumu okumak: strateji `IObservableCircuitState` uygular → `LastKnownState`. Dashboard/HealthCheck bunu kullanır.
- Manuel kontrol: Dashboard (bölüm 10) üzerinden Isolate/Reset.
- **Kurulumdan önce oluşturulan kontrol ve durum sağlayıcı (1.0.12, Polly eşdeğeri):**

```csharp
var bakimModu = new CircuitBreakerManualControl();          // birden çok devreye bağlanabilir
var odemeDurumu = new CircuitBreakerStateProvider();          // tek devreye bağlanır

.AddCircuitBreaker(o => { o.ManualControl = bakimModu; o.StateProvider = odemeDurumu; })

await bakimModu.IsolateAsync();   // bağlı tüm devreler izole (yerel + Redis'li dağıtık)
await bakimModu.CloseAsync();
if (odemeDurumu.CircuitState == CircuitState.Open) { ... }
```
`new CircuitBreakerManualControl(isIsolated: true)` ile bağlanan devreler izole başlar. Dağıtık devre kesici kurucuda
Redis'e yazmaz; izolasyonu ilk çağrıda, çağrı geçmeden uygular.

- **Sağlık bilgisine göre açılma süresi (1.1.0, Microsoft `BreakDurationGenerator` sağlık argümanları eşdeğeri):**
  Devre açılırken `e.Health` pencerenin istatistiğini taşır (`SuccessCount`, `FailureCount`, `SlowCallCount`,
  `Throughput`, `FailureRate`). Yerel ve dağıtık devre kesicide aynıdır; `OnOpened` içinde de okunabilir.

```csharp
o.BreakDurationGenerator = e => e.Health.FailureRate >= 0.9
    ? TimeSpan.FromMinutes(2)      // neredeyse tamamen çökmüş: uzun bekle
    : null;                         // null → statik BreakDuration
```

### 4.3 Timeout
```csharp
.AddTimeout(TimeSpan.FromSeconds(5))                                         // Optimistic (varsayılan)
.AddTimeout(TimeSpan.FromSeconds(5), o => o.Mode = TimeoutStrategyMode.Pessimistic)
.AddTimeout(TimeSpan.FromSeconds(30), o =>
{
    o.TimeoutGenerator = ctx => ctx.TryGetProperty<TimeSpan>("KalanButce", out var b) ? b : null; // null → statik
    o.OnTimeout = (ctx, t) => { log.Warn($"{t} aşıldı"); return default; };
})
```
- **Optimistic:** `ctx.CancellationToken` iptal edilir; kodunuz token'a **saygı göstermelidir**. Ucuz, önerilen.
- **Pessimistic:** Token'ı yok sayan kod için; iş `Task.Run` ile izole edilir, süre dolunca **terk edilir** (arka planda bitmeye devam eder — CPU/bellek maliyeti sizin). Yalnızca token almayan eski kütüphaneler için.
- `TimeoutGenerator` **0 veya negatif** döndürürse "bütçe tükendi" sayılır: callback **hiç çalıştırılmaz**, anında `AegisTimeoutException`. Zaman aşımını kapatmak için `Timeout.InfiniteTimeSpan` döndürün.
- Statik `Timeout` 0/negatif olamaz (kurulumda hata); `Timeout.InfiniteTimeSpan` geçerlidir.
- **Çok uzun süreler (1.3.0):** .NET zamanlayıcılarının üst sınırı (int.MaxValue ms ≈ 24,8 gün) aşan her süre sonsuz
  sayılır: `TimeSpan.MaxValue` zaman aşımı, retry/hedging gecikmesi, kaos gecikmesi, kuyruk bekleme süresi. Önceden çalışma
  anında `ArgumentOutOfRangeException` fırlatıyordu (batırma testi buldu). Böyle bir gecikmeyi yalnızca iptal sonlandırır.

### 4.4 Concurrency Limiter (Bulkhead)
**Ne zaman:** Bir bağımlılığın aynı anda kaç isteği kaldırabileceğini sınırlamak; bir servisin tüm thread'leri yemesini önlemek.

```csharp
.AddConcurrencyLimiter(maxConcurrent: 20)                                     // dolunca anında red (varsayılan)
.AddConcurrencyLimiter(maxConcurrent: 20, o => { o.QueueLimit = 50; o.QueueTimeout = TimeSpan.FromMilliseconds(500); })
```
- `maxConcurrent` ≥1. **Varsayılan: sınır doluysa beklemeden `RateLimitRejectedException`** (Polly, Microsoft standart işleyicisi
  ve .NET `ConcurrencyLimiter` ile aynı; 2.0.0). Gerekçe: aşırı yükte kuyruk gecikmeyi ve belleği büyütür, istemci zaten
  vazgeçmişken iş yapılır; hızlı red, çağıranın retry/fallback/devre kesicisinin hemen devreye girmesini sağlar (yük atma).
- Bekleme isteniyorsa kuyruk **sınırlıdır**: `QueueLimit` (en fazla bekleyen) ve `QueueTimeout` (en uzun bekleme). Kuyruk
  doluysa ya da süre biterse red.
- İptal edilen veya patlayan istekler izni **geri bırakır** (sızıntı yok). `Dispose` sonrası yeni çağrılar `ObjectDisposedException`, uçuştakiler tamamlanır.
- Canlı limit değişimi `OptionsProvider` ile: büyütme anında, küçültme mevcut işler bittikçe uygulanır.

### 4.5 Rate Limiter (Token Bucket)
**Ne zaman:** Dış API kotasına uymak ("dakikada 100 istek").

```csharp
.AddRateLimiter(permitLimit: 100, window: TimeSpan.FromMinutes(1), o => o.QueueTimeout = TimeSpan.FromSeconds(2))
```
- Token'lar **kademeli** dolar (100/60sn = 1.67 token/sn); sabit pencere değil. Patlamalara (burst) `permitLimit` kadar izin verir.
- Callback hata verse de token **harcanır** (Polly lease semantiği).
- Red mesajında pencere ve limit yazar; çağırana `Retry-After` üretmek için kullanılabilir.
- **1.0.11:** `RateLimitRejectedException.RetryAfter` bir sonraki iznin açılmasına kalan süreyi taşır (token bucket ve
  kayan pencerede hesaplanır, eşzamanlılık sınırında `null`). Tüm sınırlayıcılarda `OnRejected` bildirimi var:

```csharp
catch (RateLimitRejectedException ex) when (ex.RetryAfter is { } bekle)
{
    context.Response.Headers.RetryAfter = ((int)Math.Ceiling(bekle.TotalSeconds)).ToString();
}

.AddRateLimiter(new RateLimiterOptions { PermitLimit = 100, Window = TimeSpan.FromMinutes(1),
    OnRejected = a => { log.LogWarning("Kota: {Strateji} {Bekle}", a.StrategyName, a.RetryAfter); return default; } })
```

### 4.5b .NET `System.Threading.RateLimiting` köprüsü (1.0.12, `Aegis.Resilience.RateLimiting`)
.NET'in hazır sınırlayıcıları (Polly.RateLimiting eşdeğeri) doğrudan kullanılabilir:

```csharp
using System.Threading.RateLimiting;
using Aegis.Resilience.RateLimiting;

.AddTokenBucketRateLimiter(new TokenBucketRateLimiterOptions { TokenLimit = 100, TokensPerPeriod = 100,
    ReplenishmentPeriod = TimeSpan.FromMinutes(1), QueueLimit = 0 })          // boru hattıyla dispose edilir
.AddFixedWindowRateLimiter(new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromSeconds(1) })
.AddRateLimiter(ortakSinirlayici)                                              // paylaşılan RateLimiter örneği
.AddRateLimiter(PartitionedRateLimiter.Create<AegisContext, string>(ctx =>     // kiracı başına kota
    RateLimitPartition.GetConcurrencyLimiter(Kiraci(ctx), _ => new ConcurrencyLimiterOptions { PermitLimit = 5 })))
```
- İzin, geri çağrı bitince (hata olsa da) iade edilir. Sınırlayıcının `RetryAfter` metaverisi `RateLimitRejectedException.RetryAfter`'a taşınır.
- Aegis'in kendi sınırlayıcıları (4.4–4.7) bu paketten bağımsızdır ve `Core` içinde kalır.

### 4.6 Sliding Window Rate Limiter
**Ne zaman:** Sabit pencerenin sınır açığını (t=59'da 100 + t=61'de 100) kapatmak.

```csharp
.AddSlidingWindowRateLimiter(permitLimit: 100, window: TimeSpan.FromMinutes(1), segmentsPerWindow: 6)
```
- Pencere `segmentsPerWindow` dilime bölünür; eski dilimler kademeli düşer. Daha fazla dilim = daha hassas, biraz daha fazla bellek.

### 4.7 Partitioned Rate Limiter (çok kiracılı)
**Ne zaman:** Kiracı/kullanıcı/API anahtarı başına **ayrı** kota; bir kiracının diğerini aç bırakmaması.

```csharp
.AddPartitionedRateLimiter(o =>
{
    o.PartitionKeySelector = ctx => ctx.TryGetProperty<string>("TenantId", out var t) ? t! : "anonim";
    o.DefaultOptions = new RateLimiterOptions { PermitLimit = 50, Window = TimeSpan.FromMinutes(1) };
    o.OptionsFactory = tenant => tenant == "vip" ? new RateLimiterOptions { PermitLimit = 500, Window = TimeSpan.FromMinutes(1) } : o.DefaultOptions; // kiracı bazlı kota
    o.MaxPartitions = 10_000;   // kardinalite/bellek koruması: aşılınca en uzun süredir kullanılmayan bölümler atılır
})
```
- `PartitionKeySelector` verilmezse bağlamdaki `"PartitionKey"` özelliği, o da yoksa pipeline adı kullanılır.
- **DoS notu:** Rastgele anahtarla gelen saldırgan bellek şişiremez; `MaxPartitions` aşılınca LRU tahliye çalışır (yük testinde 5.000 kiracı → sabit bellek).

### 4.8 Hedging
**Ne zaman:** Kuyruk gecikmesi (tail latency) — birincil yavaşlarsa paralel ikinci deneme başlat, ilk biten kazansın.

```csharp
.AddHedging(o =>
{
    o.MaxHedgedAttempts = 1;                          // ≥1: birincile EK yedek sayısı (Polly ile aynı; 2.0.0 öncesi toplamdı)
    o.HedgingDelay = TimeSpan.FromMilliseconds(200);  // birincil bu sürede bitmezse yedek başlar
    // TimeSpan.Zero → hepsi aynı anda; Timeout.InfiniteTimeSpan → yalnızca öncekiler BAŞARISIZ olunca (ardışık yedekleme)
})
```
- **1.0.11:** `DelayGenerator` deneme başına gecikme verir (Polly eşdeğeri), `OnHedging` her yedek denemeden önce çağrılır:
  `o.DelayGenerator = a => ValueTask.FromResult(a.AttemptNumber == 1 ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromMilliseconds(300));`
- Birincil deneme eşzamanlı başarılı olursa hiç bellek ayrılmaz (alt bağlam havuzdan gelir; 1.0.9'da 144 B).
- **1.0.12, Polly eşdeğeri:**
  - **Sonuca göre hedging:** `ShouldHandleResult` / `ShouldHandleOutcome` ile kötü sayılan sonuç (ör. 503) sıradaki denemeyi
    tetikler. Atılan kötü sonuçlar dispose edilir; hepsi kötüyse son sonuç (istisna değil) döner.
  - **`ActionGenerator`:** Yedek deneme farklı bir işlem çalıştırabilir (ör. başka bölge).
  - **Deneme numarası:** Alt bağlamda `HedgingOptions.AttemptNumberKey` ile okunur (birincilde yoktur, 0 kabul edin).

```csharp
.AddHedging(o =>
{
    o.ShouldHandleOutcome = new AegisPredicateBuilder().HandleResult<HttpResponseMessage>(r => (int)r.StatusCode >= 500);
    o.ActionGenerator = a => ctx => YedekBolgedenGetir(ctx);   // null dönerse asıl işlem
})
```
- Kaybeden denemeler **iptal edilir**, `IDisposable` sonuçları **dispose edilir**. Kazananın token'ı iptal edilmez.
- Her deneme kendi alt bağlamını alır (`CorrelationId` ebeveynle aynı, `Properties` kopyası); kazananın `Properties` değişiklikleri ebeveyne **geri birleştirilir**.
- **Yalnızca idempotent işlemler için** kullanın — iki deneme paralel çalışabilir.
- **İstisna koşulu (1.3.0, Polly `ShouldHandle` eşdeğeri):** `o.ShouldHandle = ex => ex is HttpRequestException or TimeoutException;`.
  Koşulun ele almadığı istisna nihai sonuç sayılır; yedekleme durur ve istisna çağırana iletilir. Örneğin
  `ArgumentException` başka denemede de düzelmez. Null (varsayılan) ise tüm istisnalar sonraki denemeye geçer.

### 4.9 Request Collapser (Singleflight)
**Ne zaman:** Aynı anda aynı veriyi isteyen N çağrıyı **tek** gerçek çalıştırmaya indirmek (cache stampede).

```csharp
.AddRequestCollapser(o => o.KeySelector = ctx => ctx.TryGetProperty<string>("UrunId", out var id) ? id : null)
```
- **`KeySelector` zorunludur** (1.0.5 kırıcı değişiklik). Verilmezse kurulumda `ArgumentException` fırlar. Anahtar **işlemi ve girdisini** birlikte tanımlamalıdır (ör. `$"urun:{id}"`). Eski varsayılan `CorrelationId`, aynı bağlamdaki farklı işlemleri tek işleme indiriyordu ve ikinci işlem birincinin sonucunu alıyordu.
- `KeySelector` `null`/boş dönerse o çağrı birleştirilmez.
- Sonuç tipi anahtarın parçasıdır: aynı anahtarla farklı tipte sonuç bekleyen çağrılar ayrı çalışır, `InvalidCastException` oluşmaz.
- Yalnızca **uçuştaki** istekler paylaşılır; uçuş bitince anahtar kaldırılır (önbellek değildir — onun için 4.12).
- Lider patlarsa tüm takipçiler aynı istisnayı alır; zehirli sonuç önbelleklenmez, sonraki çağrı taze başlar.
- Paylaşılan iş **çağıranların token'ından bağımsız** çalışır: biri iptal etse diğerleri etkilenmez.

### 4.10 Fallback
```csharp
.AddFallback(o =>
{
    o.ShouldHandle = ex => ex is HttpRequestException or AegisTimeoutException or BrokenCircuitException;
    o.FallbackHandler = (ctx, ex) => ValueTask.FromResult<object?>(new Fiyat { Deger = 0, Kaynak = "varsayilan" });
})
```
- **Zorunlu:** `FallbackHandler` (veya `FallbackAction`). Yoksa veya dönüş tipi `TResult` ile uyuşmazsa **açık** `InvalidOperationException` (sessiz `default` yok — veri bozulması önlenir). `null` yalnızca referans/`Nullable<T>` tipler için geçerli yedek değerdir.
- **Sonuca göre yedek (1.0.11, Polly eşdeğeri):** istisna fırlatmayan kötü sonuçlarda da yedeğe düşülür. `FallbackAction`
  hem istisnayı hem sonucu görür; `OnFallback` yedekten hemen önce çağrılır; `ShouldHandleOutcome` async koşul alır:

```csharp
.AddFallback(o =>
{
    o.ShouldHandleOutcome = new AegisPredicateBuilder()
        .Handle<HttpRequestException>()
        .HandleResult<HttpResponseMessage>(r => (int)r.StatusCode >= 500);
    o.FallbackAction = a => ValueTask.FromResult<object?>(OnbellekYaniti(a.Context));
    o.OnFallback = a => { log.LogWarning(a.Exception, "Yedeğe düşüldü"); return default; };
})
```
- Handler'ın kendisi patlarsa **handler hatası** yükselir.
- Fallback'i pipeline'da **en dışa** koyun ki içteki tüm stratejilerin hatalarını yakalasın.

### 4.11 Stale-While-Revalidate
**Ne zaman:** Kur, fiyat, katalog gibi "biraz eski olsun ama gelsin" veriler.

```csharp
.AddStaleFallback(o =>
{
    o.MaxStaleAge = TimeSpan.FromHours(24);          // bayat veri en fazla bu kadar sunulur
    o.FreshnessDuration = TimeSpan.FromMinutes(5);   // bu kadar "taze" sayılır, hedefe hiç gidilmez
    o.MaxCacheEntries = 10_000;
    o.KeyGenerator = ctx => ctx.TryGetProperty<string>("Sembol", out var s) ? s! : "default";
    o.ShouldHandle = ex => ex is not ArgumentException;
})
// Çağıran bayat mı olduğunu bağlamdan okur:
var kur = await p.ExecuteAsync(GetirAsync, ctx);
var bayat = ctx.TryGetProperty<bool>(StaleFallbackOptions.IsStaleDataKey, out var b) && b;
```
- Taze → hedefe gidilmez. Taze değil ama `MaxStaleAge` içinde → **bayat veri anında döner, arka planda tek yenileme** başlar (stampede yok). Önbellek boşsa → gerçek hata (veri uydurulmaz).
- Zaman ölçümü monotoniktir (saat değişimlerinden etkilenmez).

### 4.12 Cache-Aside
```csharp
.AddCache(TimeSpan.FromSeconds(30), o =>
{
    o.KeySelector = ctx => ctx.TryGetProperty<string>("Id", out var id) ? id! : "default"; // yoksa bağlamdaki "CacheKey", o da yoksa pipeline adı
    o.MaxEntries = 10_000;           // aşılınca LRU tahliye
    o.CacheNulls = false;            // null sonuçlar önbelleklensin mi
    o.OnCacheHit = (key, v) => { }; o.OnCacheMiss = key => { };
})
```
- TTL monotonik saatle ölçülür. Süresi dolanlar erişimde ve periyodik arka plan taramasında temizlenir.
- **Gerçek LRU:** kapasite dolunca en uzun süredir *erişilmeyen* girdiler atılır; sık okunan sıcak veri korunur.
- Cache'i pipeline'da **Retry/CB'nin dışına** koyun: isabet varsa hiçbir strateji çalışmaz.

**Süre türleri ve dış depo (1.2.0, Polly.Caching.Memory / Polly.Caching.Distributed eşdeğeri):**

```csharp
.AddCache(o =>
{
    o.SlidingExpiration = true;                                  // her isabette süre yeniden başlar (sık okunan yaşar)
    o.TtlGenerator = (ctx, sonuc) => sonuc is Kur k && k.Canli   // sonuca göre süre; sıfır → önbelleğe alma
        ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(10);
    o.Store = sp.GetRequiredService<IAegisCacheStore>();         // dağıtık depo (aşağıda); null → bellek içi
    o.OnCacheError = (key, ex) => log.LogWarning(ex, "Önbellek deposu hatası: {Key}", key);
})
```

Dağıtık depo (`Aegis.Resilience.Extensions.Caching`) Redis, SQL Server, NCache gibi her `IDistributedCache` üzerinde çalışır:

```csharp
builder.Services.AddStackExchangeRedisCache(o => o.Configuration = "redis:6379");          // ya da başka bir IDistributedCache
builder.Services.AddAegisDistributedCacheStore(
    new SystemTextJsonCacheSerializer(UygulamaJsonContext.Default.Options), keyPrefix: "katalog:");

[JsonSerializable(typeof(Urun))]
partial class UygulamaJsonContext : JsonSerializerContext;                                 // AOT uyumlu serileştirme
```
- **Depo hatası çağrıyı düşürmez:** okuma hatası ıskalama sayılır ve işlem devam eder; yazma hatası yok sayılır. Hata
  `OnCacheError` ile bildirilir ve `aegis.callback.errors.total` sayacına (`CacheStore`) yazılır. Önbellek bir
  hızlandırıcıdır, doğruluk kaynağı değil.
- Kaynak üretici bağlamı kullanmıyorsanız `SystemTextJsonCacheSerializer.CreateReflectionBased()` kullanın; bu yol Native
  AOT'de çalışmaz ve derleyici uyarır.
- Kayan süre dağıtık depoda deponun kendi kayan süresiyle uygulanır (`DistributedCacheEntryOptions.SlidingExpiration`).
- Geçersiz kılma: `cacheStrategy.InvalidateAsync(key)` hem bellek içi hem dış depodan siler. Senkron `Invalidate(key)`
  yalnızca bellek içi depoyu siler (eski davranış).

### 4.13 Adaptive Concurrency
**Ne zaman:** Sabit bulkhead sınırı bilmiyorsanız; hedefin kapasitesine göre limit kendini ayarlasın (Netflix Gradient2).

```csharp
.AddAdaptiveConcurrency(o =>
{
    o.InitialConcurrency = 20; o.MinConcurrency = 5; o.MaxConcurrency = 200;   // Min ≤ Initial ≤ Max
    o.SmoothingFactor = 0.2;                     // RTT hareketli ortalama ağırlığı (0,1]
    o.RttJitterToleranceRatio = 1.0;             // ortalama RTT, minRTT'nin 2 katına kadar "sağlıklı"
    o.MinRttJitterToleranceMs = 20;              // mutlak taban (OS zamanlayıcı gürültüsü ~15ms)
    o.WarmupSamples = 5;                         // ilk örnekler karar vermez (JIT/bağlantı ısınması)
    o.QueueTimeout = TimeSpan.FromSeconds(2);    // varsayılan 0: kapasite doluysa anında red (2.0.0)
})
// Gözlem: ((AdaptiveConcurrencyStrategy)p.Strategies[0]).CurrentLimit / ActiveExecutions
```
- Gecikme tolerans çizgisini aşarsa limit çarpımsal daralır; sağlıklıysa +0.5/örnek büyür. Tek aykırı-hızlı örnek algoritmayı çökertmez (ardışık iki örnek onayı).
- RTT'si ~20ms altındaki yerel çağrılarda sinyal gürültüdür; orada sabit bulkhead (4.4) daha mantıklıdır.

### 4.14 Chaos Engineering
```csharp
.AddChaos(o =>
{
    o.Enabled = builder.Environment.IsStaging();     // üretimde KAPALI tutun
    o.InjectionRate = 0.1;                            // [0,1]
    o.Latency = TimeSpan.FromMilliseconds(300);       // enjekte edildiğinde ek gecikme (iptale saygılı)
    o.FaultGenerator = () => new HttpRequestException("kaos");   // null → hata fırlatılmaz
    o.ResultGenerator = ctx => sahteSonuc;            // hata yerine sahte sonuç (tip TResult ile uyumlu olmalı)
    o.BehaviorGenerator = ctx => default;             // yan etki (log, sayaç)
    o.OptionsProvider = () => ayarlar.Kaos;           // canlı kill-switch
    // 1.0.11 (Polly eşdeğeri): çağrı bazında karar + bildirim
    o.EnabledGenerator = ctx => ValueTask.FromResult(ctx.TryGetProperty<string>("Tenant", out var t) && t == "test");
    o.InjectionRateGenerator = ctx => ValueTask.FromResult(0.5);
    o.OnInjected = a => { log.LogInformation("Kaos: {Tur}", a.Kind); return default; };
})
```
Pipeline'da hedef çağrıya **en yakın** (en iç) yere koyun ki Retry/CB kaosa gerçek hata gibi tepki versin.

**Ayrık kaos stratejileri (1.0.12, Polly/Simmy eşdeğeri):** Her biri kendi oranıyla ayrı bir stratejidir.
`o => ...` ile üretici ve bildirimler de verilebilir:

```csharp
.AddChaosFault(0.05, () => new HttpRequestException("kaos"))
.AddChaosLatency(0.10, TimeSpan.FromMilliseconds(500))
.AddChaosOutcome(0.02, ctx => SahteYanit())
.AddChaosBehavior(0.01, ctx => BellekBaskisiUygula())
```

**Ağırlıklı sonuç üretici (1.1.0, Polly `OutcomeGenerator` eşdeğeri):** Enjeksiyon olduğunda hangi sonucun
üretileceği ağırlığa göre seçilir (ağırlık varsayılanı 100):

```csharp
.AddChaosOutcome(0.05, new ChaosOutcomeGenerator()
    .AddException<TimeoutException>(weight: 60)                                    // %60 zaman aşımı
    .AddException(ctx => new HttpRequestException("bağlantı koptu"), weight: 30)  // %30 ağ hatası
    .AddResult(ctx => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), weight: 10))
```

**Çağrı başına gecikme ve koşullu hata (1.3.0, Polly 8.6–8.8 eşdeğeri):**
- `LatencyGenerator` çağrı başına gecikme verir ve ayarlanırsa `Latency` yerine kullanılır. Sıfır ya da negatif değer o
  çağrıda gecikme yok demektir.
- `FaultGenerator` null dönerse o çağrıda hata enjekte edilmez.
- Boş `ChaosOutcomeGenerator` çağrıyı aynen geçirir.

```csharp
.AddChaos(o =>
{
    o.Enabled = true;
    o.LatencyGenerator = ctx => ValueTask.FromResult(TimeSpan.FromMilliseconds(Random.Shared.Next(50, 500)));
    o.FaultGenerator = () => DateTime.UtcNow.Minute % 2 == 0 ? new TimeoutException() : null!; // null: o çağrıda hata yok
})
```

### 4.15 Standard Resilience (5-in-1)
Mikroservis çağrıları için önerilen dizilim tek satırda:

```csharp
.AddStandardResilience(
    totalTimeout: TimeSpan.FromSeconds(30),   // dış: retry'lar dahil toplam
    maxConcurrency: 100,                      // bulkhead
    retryAttempts: 3,                         // üstel + jitter
    attemptTimeout: TimeSpan.FromSeconds(10)) // iç: tek deneme
// Sıra: Timeout → ConcurrencyLimiter → Retry → CircuitBreaker → Timeout(attempt)
```

---

## 5. İleri seviye

### 5.1 Result-based handling (istisnasız retry/CB)
Hata fırlatmayan ama "kötü sonuç" dönen kodlar için:

```csharp
.AddRetry(o => o.ShouldHandleResult = r => r is null or "" )
.AddCircuitBreaker(o => o.ShouldHandleResult = r => r is HttpResponseMessage { StatusCode: HttpStatusCode.ServiceUnavailable })
```
`ShouldHandleResult` `object?` alır — birden fazla sonuç tipiyle aynı pipeline paylaşılabilir (devre durumu tipler arası ortaktır).

**Bağlam farkındalıklı, async koşullar (1.0.10, Polly `ShouldHandle` + `PredicateArguments` eşdeğeri):**
`ShouldHandleOutcome` hem istisnayı hem sonucu, bağlamı ve deneme numarasını görür; ayarlanırsa `ShouldHandle` ve
`ShouldHandleResult` yerine kullanılır. İptal ve açık devre reddi her durumda ele alınmaz.

```csharp
var gecici = new AegisPredicateBuilder()
    .Handle<HttpRequestException>()
    .Handle<TimeoutException>(ex => ex.Message.Contains("db"))
    .HandleInner<SocketException>()                              // InnerException / AggregateException içinde
    .HandleResult<HttpResponseMessage>(r => (int)r.StatusCode >= 500);

.AddRetry(o => o.ShouldHandleOutcome = gecici)
.AddCircuitBreaker(o => o.ShouldHandleOutcome = gecici)          // aynı koşul birden çok stratejide

// Bağlam ve deneme numarasına bakan async koşul:
.AddRetry(o => o.ShouldHandleOutcome = AegisPredicate.Create(async a =>
    a.Exception is not null && a.AttemptNumber < 2 && await izinVarMi(a.Context.OperationKey)))
```
`HandleResult<int>(...)` gibi değer tipi kuralları sonucu **kutulamaz** (Polly'de bu yalnızca tipli pipeline ile mümkün).

### 5.2 Dinamik generator'lar (istek bütçesi)
```csharp
.AddTimeout(TimeSpan.FromSeconds(30), o => o.TimeoutGenerator = ctx =>
    ctx.TryGetProperty<DateTimeOffset>("Deadline", out var d) ? d - DateTimeOffset.UtcNow : null)
// Kalan bütçe ≤ 0 ise istek başlatılmadan AegisTimeoutException
```
Benzer: `RetryOptions.DelayGenerator`, `CircuitBreakerOptions.BreakDurationGenerator`.

### 5.3 Hot reload (`OptionsProvider`)
Her stratejinin seçeneklerinde `OptionsProvider` vardır; **her çağrıda** çağrılır:

```csharp
.AddRetry(o => o.OptionsProvider = DynamicAegisOptionsBridge.Create(monitor))          // IOptionsMonitor<RetryOptions>
.AddRateLimiter(new RateLimiterOptions { PermitLimit = 100, Window = 1min, OptionsProvider = () => ayarKaynagi.Guncel })
```
- Sağlayıcının döndürdüğü seçenekler **doğrulanır**; geçersizse (ör. `PermitLimit=0`) veya sağlayıcı istisna fırlatırsa **son geçerli** seçeneklerle devam edilir — hatalı yapılandırma yayını trafiği düşürmez.
- Aynı seçenek örneği dönerse doğrulama tekrarlanmaz (hızlı yol); değişiklikte **yeni** bir örnek döndürün.

### 5.3b Sahte saat ve deterministik testler (`TimeProvider`, `Randomizer`)
Polly'deki gibi tüm zaman kullanan stratejiler (Retry, Timeout — kötümser dahil, Circuit Breaker açılma süresi ve
örnekleme penceresi, Hedging gecikmesi, Rate Limiter, Sliding Window, Adaptive Concurrency, Cache, Stale Fallback,
Chaos gecikmesi) boru hattının saatini kullanır. Testte sahte saat verin, beklemeden ilerletin:

```csharp
var saat = new FakeTimeProvider();                       // Microsoft.Extensions.TimeProvider.Testing
var pipeline = new AegisPipelineBuilder("test")
    .AddRetry(o => { o.Delay = TimeSpan.FromMinutes(10); o.Randomizer = () => 0.5; })   // jitter sabit
    .AddCircuitBreaker(o => o.BreakDuration = TimeSpan.FromHours(1))
    .WithTimeProvider(saat)
    .Build();

saat.Advance(TimeSpan.FromHours(1));                     // devre anında HalfOpen'a geçer
```
`RetryOptions.Randomizer` ve `ChaosOptions.Randomizer` jitter'ı ve kaos enjeksiyonunu deterministik yapar.
Varsayılan saat `TimeProvider.System`'dir; üretimde hiçbir şey ayarlamanız gerekmez.

### 5.4 Kompozisyon
```csharp
var ortak = registry.GetPipeline("sirket-standardi");
var yerel = new AegisPipelineBuilder("rapor").AddCache(TimeSpan.FromMinutes(5)).AddPipeline(ortak).Build();
// yerel.Dispose() ortak'ı DISPOSE ETMEZ (sahiplik çağıranda)
```

### 5.5 Özel strateji yazma
```csharp
public sealed class LogStrategy(ILogger log) : IAegisStrategy
{
    public string Name => "Log";
    public async ValueTask<T> ExecuteAsync<T>(Func<AegisContext, ValueTask<T>> next, AegisContext ctx)
    {
        log.LogInformation("{Pipeline} başlıyor", ctx.PipelineName);
        try { return await next(ctx); }
        finally { log.LogInformation("{Pipeline} bitti", ctx.PipelineName); }
    }
}
// .AddStrategy(new LogStrategy(logger))
```
Kurallar: `next` **tam olarak bir kez** çağrılmalı (retry yazıyorsanız her denemede yeniden); `ctx.CancellationToken` ikame edilirse `finally`'de **geri yüklenmeli**; kaynak tutuyorsanız `IDisposable` uygulayın.

**Sıfır tahsisli hızlı yol (isteğe bağlı, 1.0.9+).** `IAegisStrategy` ile yazılan strateji her çağrıda küçük bir
closure ayırır (katman başına ~96 B) ve iç katmanın hatasını istisna olarak görür. Çoğu uygulama için bu önemsizdir.
Yerleşik stratejilerin kullandığı hızlı yolu isterseniz `AegisStrategy`'den türeyin:

```csharp
public sealed class LogStrategy(ILogger log) : AegisStrategy
{
    public override string Name => "Log";

    protected override async ValueTask<Outcome<T>> ExecuteCoreAsync<T, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<T>>> next, AegisContext ctx, TState state)
    {
        log.LogInformation("{Pipeline} başlıyor", ctx.PipelineName);
        var outcome = await next(ctx, state);           // asla fırlatmaz; hata outcome.Exception'dadır
        log.LogInformation("{Pipeline} bitti: {Ok}", ctx.PipelineName, outcome.IsSuccess);
        return outcome;                                  // başarısızlığı fırlatmak yerine döndürün
    }
}
```
`next` istisna fırlatmaz; başarısızlık `Outcome<T>.Exception` ile gelir. Kendi reddinizi
`Outcome<T>.FromException(...)` ile döndürün. İstisna yalnızca boru hattının en dışında bir kez fırlatılır.
Yanlışlıkla fırlattığınız istisnalar da katman sınırında yakalanıp sonuca çevrilir.

### 5.6 SQL Server / Azure SQL geçici hataları (1.2.0, `Aegis.Resilience.Data.SqlClient`)

Enterprise Library Transient Fault Handling (Topaz) eşdeğeri. Yeniden denenebilir SQL hatalarını tanır:

```csharp
.AddRetry(o =>
{
    o.MaxRetryAttempts = 5;
    o.BackoffType = DelayBackoffType.DecorrelatedJitter;
    o.ShouldHandleOutcome = new AegisPredicateBuilder().HandleSqlTransientErrors();  // ya da: o.ShouldHandle = AegisSqlTransientErrors.IsTransient
})
.AddCircuitBreaker(o => o.ShouldHandle = AegisSqlTransientErrors.IsTransient)       // yalnızca geçici hatalar devreyi açar
```
- **Hata listesi:** EF Core'un güncel listesiyle aynıdır (175 numara; `AegisSqlTransientErrors.ErrorNumbers`). Örnekler:
  1205 kilitlenme kurbanı, 40501 servis meşgul, 40613 veritabanı kullanılamıyor, 49918–49920 kaynak sınırı.
  `TimeoutException` da geçicidir.
- **Bilerek hariç tutulanlar:**
  - -2 (komut zaman aşımı): işlem sunucuda tamamlanmış olabilir, yeniden denemek çift yazma riski taşır.
  - 203: yalnızca iç istisna `Win32Exception` ise geçicidir.
- **İç istisna zinciri taranır:** `DbUpdateException` gibi ORM sarmalayıcılarının içindeki `SqlException` da tanınır.
- **EF Core ile:** EF'nin kendi `EnableRetryOnFailure`'ı işlem (transaction) sınırını yönetir. Aegis'i EF işleminin
  tamamını saran uygulama katmanında kullanın; ikisini aynı işlemde üst üste açmayın.

---

## 6. Dependency Injection

```csharp
// Program.cs
builder.Services.AddAegis();   // IAegisPipelineRegistry (Singleton)

builder.Services.AddAegisPipeline("odeme", (p, sp) => p
    .AddStandardResilience(retryAttempts: 2)
    .AddFallback(o => { o.ShouldHandle = _ => true; o.FallbackHandler = (_, _) => ValueTask.FromResult<object?>(OdemeSonucu.Beklemede); }));

builder.Services.AddAegisPipeline("katalog", p => p.AddCache(TimeSpan.FromMinutes(10)).AddRetry());

// Kullanım
public sealed class OdemeServisi(IAegisPipelineRegistry registry)
{
    private readonly IAegisPipeline _p = registry.GetPipeline("odeme");   // Lazy: ilk erişimde bir kez kurulur
    public Task<OdemeSonucu> OdeAsync(Siparis s, CancellationToken ct) =>
        _p.ExecuteAsync(ctx => gateway.ChargeAsync(s, ctx.CancellationToken), new AegisContext(ct)).AsTask();
}
```
- Adlar **büyük/küçük harfe duyarsız**. Bilinmeyen ad → açıklayıcı istisna; `TryGetPipeline` ile kontrol edin.
- `RegisterPipeline(name, pipeline)` ile dışarıdan kurulmuş bir pipeline eklenir; sahipliği sizde kalır (registry dispose etmez).
- Registry `IDisposable`: konteyner kapanınca **kendi kurduğu** pipeline'ları dispose eder; sonra `GetPipeline` → `ObjectDisposedException`.
- Configurator patlarsa istisna yükselir ama **önbelleklenmez**; sonraki çağrı yeniden dener (açılışta config servisi henüz hazır değilse toparlanır).

### 6.1 Anahtarlı ve dinamik boru hatları (1.0.12, Polly `AddResiliencePipeline<TKey>` eşdeğeri)

```csharp
// Statik anahtar (ör. servis + sürüm)
builder.Services.AddAegisPipeline(new UcNokta("odeme", "v2"), (b, key, sp) => b.AddRetry().AddCircuitBreaker());

// Önceden bilinmeyen anahtarlar: kiracı başına AYRI devre kesici ve kota, ilk erişimde kurulur
builder.Services.AddAegisPipelines<string>((b, kiraci, sp) =>
    b.AddCircuitBreaker().AddRateLimiter(kiraci == "vip" ? 1000 : 100, TimeSpan.FromMinutes(1)),
    maxDynamicPipelines: 10_000);     // kardinalite koruması: rastgele anahtarla bellek şişirilemez (Polly'de yok)

var pipeline = sp.GetRequiredService<IAegisPipelineProvider<string>>().GetPipeline(kiraciId);
```
DI'sız kullanım: `new AegisPipelineRegistry<TKey> { DynamicBuilder = ..., MaxDynamicPipelines = ... }`.

### 6.2 Yapılandırma değişince yeniden yükleme (1.0.12, Polly `EnableReloads` eşdeğeri)

```csharp
builder.Services.Configure<OdemeAyarlari>(builder.Configuration.GetSection("Odeme"));
builder.Services.AddAegisPipeline<OdemeAyarlari>("odeme", (b, ayar, sp) =>
    b.AddRetry(o => o.MaxRetryAttempts = ayar.RetrySayisi).AddTimeout(ayar.ZamanAsimi));
```
- `appsettings.json` değişince **tüm boru hattı** yeniden kurulur. Uçuştaki istekler eski boru hattıyla tamamlanır;
  eski boru hattı son istek bitince dispose edilir.
- Yeni yapılandırma kurulamazsa (ör. geçersiz değer) eski boru hattı çalışmaya devam eder; hata `ReloadableAegisPipeline.LastReloadError` ile okunur.
- Yeniden kurulum strateji durumunu sıfırlar (devre kesici, kota). Durumu koruyarak yalnızca değer değiştirmek için
  strateji başına `OptionsProvider` (5.3) daha uygundur.

### 6.3 Kurulum bağlamıyla boru hattı (1.3.0, Polly `AddResiliencePipeline(key, (builder, context) => ...)` eşdeğeri)

```csharp
builder.Services.Configure<OdemeAyarlari>(builder.Configuration.GetSection("Odeme"));
builder.Services.AddAegisPipelineWithContext("odeme", (b, ctx) =>
{
    var ayar = ctx.GetOptions<OdemeAyarlari>();      // adlandırılmış seçenek: ctx.GetOptions<T>("ad")
    ctx.EnableReloads<OdemeAyarlari>();              // değişince yeniden kur (birden çok tip eklenebilir)
    ctx.AddReloadToken(ozelKaynak.DegisiklikToken);  // özel yapılandırma kaynağı (iptal edilince yeniden kurar)
    var istemci = new PahaliIstemci();
    ctx.OnPipelineDisposed(istemci.Dispose);         // nesil bırakılınca kaynağı da bırak

    b.AddRetry(o => o.MaxRetryAttempts = ayar.RetrySayisi).AddTimeout(ayar.ZamanAsimi);
});
```
- Bağlam `ServiceProvider`, `PipelineName`, `GetOptions`, `EnableReloads<T>()`, `EnableReloads(IOptionsMonitor<T>)`
  (Polly 8.8), `AddReloadToken` ve `OnPipelineDisposed` sunar.
- Her yeniden kurulum yeni bir bağlamla çağrılır. Eski neslin abonelikleri ve dispose bildirimleri, nesil boşalınca
  birlikte bırakılır.
- Bağlam hiçbir yeniden kurma kaynağı veya dispose bildirimi toplamazsa düz boru hattı döner (ek maliyet yok).
- HTTP'deki `AegisHttpHandlerContext` aynı taban sınıftan türer; aynı yöntemler orada da vardır.
- Kayıt defterleri `IAsyncDisposable`'dır: `await using var kayit = new AegisPipelineRegistry();`.

---

## 7. HttpClient entegrasyonu

### 7.1 Adlandırılmış pipeline ile
```csharp
builder.Services.AddHttpClient("odeme-api", c => c.BaseAddress = new Uri("https://odeme.example.com"))
    .AddAegisResilienceHandler("odeme");        // registry'deki pipeline
// veya satır içi:
    .AddAegisResilienceHandler(p => p.AddRetry(o => o.MaxRetryAttempts = 2).AddCircuitBreaker())
// veya hazır standart:
    .AddStandardAegisHandler(totalTimeout: TimeSpan.FromSeconds(20), retryAttempts: 3);
```
Handler davranışı:
- **Geçici durum kodları** (408, 429, 500, 502, 503, 504) `HttpRequestException` (StatusCode dolu) olarak yükseltilir → Retry/CB tetiklenir. 4xx'ler (404, 400, 401…) **yanıt olarak** döner, retry edilmez.
- **İdempotency koruması:** `POST/PATCH` gibi idempotent olmayan istekler `Idempotency-Key` (veya `X-Idempotency-Key`) başlığı **yoksa asla yeniden gönderilmez** (çift ödeme yok). Handler bağlama `AegisContextKeys.SuppressAdditionalAttempts` işaretini koyar; Retry ve Hedging ek deneme **üretmez**. İlk denemenin **gerçek** sonucu döner: 5xx ise yanıtın kendisi, ağ hatasıysa asıl `HttpRequestException`. İşaret çağrı bitince bağlamdan kaldırılır. Bilinçli açmak için `allowNonIdempotentRetry: true`.
- **Gövde tekrar oynatma:** gövdeli istekler belleğe alınır, her denemede birebir aynı gövde gider (seekable olmayan akışlar dahil). `maxRequestBodySize` aşılırsa istek tek sefer gönderilir ve gerçek yanıt döner; tekrar denenmez (OOM koruması).
- Başarısız denemelerin `HttpResponseMessage`'ları dispose edilir; döndürülen edilmez.
- İstek üzerinde `request.SetAegisContext(ctx)` / `request.GetOrCreateAegisContext()` ile bağlam taşınır — tüm denemeler aynı `CorrelationId`'yi paylaşır.

### 7.1b Microsoft standart handler eşdeğeri (1.0.12)

```csharp
// Seçenek nesnesiyle — varsayılanlar Microsoft.Extensions.Http.Resilience ile aynı
.AddStandardAegisHandler(o =>
{
    o.Retry.MaxRetryAttempts = 5;
    o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(3);
    o.CircuitBreaker.ShouldHandleOutcome = new AegisPredicateBuilder().Handle<HttpRequestException>();
})

// appsettings.json'dan; dosya değişince boru hattı yeniden kurulur
.AddStandardAegisHandler(builder.Configuration.GetSection("Aegis:OdemeServisi"))
```

```json
"Aegis": { "OdemeServisi": {
  "RateLimiter":        { "MaxConcurrentExecutions": 500 },
  "TotalRequestTimeout": { "Timeout": "00:00:20" },
  "Retry":              { "MaxRetryAttempts": 3, "Delay": "00:00:01" },
  "CircuitBreaker":     { "FailureRatio": 0.2, "SamplingDuration": "00:00:30", "MinimumThroughput": 50, "BreakDuration": "00:00:10" },
  "AttemptTimeout":     { "Timeout": "00:00:05" }
} }
```
- Zincir dıştan içe: eşzamanlılık → toplam zaman aşımı → retry → devre kesici → deneme zaman aşımı.
- **Tutarlılık doğrulaması:** Kurulumda (fail-fast) iki kural denetlenir; Microsoft ile aynı. Deneme zaman aşımı toplam
  zaman aşımından küçük olmalı. Devre kesici örnekleme penceresi de deneme zaman aşımının en az iki katı olmalı.
  Geçersiz yeni yapılandırma yüklenmez; eski yapılandırma çalışmaya devam eder.
- Eski parametreli `AddStandardAegisHandler(totalTimeout:, retryAttempts:)` aynen desteklenir.

**Uç nokta başına boru hattı (1.1.0, Microsoft `SelectPipelineByAuthority` eşdeğeri):** Tek istemci birden çok sunucuya
gidiyorsa, bir sunucunun çökmesi diğerlerinin devresini açmamalıdır:

```csharp
.AddStandardAegisHandler(o =>
{
    o.SelectPipelineByAuthority();                 // şema + ana bilgisayar + port başına ayrı devre ve eşzamanlılık sınırı
    // veya: o.SelectPipelineBy(r => r.Headers.TryGetValues("X-Tenant", out var t) ? t.First() : "varsayilan");
    o.MaxPipelines = 1000;                         // kardinalite koruması: sınırı aşan yeni anahtar InvalidOperationException alır
})
```
- Boru hatları ilk istekte tembel kurulur ve yapılandırma yeniden yüklenince hepsi yeni ayarla yenilenir.
- `CircuitBreaker.StateProvider` tek devreye bağlanır; seçiciyle birlikte verilirse kurulum hata verir (fail-fast).

### 7.1c Standart hedging + yönlendirme grupları + uç nokta başına devre kesici (1.0.12)

```csharp
.AddStandardAegisHedgingHandler(o =>
{
    o.MaxHedgedAttempts = 2;                     // birincile ek 2 deneme (toplam 3)
    o.HedgingDelay = TimeSpan.FromMilliseconds(300);
    // Sıralı: 1. deneme AB bölgesi, 2. deneme ABD (gruptan ağırlığa göre uç nokta seçilir)
    o.OrderedGroups.Add(new UriEndpointGroup { Endpoints = { new() { Uri = new("https://eu.api.com") } } });
    o.OrderedGroups.Add(new UriEndpointGroup { Endpoints = { new() { Uri = new("https://us1.api.com"), Weight = 70 },
                                                             new() { Uri = new("https://us2.api.com"), Weight = 30 } } });
    // veya ağırlıklı gruplar: o.WeightedGroups + o.SelectionMode (InitialAttempt / EveryAttempt)
    o.EndpointCircuitBreaker.FailureRatio = 0.2;
})
// veya: .AddStandardAegisHedgingHandler(configuration.GetSection("Aegis:Hedging"))
```
- **Uç nokta başına devre kesici:** Her authority kendi eşzamanlılık sınırını, devre kesicisini ve deneme zaman aşımını
  alır. Çöken bölgenin devresi açılınca ona hiç istek gitmez, hedging sağlıklı gruba geçer.
- 5xx/408/429 yanıtlar sıradaki denemeyi tetikler; kaybeden yanıtlar dispose edilir; hepsi başarısızsa son yanıt döner.
- İstek yolu ve sorgusu korunur, yalnızca şema, ana bilgisayar ve port değişir. Deneme sayısı grup sayısını aşmaz.
- Idempotent olmayan istekler (`Idempotency-Key`'siz POST) hedging'e girmez; tek deneme yapılır (`AllowNonIdempotentHedging` ile açılır).
- **Yeniden yükleme (1.1.0):** `IConfigurationSection` ile kurulduğunda yapılandırma değişince gruplar, devreler ve
  zaman aşımları yeniden kurulur. Uçuştaki istekler eski ayarla tamamlanır; geçersiz yeni ayar yüklenmez.

### 7.1d Yöntem bazında yeniden deneme, son yanıt, DI bağlamlı handler, `RemoveAllAegisHandlers` (1.2.0)

Bunlar Microsoft.Extensions.Http.Resilience'ın `DisableFor`, `AddResilienceHandler(name, (builder, context) => ...)` ve
`RemoveAllResilienceHandlers` özelliklerinin karşılığıdır.

```csharp
.AddStandardAegisHandler(o =>
{
    o.DisableRetryFor(HttpMethod.Delete);       // DELETE hiç yeniden denenmez
    // o.DisableRetryForUnsafeHttpMethods();    // POST, PATCH, PUT, DELETE, CONNECT
    o.ReturnFinalResponse = true;              // denemeler tükenince son 503'ü yanıt olarak döndür (Microsoft davranışı)
})
```
- **Açık kapatma önceliklidir:** `DisableRetryFor` her kuraldan önce gelir. `Idempotency-Key` başlığı ya da
  `AllowNonIdempotentRetry` bu yöntemleri açmaz. Varsayılan Aegis koruması ayrıca sürer: `Idempotency-Key`'siz POST/PATCH
  zaten yeniden denenmez.
- **`ReturnFinalResponse`:**
  - Varsayılan `false`'tur: Aegis denemeler tükenince `HttpRequestException` (StatusCode dolu) fırlatır.
  - `true` olursa çağıran `response.StatusCode` ile karar verir. Microsoft'tan geçişte kod değişmeden çalışır.
  - Önceki denemelerin yanıtları her iki durumda da dispose edilir.
  - Devre açıkken (`BrokenCircuitException`) ve zaman aşımında yine istisna fırlatılır (Microsoft ile aynı).

**DI bağlamlı özel handler:** Boru hattını kurarken servislere, seçeneklere ve yeniden yüklemeye erişilir.

```csharp
builder.Services.AddHttpClient("odeme")
    .AddAegisResilienceHandler((pipeline, context) =>
    {
        var ayar = context.GetOptions<OdemeAyarlari>();                  // IOptionsMonitor'dan güncel değer
        pipeline.AddRetry(o => o.MaxRetryAttempts = ayar.Deneme)
                .AddCircuitBreaker(o => o.OnOpened = e => context.ServiceProvider.GetRequiredService<IAlarm>().GonderAsync(e));
        context.EnableReloads<OdemeAyarlari>();                          // ayar değişince yeniden kur
        context.DisableRetryFor(HttpMethod.Post);
        context.OnPipelineDisposed(() => { /* boru hattıyla birlikte oluşturulan kaynakları bırak */ });
    });
```
- Boru hattı servis sağlayıcı başına bir kez kurulur ve HandlerLifetime yenilemeleri arasında paylaşılır; devre durumu
  korunur. Sağlayıcı dispose edilince (uygulama kapanınca) boru hattı da dispose edilir ve `OnPipelineDisposed` çağrılır.
- **Yeniden yükleme:** Uçuştaki istekler eski nesille tamamlanır, eski nesil boşalınca bırakılır. Geçersiz yeni ayarda
  kurulum hata verirse eski nesil çalışmaya devam eder.
- DI telemetrisi (otomatik `ILogger` günlüğü, `ConfigureAegisTelemetry`) uygulanır.

**Varsayılan handler'ı tek istemciden kaldırmak:**

```csharp
builder.Services.ConfigureHttpClientDefaults(b => b.AddStandardAegisHandler(o => { }));    // tüm istemciler
builder.Services.AddHttpClient("dosya-yukleme").RemoveAllAegisHandlers();                  // bu istemci hariç
builder.Services.AddHttpClient("rapor").RemoveAllAegisHandlers().AddStandardAegisHandler(o => o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(2));
```
Bu çağrıdan önce eklenen Aegis handler'ları kaldırılır. Aegis dışı handler'lara ve sonradan eklenenlere dokunulmaz.

### 7.1e Microsoft 10.10 eşitliği: geçici hata tanımı, Retry-After, servis sağlayıcı, anahtar başına bağlam (1.3.0)

**Geçici hata tanımı.** Tüm işleyiciler tek bir tanımı kullanır: `AegisHttpTransientErrors`. Microsoft
`HttpClientResiliencePredicates` ve Polly `HandleTransientHttpError` ile aynıdır:
- tüm 5xx, 408, 429
- `HttpRequestException`, `AegisTimeoutException`
- bağlantı kurma zaman aşımı

```csharp
if (AegisHttpTransientErrors.IsTransient(response)) { ... }
o.ShouldHandleOutcome = new AegisPredicateBuilder().HandleTransientHttpErrors();   // kendi boru hattınızda aynı küme
```
- **Bağlantı kurma zaman aşımı yeniden denenir.** `SocketsHttpHandler.ConnectTimeout`, içinde `TimeoutException`
  taşıyan bir `OperationCanceledException` fırlatır.
  - Çağıran iptal etmemişse bu geçici hatadır. Denemeler tükenirse özgün istisna yükselir.
  - Çağıranın ve `HttpClient.Timeout`'un iptali asla yeniden denenmez.
- **`Retry-After`:** `o.Retry.ShouldRetryAfterHeader = false` ile sunucunun istediği bekleme yok sayılır; varsayılan
  true'dur. Bildirilen süre her zaman `o.Retry.MaxDelay` ile sınırlanır, böylece saçma değerler (ör. 2147483647 sn, 9999
  yılı) çağrıyı kilitlemez ve düşürmez.
- **Senkron `HttpClient.Send`** (.NET 5+) tüm Aegis işleyicilerinden aynı çekirdekle geçer.

**Servis sağlayıcıyla standart işleyici** (Microsoft: `.Configure((o, sp) => ...)`):

```csharp
builder.Services.AddHttpClient("odeme").AddStandardAegisHandler((o, sp) =>
{
    var log = sp.GetRequiredService<ILogger<Program>>();
    o.Retry.OnRetry = a => { log.LogWarning("yeniden deneme {N}: {Uri}", a.AttemptNumber, a.Context.GetRequestMessage()?.RequestUri); return default; };
});
```
Seçenekler kapsayıcı başına bir kez, ilk istemci oluşturulurken kurulur ve doğrulanır. `AddStandardAegisHedgingHandler`
için de aynı biçim vardır.

**Anahtar başına DI bağlamlı boru hattı** (Microsoft: `AddResilienceHandler(...).SelectPipelineByAuthority()`):

```csharp
builder.Services.AddHttpClient("katalog").AddAegisResilienceHandler(
    (pipeline, ctx) => pipeline.AddCircuitBreaker(o => o.MinimumThroughput = 20),   // ctx.InstanceName = "https://host:port"
    selectPipelineBy: AegisHttpPipelineSelectors.ByAuthority,
    maxPipelines: 100);
```
- Her hedef host kendi devresini, kotasını ve sınırını alır; bir host çökünce diğerleri etkilenmez.
- Anahtar telemetride `pipeline.instance` etiketidir.
- `maxPipelines` kardinalite korumasıdır. Kurulum hatası önbelleğe alınmaz; sonraki istek yeniden dener.
- `AegisContext.GetRequestMessage()` bildirimlerde ve koşullarda isteği verir.

### 7.2 Dinamik seçim (istek bazlı pipeline)
```csharp
.AddAegisDynamicHandler(req => req.RequestUri!.Host switch { "odeme.example.com" => "odeme", _ => "Default" })
.AddAegisHandlerByHost(fallbackPipelineName: "Default")   // host adı = pipeline adı
```

### 7.3 Çoklu uç nokta hedging
```csharp
.AddMultiEndpointHedgingHandler(o =>
{
    o.Endpoints = [new Uri("https://eu.api.example.com"), new Uri("https://us.api.example.com")];
    o.HedgingDelay = TimeSpan.FromMilliseconds(400); o.MaxHedgedAttempts = 1;
    o.ShouldHandleResult = r => !r.IsSuccessStatusCode;   // bu yanıtlar "başarısız" sayılıp diğer uca geçilir
})
```
İsteğin **host'u** değiştirilir, path korunur. Kaybeden yanıtlar dispose edilir.

### 7.4 Retry-After
`AegisResilienceHandler` 429/503 yanıtındaki `Retry-After` (delta-saniye veya HTTP tarihi) başlığını ayrıştırıp bağlama yazar; Retry stratejisi gecikmeyi **bu değerle ezer** (`MaxDelay` tavanıyla). Elle: `HttpRetryAfterHelper.TryParse(response, out var delay)` / `TryParseRaw("120", out d)`. Geçmiş tarih → 0; çöp girdi → `false`; asla istisna yok.

### 7.5 Ağırlıklı kanarya
```csharp
.AddWeightedCanaryHandler(o =>
{
    o.Endpoints = [ new WeightedEndpoint(new Uri("https://stable.example.com"), 90),
                    new WeightedEndpoint(new Uri("https://canary.example.com"), 10) ];
    o.StickySessionKeySelector = req => req.Headers.TryGetValues("X-User-Id", out var v) ? v.First() : null; // aynı kullanıcı hep aynı sürüme
})
```
Sticky anahtar deterministik hash'lenir (`WeightedCanaryHandler.ComputeDeterministicHash`); anahtar yoksa ağırlıklı rastgele.

### 7.6 Sadece gövde tekrar oynatma
Kendi handler zincirinizde yalnızca replay istiyorsanız: `.AddHttpRequestReplayHandler()` veya `HttpRequestReplayHandler.CloneRequestAsync(req)`.

---

## 8. AOP — `[AegisPolicy]`

Servis kodunda tek satır dayanıklılık kodu olmadan, **arayüz** üzerinden politika:

```csharp
public interface IOdemeGateway
{
    [AegisPolicy("odeme")]                      // ARAYÜZ metodunda/arayüzde olmalı — sınıfta DEĞİL
    Task<string> ChargeAsync(string siparisId, CancellationToken ct);
}

builder.Services.AddAegisPipeline("odeme", p => p.AddRetry(o => o.MaxRetryAttempts = 2));
builder.Services.AddAegisProxiedScoped<IOdemeGateway, OdemeGateway>();     // veya AddAegisProxiedTransient
// Elle: var proxy = AegisDispatchProxy<IOdemeGateway>.Create(target, registry, defaultPipelineName: "odeme");
```
- `CancellationToken` parametresi varsa bağlama akıtılır (iptal proxy'den geçer).
- Hedef metodun istisnası **gerçek tipiyle** yükselir (`TargetInvocationException` sarmalaması yok).
- `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>` ve senkron metotlar desteklenir. **Native AOT ile çalışmaz** (`DispatchProxy` runtime kod üretir).

---

## 9. Dağıtık Circuit Breaker (Redis)

Birden fazla pod aynı devreyi paylaşır: A'da açılan devre B'de de kapanır.

```csharp
builder.Services.AddAegisRedisStateStore("redis:6379");                 // veya (IConnectionMultiplexer)
builder.Services.AddAegisPipeline("odeme", (p, sp) => p
    .AddDistributedCircuitBreaker(sp.GetRequiredService<ICircuitBreakerStateStore>(), o =>
    {
        o.CircuitKey = "odeme-gateway";                  // Redis anahtarı; boşsa pipeline adı
        o.FailureRatio = 0.5; o.MinimumThroughput = 10;
        o.BreakDuration = TimeSpan.FromSeconds(30);
        o.StateCacheDuration = TimeSpan.FromMilliseconds(250);   // Redis'e her çağrıda gitme; yerel önbellek
    }));
```
- Sayaçlar Lua ile **atomik** güncellenir. Dağıtık pencere sabittir (tumbling); yavaş çağrı oranı yalnızca yerel devre kesicide vardır. Eşikler, olaylar ve `BreakDurationGenerator` yerel devre kesiciyle ortaktır (`CircuitBreakerOptionsBase`).
- **Açık → HalfOpen → Kapalı:** `BreakDuration` dolunca devre `HalfOpen` okunur, `Closed` okunmaz. Bu geçiş Redis'in kendi TTL'inden türetilir; pod saatleri arasındaki farktan etkilenmez. **Tüm pod'lar arasında tek probe** geçer (Redis `SET NX` kiralaması, `BreakDuration` sonunda kendiliğinden düşer). Diğer pod'lar `BrokenCircuitException` alır. Probe başarılıysa devre kapanır ve pencere sayaçları sıfırlanır. 10 dakika boyunca hiç probe gelmezse devre kendiliğinden kapanır (`RedisCircuitBreakerStateStore.HalfOpenRetention`).
- **Redis erişilemezse fail-open:** uygulama çökmez, yerel duruma ve sayaçlara düşer. Yerel `Open` durumu da `BreakDuration` sonunda `HalfOpen` olur; Redis kesintisi devreyi asla kalıcı açık bırakmaz. Probe tekilliği bu sırada pod içinde sağlanır.
- **Redis'ten önce açılan pod (Kubernetes):** `AddAegisRedisStateStore("redis:6379")` bağlantıyı **arka planda** kurar (`AbortOnConnectFail=false`, `BacklogPolicy.FailFast`). Redis hazır olana kadar hiçbir istek beklemez; depo yerel duruma düşer. Redis gelince pod yeniden başlatılmadan bağlanır. Kendi `IConnectionMultiplexer`'ınızı veriyorsanız bu iki ayarı siz yapın. Aksi halde Redis yokken istekler 500 döner veya her çağrı ~5 sn bekler.
- **Yavaş Redis:** Her Redis işlemi `DefaultOperationTimeout` (250 ms) ile sınırlıdır; aşılırsa işlem yerel duruma düşer. Böylece yavaşlayan Redis, korunan çağrılara gecikme eklemez. Uzak bölgedeki bir Redis için artırın: `AddAegisRedisStateStore("redis:6379", operationTimeout: TimeSpan.FromMilliseconds(500))`.
- **Split-brain görünürlüğü:** Paylaşılan depoya ulaşılamıyorsa (yanlış adres, kesinti, süre aşımı) `AddAegisCheck` `Degraded` raporlar. Açıklamada "pod-yerel mod" yazar ve `data`'da `{pipeline}:sharedState` anahtarı bulunur. Durum `AegisHealthCheckOptions.SharedStateUnavailableStatus` ile ayarlanır. Kendi deponuzu yazıyorsanız `ICircuitBreakerStateStore.IsAvailable`'ı uygulayın.
- Gerçek küme testleri (3 düğüm, 3 pod, Redis, pod öldürme, Redis kesintisi, rolling restart, k6 yük testi): [docs/reports/kubernetes-verification.md](reports/kubernetes-verification.md).
- Kendi deponuzu yazıyorsanız `ICircuitBreakerStateStore` belgesindeki davranış sözleşmesine uyun. `TryAcquireProbeAsync`/`ReleaseProbeAsync` varsayılan uygulamalıdır; uygulamazsanız tek probe yalnızca pod içinde sağlanır.
- Test/geliştirme için `InMemoryCircuitBreakerStateStore` (`Distributed.Abstractions`).
- Dashboard ve HealthCheck dağıtık devreyi de görür (`IObservableCircuitState`).

### 9.1 Dağıtık hız sınırlayıcı (1.2.0, AspNetCoreRateLimit.Redis / RedisRateLimiting eşdeğeri)

Kota tüm pod'lar arasında ortaktır. Örnek: ücretli bir dış API'nin "saniyede 50" sınırı 10 pod çalışırken de toplamda
aşılmaz.

```csharp
builder.Services.AddAegisRedisRateLimitStore("redis:6379");                  // veya (IConnectionMultiplexer)
builder.Services.AddAegisPipeline("stripe", (p, sp) => p
    .AddDistributedRateLimiter(sp.GetRequiredService<IDistributedRateLimitStore>(), o =>
    {
        o.LimiterKey = "stripe-api";                          // tüm pod'larda aynı anahtar = ortak kota
        o.Algorithm = DistributedRateLimitAlgorithm.TokenBucket;
        o.PermitLimit = 50;                                   // kova kapasitesi (patlama)
        o.Window = TimeSpan.FromSeconds(1);                   // dolum periyodu
        o.TokensPerPeriod = 50;                               // saniyede 50 jeton
        o.PartitionKeySelector = ctx => ctx.OperationKey;     // (isteğe bağlı) kiracı / kullanıcı başına ayrı kota
        o.OnRejected = a => { log.LogWarning("Kota: {Bekle}", a.RetryAfter); return default; };
    }));
```

| Algoritma | Davranış | Ne zaman |
|---|---|---|
| `TokenBucket` (varsayılan) | Kapasite kadar patlama, ortalama `TokensPerPeriod / Window` | Dış API kotaları |
| `FixedWindow` | Her pencerede en fazla `PermitLimit`; en ucuz | Basit günlük/saatlik kota |
| `SlidingWindow` | İki pencereli ağırlıklı tahmin; pencere sınırında iki kat patlamayı önler | Adil, düzgün sınır |

- **Red:** Diğer sınırlayıcılarla aynıdır: `RateLimitRejectedException` (`RetryAfter` dolu), `OnRejected` ve
  `aegis.ratelimit.rejections.total`.
- **Atomiklik:** Her karar tek bir Lua betiğiyle atomiktir. Saat olarak Redis'in `TIME` komutu kullanılır; pod saatleri
  arasındaki fark sonucu etkilemez.
- **Redis erişilemezse:** Sınırsız geçiş yerine aynı kuralla **pod başına yerel sınır** uygulanır; koruma sürer. Redis
  dönünce ortak kota kendiliğinden devam eder. Health check bu durumu `Degraded` ("pod-yerel mod") gösterir.
- Tek sunucu ve testler için `InMemoryDistributedRateLimitStore` (`Distributed.Abstractions`). Algoritmalar Redis Lua
  betikleriyle birebir aynıdır.

---

## 10. Health Check, Dashboard, Telemetri

```csharp
builder.Services.AddHealthChecks().AddAegisCheck(
    failureStatus: null, tags: ["ready"],
    configureOptions: o => o.OpenCircuitStatus = HealthStatus.Degraded);   // varsayılan Degraded — Unhealthy DEĞİL

app.MapHealthChecks("/health");
app.MapAegisDashboard("/aegis",
    authorizationPolicy: "OpsReadOnly",          // panoyu görme
    actionAuthorizationPolicy: "OpsAdmin");      // Isolate / Reset butonları
app.MapAegisStatus("/aegis/status");             // yalnızca JSON istiyorsanız
```
- **Health:** herhangi bir devre `Open`/`Isolated` ise `Degraded` (K8s restart döngüsüne girmez). Ağa çıkmaz, <5ms.
- **Birden çok kontrol:** her `AddAegisCheck` kendi `configureOptions` ayarını taşır. Örnek: canlılık `Degraded`, hazır olma
  `.AddAegisCheck("aegis_ready", tags: ["ready"], configureOptions: o => o.OpenCircuitStatus = HealthStatus.Unhealthy)`.
  Varsayılan etiketler `resilience`, `aegis`, `ready`'dir; uç noktaları ayırırken etikete değil kontrol adına göre filtreleyin.
- **Dashboard güvenliği:** politika verilmezse pano ve durum JSON'u **anonim** okunur, ama Isolate/Reset **yalnızca yerel
  makineden** (loopback) kabul edilir; uzak istemci 403 alır. Uzaktan müdahale için `actionAuthorizationPolicy` verin. Isolate/Reset POST'ları ayrıca `X-Aegis-Action: true` başlığı ister (CSRF koruması); pano bunu kendisi gönderir. Kullanıcı verisi HTML-escape edilir.
- **Telemetri:** `System.Diagnostics.Metrics` — Meter adı `Aegis`. Sayaçlar (`pipeline` etiketiyle):
  `aegis.executions.total`, `aegis.execution.duration.ms`, `aegis.retry.attempts.total`, `aegis.timeout.total`,
  `aegis.circuitbreaker.state_changes.total`, `aegis.ratelimit.rejections.total`, `aegis.chaos.injections.total`,
  `aegis.cache.hits.total`, `aegis.cache.misses.total`, `aegis.callback.errors.total`. `aegis.circuitbreaker.state_changes.total` ek olarak `state` etiketi taşır (`closed`/`open`/`half_open`/`isolated`); "devre açıldı" alarmı için `state="open"` kullanın (yutulan olay/üretici hataları; `callback` ve `exception` etiketli). OpenTelemetry ile: `.AddMeter("Aegis")`. Konsolda: `dotnet-counters monitor -n <uygulama> --counters Aegis`.

### 10.1 Standart telemetri, ILogger ve dinleyiciler (1.0.11, Polly eşdeğeri)

Yukarıdaki sayaçlara ek olarak Polly / OpenTelemetry resilience semantiğiyle **aynı etiket adlarını** taşıyan üç metrik
yayınlanır. Polly için kurulmuş panolar ve alarmlar küçük bir ad değişikliğiyle taşınabilir:

| Metrik | Polly karşılığı | Etiketler |
|---|---|---|
| `aegis.strategy.events` | `resilience.polly.strategy.events` | `event.name`, `event.severity`, `pipeline.name`, `strategy.name`, `operation.key`, `exception.type` |
| `aegis.strategy.attempt.duration` (ms) | `resilience.polly.strategy.attempt.duration` | + `attempt.number`, `attempt.handled` |
| `aegis.pipeline.duration` (ms) | `resilience.polly.pipeline.duration` | `pipeline.name`, `operation.key`, `exception.type` |

Olay adları Polly ile aynıdır: `OnRetry`, `ExecutionAttempt`, `OnTimeout`, `OnCircuitOpened/Closed/HalfOpened`,
`OnRateLimiterRejected`, `OnHedging`, `OnFallback`, `Chaos.OnFault/OnLatency/OnOutcome/OnBehavior`, `PipelineExecuted`.
Aegis'e özgü olanlar: `OnCacheHit`, `OnCacheMiss`, `OnStaleFallback`, `OnRequestCollapsed`.

**DI ile otomatik günlük:** `AddAegisPipeline` ile kurulan boru hatları, DI'da `ILoggerFactory` varsa olayları kendiliğinden
`Aegis` kategorisine loglar (Polly `AddResiliencePipeline` ile aynı). Mesaj biçimi ve EventId'ler Polly ile aynıdır.
Polly'den farkı şu: sorunsuz denemeler `Debug` düzeyinde yazılır (Polly `Information` kullanır), bu yüzden her istek log üretmez.

```csharp
builder.Services.ConfigureAegisTelemetry(t =>
{
    t.EnableLogging = true;                                          // false: otomatik günlüğü kapat
    t.ResultFormatter = (ctx, sonuc) => sonuc is Kullanici ? "***" : sonuc;   // hassas veriyi maskele
    t.Configure.Add((o, sp) => o.MeteringEnrichers.Add(e => e.Tags.Add(new("tenant", Kiraci(e.TelemetryEvent.Context)))));
});
```

**Builder ile (DI'sız):**

```csharp
var pipeline = new AegisPipelineBuilder("odeme")
    .AddRetry(o => o.MaxRetryAttempts = 3)
    .WithTelemetry(t =>
    {
        t.Listeners.Add(new AegisLoggingTelemetryListener(loggerFactory));   // ILogger
        t.Listeners.Add(new BenimDinleyicim());                              // AegisTelemetryListener'dan türetin
        t.SeverityProvider = e => e.EventName == AegisEventNames.OnRetry ? AegisEventSeverity.Information : e.Severity;
    })
    .Build();

await pipeline.ExecuteAsync(IsYap, new AegisContext { OperationKey = "SiparisGetir" });   // operation.key etiketi
```
- Dinleyici ya da metrik dinleyicisi bağlı değilse olay nesnesi hiç oluşturulmaz; başarı yolu **0 B** kalır (testle korunuyor).
- Dinleyici, zenginleştirici veya önem sağlayıcı fırlatırsa çağrı etkilenmez; hata `aegis.callback.errors.total` sayacına yazılır.

**Örnek adı, giriş olayı, red kaynağı (1.3.0, Polly eşdeğeri):**
- `new AegisPipelineBuilder("odeme").WithInstanceName("kiraci-42")` aynı adlı boru hattının örneklerini ayırt eder.
  Olaylarda `AegisTelemetryEvent.PipelineInstance`, metriklerde `pipeline.instance` etiketi olarak görünür.
- `PipelineExecuting` olayı yalnızca dinleyicilere gider (Debug); Polly gibi metrik yazılmaz.
- Redler (`BrokenCircuitException`, `AegisTimeoutException`, `RateLimitRejectedException`) `ex.TelemetrySource` taşır:
  `PipelineName`, `PipelineInstanceName`, `StrategyName`.
- `RateLimiterRejectedArguments.Metadata`, System.Threading.RateLimiting köprüsünde kiralamanın tüm meta verisidir.
- Fırlatan dinleyici, olay geri çağrısı veya zenginleştirici çağrıyı asla düşürmez; hata `aegis.callback.errors.total`
  ile sayılır. Polly'de fırlatan dinleyici her çağrıyı düşürür (batırma testi, `docs/POLLY-COMPARISON.md` bölüm 12).

### 10.2 `ContinueOnCapturedContext` (1.0.11)
UI (WPF/WinForms/MAUI) veya eski ASP.NET gibi senkronizasyon bağlamı gerektiren ortamlarda:
`new AegisContext { ContinueOnCapturedContext = true }`. Böylece strateji gecikmesinden sonraki denemeler de özgün
bağlamda (ör. UI iş parçacığında) çalışır. Varsayılan `false`'tur ve sunucu uygulamaları için en hızlı seçenektir.

### 10.3 Hata özeti ve istek üst verisi etiketleri (1.1.0, `Aegis.Resilience.Extensions.Telemetry`)

Microsoft `AddResilienceEnricher` eşdeğeri. DI ile kurulan tüm boru hatlarının standart metriklerine üç etiket ekler:
`error.type` (özetlenmiş hata, ör. `HostNotFound`), `request.name` ve `request.dependency.name`.

```csharp
builder.Services.AddExceptionSummarizer(b => b.AddHttpProvider());   // error.type için özetleyici
builder.Services.AddAegisResilienceEnricher();

// Çağrı bazında üst veri: bağlama ya da HTTP isteğine iliştirilir
context.SetRequestMetadata(new RequestMetadata { RequestName = "SiparisGetir", DependencyName = "SiparisServisi" });
request.SetRequestMetadata(new RequestMetadata { RequestName = "UrunGetir", DependencyName = "StokApi" });
```
- Özetleyici kayıtlı değilse `error.type` eklenmez; diğer etiketler yine çalışır.
- Bağlamda üst veri yoksa HTTP isteğindeki, o da yoksa `IOutgoingRequestContext`'teki üst veri kullanılır.
- Birden çok çağrı güvenlidir; zenginleştirici bir kez kaydedilir.

### 10.4 İz (trace): boru hattı başına span

```csharp
// OpenTelemetry (ASP.NET Core / genel ana makine):
builder.Services.AddOpenTelemetry().WithTracing(t => t
    .AddSource(AegisTelemetry.ActivitySourceName)      // "Aegis"
    .AddOtlpExporter());

// Aspire ServiceDefaults: tek çağrı (metrik + iz + sağlık kontrolü + HTTP dayanıklılığı)
builder.AddAegisServiceDefaults();                       // AddTracing = false ile iz kapatılır
```
- **Span:** boru hattı yürütmesi başına bir tane; adı `Aegis <boru hattı adı>`, türü `Internal`.
  - Etiketler: `pipeline.name`, `pipeline.instance` (`WithInstanceName` verildiyse), `operation.key` (`AegisContext.OperationKey` verildiyse).
  - Başarısızlıkta durum `Error` ve `exception.type` etiketi. **İstisna iletisi yazılmaz**: kart numarası, kimlik bilgisi gibi
    hassas veriler izlere sızmasın.
- **Span olayları:** strateji olayları span üzerinde `ActivityEvent` olarak görünür: `OnRetry`, `ExecutionAttempt` (hata veren ya da
  ele alınan denemeler), `OnCircuitOpened`, `OnTimeout`, `OnHedging`, `OnFallback`, `OnRateLimiterRejected` ...
  Etiketleri: `strategy.name`, `event.severity`, `attempt.number`, `attempt.handled`, `exception.type`.
- **İç içe geçme:** geri çağrının içinde açılan span'lar (örneğin `HttpClient` istekleri) Aegis span'ının çocuğu olur; bu eşzamansız
  sınırlar (`await`) boyunca da korunur. Çağrı sürerken çağıranın kendi `Activity.Current` değeri değişmez.
- **Örnekleme:** OpenTelemetry örnekleyicisi uygulanır. Örneklenmeyen çağrıda span oluşmaz ve strateji olayları için nesne ayrılmaz.
- **Maliyet:** dinleyici yoksa tek bir `HasListeners()` denetimi (ölçülebilir maliyet yok). Dinleyici bağlı ama örnekleme kapalıysa
  standart zincirde ~%10, tam kayıtta ~0,55 µs ve çağrı başına ~0,5 KB ek (bkz. `docs/BENCHMARKS.md`).
- **Sınır:** boru hattına girmeden önce iptal edilmiş bir çağrı hiçbir strateji çalıştırmadığı için span üretmez.
- **Collector doğrulaması:** gerçek bir OpenTelemetry Collector'a gRPC ve HTTP/protobuf ile iz gönderimi native AOT dahil
  denenmiştir (`docs/TEST-INFRASTRUCTURE.md` bölüm 6.1).

---

## 11. Doğrulama kuralları (fail-fast)

Geçersiz yapılandırma **kurulumda** (`Build()`/kurucu) açıklayıcı `ArgumentOutOfRangeException` verir; sessizce düzeltilmez. Mesajda seçenek sınıfı ve özellik adı yazar (`RateLimiterOptions.PermitLimit en az 1 olmalıdır (verilen: 0)`).

| Alan | Kural |
|---|---|
| `MaxRetryAttempts` | ≥ 0 |
| `Delay`, `MaxDelay`, `QueueTimeout`, `Latency`, `StateCacheDuration` | ≥ 0 |
| `Window`, `SamplingDuration`, `BreakDuration`, `SlowCallDurationThreshold` | > 0 |
| `Timeout` | > 0 **veya** `Timeout.InfiniteTimeSpan` |
| `HedgingDelay` | ≥ 0 **veya** `Timeout.InfiniteTimeSpan` |
| `PermitLimit`, `SegmentsPerWindow`, `MaxConcurrentExecutions`, `MaxHedgedAttempts`, `MaxEntries`, `MaxPartitions`, `MinimumThroughput`, `MinConcurrency` | ≥ 1 |
| `FailureRatio`, `SlowCallRateThreshold`, `SmoothingFactor` | (0, 1] |
| `InjectionRate` | [0, 1] |
| Adaptive | `MinConcurrency ≤ InitialConcurrency ≤ MaxConcurrency` |
| Pipeline adı | boş/whitespace olamaz |

Canlı (`OptionsProvider`) seçenekler için aynı kurallar; ihlalde son geçerli seçeneklerle devam edilir (bölüm 5.3).

---

## 12. Sık yapılan hatalar

| Hata | Sonuç | Doğrusu |
|---|---|---|
| Callback'te `ctx.CancellationToken` yerine dıştaki token'ı kullanmak | Timeout işe yaramaz, iptal akmaz | Her zaman `ctx.CancellationToken` |
| Pipeline'ı istek başına kurmak | Devre kesici hiç açılmaz (her istek yeni durum), bellek/timer sızar | Singleton / registry |
| Aynı builder'ı iki kez `Build()` | `InvalidOperationException` | Her pipeline için yeni builder |
| Retry'ı Timeout'un **içine** koymak | Toplam süre sınırsız | `AddStandardResilience` sırası |
| `[AegisPolicy]`'yi sınıfa koymak | Politika uygulanmaz | Arayüze koy |
| `TimeSpan.FromTicks(500)` yazmak | Kurulumda hata (0 sanılır) — iyi ki | `FromMilliseconds` |
| Hedging'i POST'a uygulamak | Çift yazma | Yalnızca idempotent işlemler |
| Fallback'i içe koymak | Dıştaki stratejilerin hatası yakalanmaz | En dışa |
| Bağlamı callback dışında saklamak | Havuza dönmüş nesne (başkasının bağlamı) | Kendi `new AegisContext()`'inizi verin |
| Dashboard'u politika olmadan yayınlamak | Herkes panoyu görür | `authorizationPolicy` ver |

---

## 13. Polly'den ve diğer kütüphanelerden geçiş

| Polly v8 / Microsoft | Aegis |
|---|---|
| `new ResiliencePipelineBuilder().AddRetry(new RetryStrategyOptions{...}).Build()` | `new AegisPipelineBuilder("ad").AddRetry(o => ...).Build()` |
| `pipeline.ExecuteAsync(async ct => ..., ct)` | Aynı biçim: `pipeline.ExecuteAsync(async ct => ..., ct)` (ayrıca bağlamlı, `TState`, senkron, `ExecuteOutcomeAsync`) |
| `ResilienceContext` / `ResiliencePropertyKey<T>` | `AegisContext` / `AegisPropertyKey<T>` |
| `ShouldHandle = new PredicateBuilder().Handle<X>().HandleResult(...)` | `ShouldHandleOutcome = new AegisPredicateBuilder().Handle<X>().HandleResult(...)` |
| `TimeoutRejectedException` / `BrokenCircuitException` / `RateLimiterRejectedException` | `AegisTimeoutException` / `BrokenCircuitException` / `RateLimitRejectedException` |
| `AddResiliencePipeline("ad", ...)` + `ResiliencePipelineProvider<string>` | `AddAegisPipeline("ad", ...)` + `IAegisPipelineRegistry` |
| `AddResiliencePipeline<TKey>` / `EnableReloads<TOptions>` | `AddAegisPipeline<TKey>` / `AddAegisPipeline<TOptions>` |
| `CircuitBreakerManualControl` / `CircuitBreakerStateProvider` | Aynı adlar (yerel + dağıtık devrede) |
| `AddStandardResilienceHandler()` / `AddStandardHedgingHandler()` | `AddStandardAegisHandler()` / `AddStandardAegisHedgingHandler()` |
| `AddResilienceHandler(name, (b, ctx) => ...)` | `AddAegisResilienceHandler((b, ctx) => ...)` |
| `DisableFor(...)` / `RemoveAllResilienceHandlers()` | `DisableRetryFor(...)` / `RemoveAllAegisHandlers()` |
| `AddResilienceEnricher()` | `AddAegisResilienceEnricher()` (`Extensions.Telemetry`) |
| `Polly.Testing`: `GetPipelineDescriptor()` | `Aegis.Resilience.Testing`: `GetPipelineDescriptor()` |

| Polly v7 ve ekosistem | Aegis |
|---|---|
| `Policy.Handle<X>().CircuitBreakerAsync(5, süre)` (art arda hata) | `AddCircuitBreaker(o => { o.ConsecutiveFailureThreshold = 5; o.BreakDuration = süre; })` |
| `Policy.CacheAsync(provider, Ttl)` / `SlidingTtl` / `ResultTtl` | `AddCache(o => { o.Ttl = ...; o.SlidingExpiration = true; o.TtlGenerator = ...; })` |
| Polly.Caching.Distributed | `o.Store = new DistributedCacheStore(...)` (`Extensions.Caching`) |
| Polly.Contrib.WaitAndRetry `DecorrelatedJitterBackoffV2` | `o.BackoffType = DelayBackoffType.DecorrelatedJitter` |
| Polly.Contrib.Simmy | `AddChaosFault` / `AddChaosLatency` / `AddChaosOutcome` / `AddChaosBehavior` |
| Polly.Contrib.DuplicateRequestCollapser | `AddRequestCollapser` |
| Enterprise Library TFH / `SqlDatabaseTransientErrorDetectionStrategy` | `HandleSqlTransientErrors()` (`Data.SqlClient`) |
| AspNetCoreRateLimit (`IpRateLimiting`, `ClientRateLimiting`) | `AddAegisInboundRateLimiting` + `UseAegisInboundRateLimiting` (`AspNetCore`) |
| AspNetCoreRateLimit.Redis / RedisRateLimiting | `AddAegisRedisRateLimitStore` (+ `AddDistributedRateLimiter`) |
| Yalnızca Aegis'te | Stale-While-Revalidate, Adaptive Concurrency, Weighted Canary, Multi-Endpoint Hedging, AOP, dağıtık devre kesici, pano, uç nokta başına gelen istek boru hattı |

Davranış farkları:
- `TimeoutGenerator` ≤0 döndürürse Polly zaman aşımını **kapatır**, Aegis **bütçe tükendi** sayar.
- Aegis geçersiz canlı seçenekleri son geçerliyle sürdürür; Polly reload'ı reddeder.
- Devre kesici olay hataları Aegis'te yutulur ve sayaca yazılır; çağıranın sonucu değişmez.
- Request Collapser'da anahtar seçici zorunludur.

---

## 14. Test yazma

```csharp
// Zamanlamaya bağımlı olmayın: kısa süreler + deterministik callback'ler
var p = new AegisPipelineBuilder("t").AddCircuitBreaker(o => { o.MinimumThroughput = 1; o.BreakDuration = TimeSpan.FromMilliseconds(100); }).Build();
await Assert.ThrowsAsync<InvalidOperationException>(() => p.ExecuteAsync<int>(_ => throw new InvalidOperationException()).AsTask());
await Assert.ThrowsAsync<BrokenCircuitException>(() => p.ExecuteAsync(_ => ValueTask.FromResult(1)).AsTask());

// Dağıtık CB için Redis'siz: new InMemoryCircuitBreakerStateStore()
// Adaptif limiti deterministik beslemek için (InternalsVisibleTo ile test projesinde): strategy.RecordSampleForTesting(rttMs)
// Kaos: InjectionRate = 1.0 → her zaman; Enabled = false → asla
```
**Boru hattı tanımlayıcıları (1.0.12, `Aegis.Resilience.Testing`, Polly.Testing eşdeğeri):** DI ile kurulan boru hattının
doğru yapılandırıldığını birim testinde doğrulayın:

```csharp
using Aegis.Resilience.Testing;

var d = registry.GetPipeline("odeme").GetPipelineDescriptor();
Assert.Equal(["Timeout", "Retry", "CircuitBreaker"], d.Strategies.Select(s => s.Name));   // AddPipeline iç içe düzleştirilir
Assert.Equal(3, d.GetOptions<RetryOptions>().MaxRetryAttempts);
Assert.True(d.IsReloadable);
```

**Tipli boru hattı (1.0.12, Polly `ResiliencePipeline<T>` eşdeğeri):** `builder.Build<Siparis>()`, yalnızca `Siparis`
dönen işlemleri kabul eden bir `IAegisPipeline<Siparis>` üretir; yanlış tip derleme anında yakalanır. Mevcut boru hattı için
`pipeline.AsTyped<T>()` kullanılır. Aynı sıfır tahsisli çekirdeği kullanır.

Kütüphanenin kendi test paketi (`tests/Aegis.Tests`, 617 test × net8/9/10; ayrıca .NET Framework 4.8 üzerinde koşan `tests/Aegis.CompatibilityTests`) örnek olarak okunabilir: Polly parite senaryoları, düşmanca stres testleri, gerçek Redis entegrasyonu (`docker run -d -p 6379:6379 redis:7-alpine`).

---

## 15. Platform notları (.NET Framework, Native AOT, strong naming)

### 15.1 .NET Framework 4.6.2+ ve netstandard2.0

Paketler `netstandard2.0` ve `net462` hedeflerini de içerir; .NET Framework 4.6.2+, Mono ve Unity gibi netstandard
platformlarında aynı API ile çalışır. Yalnızca .NET 8+ hedefleyen beş paket var: Dashboard, AspNetCore, Grpc.AspNetCore ve Extensions.Aspire (ASP.NET Core'a bağlı), Extensions.Telemetry (dayandığı Microsoft telemetri paketleri .NET Framework'ü desteklemiyor). `Aegis.Resilience.WebApi` yalnızca .NET Framework'tedir.

- Kaynak kod tüm hedeflerde aynıdır. Eksik BCL API'leri dahili polyfill'lerle ve resmi `Microsoft.Bcl.TimeProvider`
  paketiyle karşılanır. Sahte saat (`FakeTimeProvider`) .NET Framework'te de çalışır.
- .NET 8+'da `IAegisPipeline` üzerindeki ek çalıştırma biçimleri (durumlu, token'lı, senkron, `ExecuteOutcomeAsync`)
  varsayılan arayüz üyesidir. .NET Framework varsayılan arayüz üyesini desteklemediği için bu biçimler orada aynı
  imzalı genişletme metodudur. Çağıran kod aynen derlenir; `using Aegis.Resilience.Core.Abstractions;` yeterlidir.
- HTTP: .NET Framework'te bağlam `HttpRequestMessage.Properties` üzerinden taşınır (.NET 5+'da `Options`).
  `request.GetOrCreateAegisContext()` her iki yolda aynıdır.
- Davranış farkları yalnızca platformdan gelir:
  - `CancellationTokenSource.TryReset` olmadığı için zaman aşımı CTS'leri havuzlanmaz, dispose edilir.
  - `HttpRequestException` durum kodu taşımaz (.NET 5+ `StatusCode`); kod mesajdadır.

Doğrulama: `tests/Aegis.CompatibilityTests` gerçek .NET Framework 4.8'de koşar. Yalnızca Windows'ta çalışır:

```cmd
dotnet test tests\Aegis.CompatibilityTests -c Release
```

### 15.2 Native AOT ve trimming

.NET 8+ hedefleri `IsAotCompatible` ile derlenir; trim ve AOT analizörlerinin uyarıları derlemeyi kırar. AOT
uygulamasında Aegis'i ek ayar olmadan kullanabilirsiniz. İstisnalar açıkça işaretlidir:

| API | AOT durumu |
|---|---|
| Tüm stratejiler, DI, HTTP handler'ları, telemetri, dağıtık devre ve hız sınırlayıcı, Dashboard, AspNetCore | ✅ Uyumlu |
| `DistributedCacheStore` + `SystemTextJsonCacheSerializer(JsonSerializerContext)` | ✅ Kaynak üreticili JSON (yansıma yok) |
| `SystemTextJsonCacheSerializer.CreateReflectionBased()` | ⚠️ `[RequiresDynamicCode]`: AOT'ta kaynak üreticili bağlam kullanın |
| `AddStandardAegisHandler(IConfigurationSection)` | ✅ Kaynak üreteciyle bağlanır (yansıma yok) |
| `AddAegisProxiedScoped/Transient/Singleton` / `AegisDispatchProxy<T>.Create` (DispatchProxy AOP) | ⚠️ `[RequiresDynamicCode]`: derleyici uyarır; AOT'ta dekoratör ya da doğrudan `ExecuteAsync` kullanın |

Doğrulama: `tests/Aegis.AotSmokeTest`, .NET çalışma zamanı içermeyen imajda koşar (bkz.
`docs/TEST-INFRASTRUCTURE.md`).

### 15.3 Strong naming

Tüm derlemeler `Aegis.snk` ile imzalıdır (public key token `607bc7b3658f794d`). Bu yüzden strong name zorunlu
kurumsal ortamlarda ve imzalı projelerden doğrudan referanslanabilir. Anahtar depodadır; bu, açık kaynak projelerde
standart uygulamadır. İmza bir kimlik doğrulaması değil, derleme kimliğidir.

### 15.4 Genel API sözleşmesi

Her paketin genel API'si `src/<Paket>/PublicAPI/PublicAPI.Shipped.txt` dosyasında tutulur
(`Microsoft.CodeAnalysis.PublicApiAnalyzers`). Genel bir üye eklemek, değiştirmek ya da silmek bu dosyada görünür
olmak zorundadır; aksi halde derleme kırılır. Kazara kırıcı değişiklik bu yüzden yayına çıkamaz. Yeni API önce
`PublicAPI.Unshipped.txt` dosyasına yazılır, yayında `Shipped` dosyasına taşınır. Core'da yalnızca hedefe özgü farklar
`PublicAPI/modern` ve `PublicAPI/legacy` klasörlerindedir.

---

## 16. Sunucu tarafı koruma (ASP.NET Core)

`Aegis.Resilience.AspNetCore` paketi (.NET 8+) gelen istekleri korur. İki parçası var: hız sınırlama (AspNetCoreRateLimit /
WebApiThrottle eşdeğeri) ve uç nokta başına Aegis boru hattı.

### 16.1 Gelen istek hız sınırlama

```csharp
builder.Services.AddAegisInboundRateLimiting(builder.Configuration.GetSection("Kota"), o =>
{
    o.PartitionByHeader("X-ClientId");        // ya da PartitionByClientIp() (varsayılan) / PartitionByUser()
    o.OnRejected = (ctx, red) => ctx.Response.WriteAsJsonAsync(new { hata = "kota", bekle = red.RetryAfter?.TotalSeconds });
});
// Küme genelinde tek kota için (isteğe bağlı): builder.Services.AddAegisRedisRateLimitStore("redis:6379");

app.UseAuthentication();                       // PartitionByUser kullanılacaksa önce kimlik doğrulama
app.UseAegisInboundRateLimiting();
```

```json
"Kota": {
  "Rules": [
    { "Endpoint": "*",                  "Limit": 1000, "Period": "01:00:00" },
    { "Endpoint": "*",                  "Limit": 20,   "Period": "00:00:01" },
    { "Endpoint": "POST:/api/siparis",  "Limit": 5,    "Period": "00:01:00", "Algorithm": "SlidingWindow" }
  ],
  "IpWhitelist":       [ "10.0.0.0/8", "::1" ],
  "ClientWhitelist":   [ "ic-rapor-servisi" ],
  "EndpointWhitelist": [ "GET:/health", "GET:/metrics" ]
}
```
- **Kurallar:** Sırayla değerlendirilir. İstek, eşleşen **tüm** kuralları geçmelidir; yukarıdaki örnekte saatte 1000 ve
  saniyede 20 birlikte geçerlidir.
- **Uç nokta desenleri:** `"*"`, `"/api/siparis"`, `"GET:/api/urun/*"`, `"*:/api/*"` biçimindedir; büyük/küçük harf
  duyarsızdır. Desen açılışta bir kez derlenir.
- **Yanıt:** Red `429` ve `Retry-After` ile döner. Başarılı yanıtlara `RateLimit-Limit` / `RateLimit-Remaining`
  eklenir (IETF taslağındaki adlar). Kapatmak için `EmitRateLimitHeaders = false`, farklı kod için
  `RejectionStatusCode`.
- **Sayaç deposu:** DI'da `IDistributedRateLimitStore` yoksa bellek içi depo kullanılır (tek sunucu).
  `AddAegisRedisRateLimitStore` ile tüm örnekler tek kotayı paylaşır; Redis kesintisinde her örnek yerel sınıra düşer.
- **Yeniden yükleme:** Bölüm değişince kurallar yeniden yüklenir; yeni kurallar yeni sayaçlarla başlar. Geçersiz yeni
  yapılandırma yüklenmez; eski kurallar sürer ve uyarı loglanır. Açılıştaki geçersiz yapılandırma uygulamayı
  başlatmaz (fail-fast).
- **Proxy arkasında:** Gerçek istemci IP'si için `UseForwardedHeaders`'ı bu ara katmandan önce ekleyin.

### 16.2 Uç nokta başına boru hattı

Gelen isteğe eşzamanlılık sınırı, zaman aşımı, devre kesici ve hız sınırı uygular:

```csharp
builder.Services.AddAegisPipeline("rapor", b => b
    .AddConcurrencyLimiter(20, o => o.QueueTimeout = TimeSpan.Zero)     // aynı anda en fazla 20 ağır rapor
    .AddTimeout(TimeSpan.FromSeconds(10)));

app.UseRouting();
app.UseAegisInboundPipelines();
app.MapGet("/rapor/yillik", RaporUret).RequireAegisPipeline("rapor");  // MVC: [AegisInboundPipeline("rapor")]
```

| Aegis sonucu | HTTP yanıtı |
|---|---|
| Hız / eşzamanlılık reddi (`RateLimitRejectedException`) | `429` + `Retry-After` |
| Açık devre (`BrokenCircuitException`) | `503` |
| Zaman aşımı (`AegisTimeoutException`) | `504` |

- **Zaman aşımı gerçekten iptal eder:** Zaman aşımı uç noktanın `HttpContext.RequestAborted` token'ına aktarılır;
  uzun süren sorgu iptal edilir, arka planda boşuna çalışmaz.
- **Retry ve Hedging reddedilir:** Bu stratejileri içeren boru hattı ilk kullanımda açık bir hatayla reddedilir, iç
  içe boru hatları dahil. İsteği sunucuda yeniden çalıştırmak güvenli değildir: istek gövdesi tüketilmiş, yanıt
  yazılmış olabilir.
- Yanıt yazılmaya başlandıktan sonra oluşan red istisnası yanıtı değiştiremez; bağlantı ASP.NET Core kurallarıyla sonlanır.

## 17. gRPC (`Aegis.Resilience.Grpc`, `Aegis.Resilience.Grpc.AspNetCore`)

**Neden ayrı katman:** HTTP dayanıklılık işleyicileri (Polly, Microsoft `AddStandardResilienceHandler`, `AddStandardAegisHandler`)
gRPC'de çalışmaz. gRPC hatası `grpc-status` trailer'ındadır ve yanıt HTTP 200'dür; işleyici başarı görür. Her gRPC çağrısı da HTTP
POST'tur ve yeniden denenmez. Gövde tamponlama akışları bozar, toplam zaman aşımı uzun akışları keser. Aegis bu yüzden:
- HTTP işleyicilerinde gRPC isteğini (`application/grpc`) **dokunmadan geçirir**,
- dayanıklılığı **gRPC katmanında** (interceptor) uygular.

```csharp
dotnet add package Aegis.Resilience.Grpc              // istemci (tüm hedef çerçeveler)
dotnet add package Aegis.Resilience.Grpc.AspNetCore   // sunucu (.NET 8+)

// İstemci — Grpc.Net.ClientFactory:
services.AddGrpcClient<Siparis.SiparisClient>(o => o.Address = new Uri("https://siparis"))
    .AddStandardAegisGrpcResilience(o => o.Retry.Budget = new RetryBudget());   // isteğe bağlı: gRPC retry throttling

// İstemci — kanal:
var invoker = GrpcChannel.ForAddress(adres).Intercept(new AegisGrpcClientInterceptor(boruHatti));

// Sunucu:
services.AddGrpc(o => o.AddAegisResilience(p => p.AddConcurrencyLimiter(200).AddTimeout(TimeSpan.FromSeconds(5))));
```

**İstemci kuralları (gRPC A6 ve Microsoft "gRPC retries" belgesi):**
- **Yeniden denenen:** yalnızca `Unavailable`, Aegis'in deneme zaman aşımı ve pozitif pushback'li `ResourceExhausted`.
  İstemci hataları (`InvalidArgument`, `NotFound`, `PermissionDenied`...) ve `DeadlineExceeded` yeniden denenmez.
- **Commit kuralı:**
  - Unary çağrılarda tüm stratejiler uygulanır.
  - Sunucu akışı, ilk mesaj alınana kadar yeniden denenir; ilk mesajla commit olur, sonraki hata çağırana yükselir.
  - Commit sonrası deneme zaman aşımı akışı kesmez.
  - İstemci ve çift yönlü akış gönderilen mesajları tamponlar ve her denemede baştan oynatır (gRPC A6). Tampon
    `AegisGrpcClientOptions.MaxRetryBufferBytes` (varsayılan 1 MB) aşılınca çağrı commit olur. Çift yönlü akış ilk yanıt
    mesajıyla commit olur. `MaxRetryBufferBytes = 0` tamponlamayı kapatır (olduğu gibi geçer).
- **Uç nokta ayıklama (outlier detection, .NET 8+):** `.AddAegisGrpcOutlierDetection(o => o.ConsecutiveFailures = 5)` art arda
  sunucu hatası veren uç noktayı yük dengelemeden geçici çıkarır. Envoy varsayılanları: 5 hata, 30 sn taban, 300 sn üst sınır,
  havuzun en çok %10'u. Hepsi ayıklanırsa panik kipi: tüm uç noktalar kullanılır. Grpc.Net.Client'ın balancer'ı yalnızca taşıma
  hatasını görür; Aegis `grpc-status`'u da sayar.
- **Deadline tüm denemeleri kapsar.** Dolunca kalan yeniden denemeler atlanır ve `DeadlineExceeded` döner. Çağıran iptal ederse
  `Cancelled` döner.
- **Sunucu pushback'i (`grpc-retry-pushback-ms`):** pozitif değer bekleme süresidir, negatif değer "yeniden deneme" demektir.
  Yeniden denemelerde `grpc-previous-rpc-attempts` başlığı gönderilir.
- **Retler `RpcException`'a çevrilir:** açık devre → `Unavailable` (açık kalma süresi pushback olarak), hız sınırı →
  `ResourceExhausted`, zaman aşımı → `DeadlineExceeded`.
- **Devre kesici yalnızca sunucu tarafı hataları sayar** (`Unavailable`, `DeadlineExceeded`, `Internal`, `Unknown`,
  `ResourceExhausted`, `DataLoss`).
- **İptal çevrimi:** Grpc.Net.Client iptali `RpcException(Cancelled)` olarak fırlatır. Aegis bunu çevirir, böylece deneme zaman
  aşımı ve çağıran iptali doğru tanınır.

**Sunucu kuralları:**
- **Retler istemcinin anladığı gRPC durumuyla döner:** hız ve eşzamanlılık → `ResourceExhausted`. Bekleme süresi biliniyorsa
  (token bucket, kayan pencere, açık devre) `grpc-retry-pushback-ms` de eklenir ve istemci o kadar bekleyip yeniden dener.
  Eşzamanlılık reddinde bilinen bir süre yoktur, pushback gönderilmez: istemci hemen yeniden denemez (aşırı yüklü sunucuya
  retry fırtınası gitmez; gRPC A6). Açık devre →
  `Unavailable`, zaman aşımı → `DeadlineExceeded`.
- **Sunucu zaman aşımı servis kodunu gerçekten keser:** servise verilen `ServerCallContext.CancellationToken` boru hattının
  token'ıdır. `GetHttpContext()` çalışmaya devam eder.
- **Retry ve Hedging içeren boru hattı kurulumda reddedilir:** sunucu çağrıyı istemci adına yeniden çalıştıramaz.
- **Gelen istek hız sınırı da gRPC'yi tanır.** `UseAegisInboundRateLimiting` gRPC isteğine HTTP 429 yerine trailers-only gRPC
  yanıtı verir: `grpc-status: 8` ve pushback. HTTP 429, gRPC istemcisinde bilgisiz bir `Unavailable`'a dönüşürdü.
- **İki taraf birlikte çalışır:** Aegis sunucusu kota dolunca bekleme süresini bildirir, Aegis istemcisi o kadar bekleyip yeniden dener.