# Polly'den Aegis'e Geçiş Rehberi

Polly v8 (`Polly.Core`, `Polly.Extensions`) ve `Microsoft.Extensions.(Http.)Resilience` kullanan projeler için adım adım
geçiş. Aegis örnekleri `tests/Aegis.Tests/MigrationGuideSnippetTests.cs` içinde derlenir ve koşar; rehber kodla eşit kalır.

> **Polly v7 (`Policy.Handle<T>().WaitAndRetryAsync(...)`) kullanıyorsanız** önce bölüm 2'deki tabloya bakın: v7
> politikalarının her biri Aegis'te bir `AddXxx` stratejisidir; `PolicyWrap` yerine strateji sırası kullanılır.

## 1. Paketler

| Polly / Microsoft | Aegis |
|---|---|
| `Polly.Core` | `Aegis.Resilience.Core` |
| `Polly.Extensions` (DI + telemetri) | `Aegis.Resilience.Extensions.DependencyInjection` |
| `Polly.RateLimiting` | `Aegis.Resilience.RateLimiting` (çekirdekte kendi sınırlayıcıları da var) |
| `Polly.Testing` | `Aegis.Resilience.Testing` |
| `Microsoft.Extensions.Http.Resilience` | `Aegis.Resilience.Extensions.Http` |
| `Microsoft.Extensions.Resilience` (`AddResilienceEnricher`) | `Aegis.Resilience.Extensions.Telemetry` |
| Aspire ServiceDefaults'taki `AddStandardResilienceHandler` | `Aegis.Resilience.Extensions.Aspire` (`AddAegisServiceDefaults`) |
| `Polly.Caching.*`, `AspNetCoreRateLimit`, `WebApiThrottle` | `Aegis.Resilience.Extensions.Caching`, `Aegis.Resilience.AspNetCore`, `Aegis.Resilience.WebApi` |

Geçiş sırasında iki kütüphane aynı projede yan yana çalışabilir; servis servis taşıyın.

## 2. Kavram eşlemesi

| Polly v8 | Aegis |
|---|---|
| `ResiliencePipelineBuilder` | `AegisPipelineBuilder("ad")` |
| `ResiliencePipelineBuilder<T>` / `ResiliencePipeline<T>` | aynı builder + `.Build<T>()` → `IAegisPipeline<T>` |
| `ResiliencePipeline` | `IAegisPipeline` (`IDisposable`; Singleton yaşatın) |
| `ResilienceContext` / `ResilienceContextPool` | `AegisContext` / `AegisContextPool` |
| `ResiliencePropertyKey<T>` | `AegisPropertyKey<T>` |
| `PredicateBuilder` | `AegisPredicateBuilder` (`Handle<T>()`, `HandleResult<T>(...)`, `HandleInner<T>()`) |
| `AddRetry(new RetryStrategyOptions {...})` | `.AddRetry(o => ...)` |
| `AddCircuitBreaker(...)` | `.AddCircuitBreaker(o => ...)` |
| `AddTimeout(TimeSpan)` | `.AddTimeout(TimeSpan)` |
| `AddFallback(...)` | `.AddFallback(o => ...)` |
| `AddHedging(...)` | `.AddHedging(o => ...)` |
| `AddRateLimiter(...)` / `AddConcurrencyLimiter(...)` | `.AddRateLimiter(izin, pencere)` / `.AddConcurrencyLimiter(n)` |
| `AddChaosFault/Latency/Outcome/Behavior` | `.AddChaosFault/Latency/Outcome/Behavior` |
| `CircuitBreakerManualControl` / `CircuitBreakerStateProvider` | aynı adlar |
| `ResiliencePipelineProvider<string>.GetPipeline("ad")` | `IAegisPipelineRegistry.GetPipeline("ad")` |
| `services.AddResiliencePipeline("ad", b => ...)` | `services.AddAegisPipeline("ad", b => ...)` |
| `AddResiliencePipeline` + `context.EnableReloads(...)` | `AddAegisPipelineWithContext("ad", (b, ctx) => ...)` |

Strateji sırası Polly ile aynıdır: **ilk eklenen en dışta** çalışır.

## 3. Temel boru hattı

**Polly v8**

```csharp
var pipeline = new ResiliencePipelineBuilder()
    .AddTimeout(TimeSpan.FromSeconds(10))
    .AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts = 3,
        Delay = TimeSpan.FromMilliseconds(200),
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        ShouldHandle = new PredicateBuilder()
            .Handle<HttpRequestException>()
            .HandleResult<HttpResponseMessage>(r => r.StatusCode == HttpStatusCode.ServiceUnavailable),
        OnRetry = args =>
        {
            Console.WriteLine($"Deneme {args.AttemptNumber}, bekleme {args.RetryDelay}");
            return default;
        },
    })
    .AddCircuitBreaker(new CircuitBreakerStrategyOptions
    {
        FailureRatio = 0.5,
        SamplingDuration = TimeSpan.FromSeconds(30),
        MinimumThroughput = 10,
        BreakDuration = TimeSpan.FromSeconds(15),
        OnOpened = args => { Console.WriteLine($"Devre açıldı: {args.BreakDuration}"); return default; },
    })
    .Build();
```

**Aegis**

```csharp
var pipeline = new AegisPipelineBuilder("odeme")
    .AddTimeout(TimeSpan.FromSeconds(10))
    .AddRetry(o =>
    {
        o.MaxRetryAttempts = 3;
        o.Delay = TimeSpan.FromMilliseconds(200);
        o.BackoffType = DelayBackoffType.Exponential;
        o.UseJitter = true;
        o.ShouldHandleOutcome = new AegisPredicateBuilder()
            .Handle<HttpRequestException>()
            .HandleResult<HttpResponseMessage>(r => r.StatusCode == HttpStatusCode.ServiceUnavailable);
        o.OnRetry = args =>
        {
            Console.WriteLine($"Deneme {args.AttemptNumber}, bekleme {args.RetryDelay}");
            return default;
        };
    })
    .AddCircuitBreaker(o =>
    {
        o.FailureRatio = 0.5;
        o.SamplingDuration = TimeSpan.FromSeconds(30);
        o.MinimumThroughput = 10;
        o.BreakDuration = TimeSpan.FromSeconds(15);
        o.OnOpened = args => { Console.WriteLine($"Devre açıldı: {args.BreakDuration}"); return default; };
    })
    .Build();
```

Farklar:
- Seçenekler nesne başlatıcı yerine `o => ...` ile verilir; seçenek adları ve anlamları aynıdır (2.0.0 öncesinde
  `MaxHedgedAttempts` birincil dahil toplamdı; 2.0.0'dan itibaren Polly gibi birincile ek deneme sayısıdır).
- Koşul alanı: Polly'de `ShouldHandle` (`PredicateBuilder`), Aegis'te `ShouldHandleOutcome` (`AegisPredicateBuilder`).
  Yalnızca istisnaya bakan basit koşul için `ShouldHandle = ex => ex is HttpRequestException` de yazılabilir.
- Boru hattına bir ad verilir (`"odeme"`); metriklerde `pipeline.name` etiketi olur.
- Seçenekler kurulumda doğrulanır; geçersiz değer `Build()` anında açık mesajla istisna fırlatır.

## 4. Çalıştırma biçimleri

| Polly v8 | Aegis |
|---|---|
| `await pipeline.ExecuteAsync(async ct => ..., token)` | aynı |
| `await pipeline.ExecuteAsync(static (state, ct) => ..., state, token)` | aynı |
| `await pipeline.ExecuteOutcomeAsync(...)` | `await pipeline.ExecuteOutcomeAsync(ctx => ...)` |
| `pipeline.Execute(() => ...)` | aynı |
| `ResilienceContextPool.Shared.Get(token)` + `ExecuteAsync(ctx => ..., ctx)` + `Return` | `new AegisContext(token)` + `ExecuteAsync(ctx => ..., ctx)` |

Elle kontrol ve tipli boru hattı:

```csharp
var manual = new CircuitBreakerManualControl();
var typed = new AegisPipelineBuilder("tipli")
    .AddCircuitBreaker(o => o.ManualControl = manual)
    .Build<string>();
```

## 5. Dependency Injection ve HttpClient

**Polly / Microsoft**

```csharp
services.AddResiliencePipeline("odeme", builder => builder
    .AddRetry(new RetryStrategyOptions { MaxRetryAttempts = 3 })
    .AddTimeout(TimeSpan.FromSeconds(5)));

services.AddHttpClient("katalog").AddStandardResilienceHandler(o =>
{
    o.Retry.MaxRetryAttempts = 5;
    o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);
});

var pipeline = provider.GetRequiredService<ResiliencePipelineProvider<string>>().GetPipeline("odeme");
```

**Aegis**

```csharp
services.AddAegisPipeline("odeme", builder => builder
    .AddRetry(o => o.MaxRetryAttempts = 3)
    .AddTimeout(TimeSpan.FromSeconds(5)));

services.AddHttpClient("katalog").AddStandardAegisHandler(o =>
{
    o.Retry.MaxRetryAttempts = 5;
    o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);
});

var pipeline = provider.GetRequiredService<IAegisPipelineRegistry>().GetPipeline("odeme");
```

HTTP karşılıkları:

| Microsoft | Aegis |
|---|---|
| `AddStandardResilienceHandler` | `AddStandardAegisHandler` (aynı zincir ve varsayılanlar) |
| `AddStandardHedgingHandler` | `AddStandardAegisHedgingHandler` |
| `AddResilienceHandler("ad", (b, ctx) => ...)` | `AddAegisResilienceHandler(...)` |
| `o.Retry.DisableForUnsafeHttpMethods()` | `o.DisableRetryForUnsafeHttpMethods()` |
| `SelectPipelineByAuthority()` | `o.SelectPipelineByAuthority()` |
| `RemoveAllResilienceHandlers()` | `RemoveAllAegisHandlers()` |
| `HttpClientResiliencePredicates.IsTransient` | `AegisHttpTransientErrors` / `HandleTransientHttpErrors()` |

### .NET Aspire

ServiceDefaults projesinde:

```csharp
// Önce (Aspire şablonu):
builder.Services.ConfigureHttpClientDefaults(http =>
{
    http.AddStandardResilienceHandler();   // ← bu satırı kaldırın
    http.AddServiceDiscovery();
});

// Sonra:
builder.AddAegisServiceDefaults();          // tüm HttpClient'lar + Aegis metrikleri + sağlık kontrolü
builder.Services.ConfigureHttpClientDefaults(http => http.AddServiceDiscovery());
```

Ayarlar `appsettings.json`'daki `Aegis:Http` bölümünden okunur ve değişince yeniden kurulur
(ör. `"Aegis": { "Http": { "Retry": { "MaxRetryAttempts": 5 } } }`). Parçalar tek tek kapatılabilir:
`builder.AddAegisServiceDefaults(o => o.AddHealthCheck = false)`.

## 6. İstisnalar

| Polly | Aegis |
|---|---|
| `ExecutionRejectedException` (taban) | `AegisException` |
| `BrokenCircuitException` | `BrokenCircuitException` (aynı ad, `RetryAfter` dahil) |
| `IsolatedCircuitException` | `IsolatedCircuitException` |
| `TimeoutRejectedException` | `AegisTimeoutException` |
| `RateLimiterRejectedException` | `RateLimitRejectedException` |

`catch` bloklarında ad değişikliği gereken tek yerler `TimeoutRejectedException` ve `RateLimiterRejectedException`'dır.

## 7. Telemetri

- Metrik adları `resilience.polly.*` yerine `aegis.*`; meter adı `Aegis` (`AegisTelemetry.MeterName`).
  OpenTelemetry'de `metrics.AddMeter("Polly")` yerine `metrics.AddMeter("Aegis")` (Aspire paketinde otomatik).
- Etiket adları Polly ile aynıdır (`pipeline.name`, `strategy.name`, `operation.key`, `event.name`, `event.severity`,
  `exception.type`, `attempt.number`); mevcut panolarda yalnızca metrik adını değiştirmeniz yeterli.
- `ILogger` olay günlükleri Polly ile aynı mesaj ve `EventId`'leri kullanır.

## 8. Davranış farkları (bilerek farklı)

| Durum | Polly | Aegis |
|---|---|---|
| Olay geri çağrısı (`OnRetry`, `OnTimeout`, `OnOpened`...) istisna fırlatırsa | istisna çağrıya yayılır; fırlatan `OnHalfOpened` devreyi kilitler | istisna yutulur, `aegis.callback.errors.total` ile sayılır |
| Telemetri dinleyicisi istisna fırlatırsa | her çağrı düşer | yutulur ve sayılır |
| HTTP: denemeler tükenince | son yanıt döner | istisna fırlatılır; Polly davranışı için `ReturnFinalResponse = true` |
| Saçma `Retry-After` (ör. 2147483647 sn) | `ArgumentOutOfRangeException` | `MaxDelay` ile sınırlanır |
| `TimeSpan.MaxValue` zaman aşımı | kurulumda reddedilir | sonsuz sayılır |
| Kötümser timeout (token'a saygısız kod) | v8'de yok | `TimeoutStrategyMode.Pessimistic` |
| Eşzamanlılık sınırı doluyken | varsayılan: anında red (`QueueLimit = 0`) | aynı (2.0.0); kuyruk için `QueueLimit` + `QueueTimeout` |
| Standart HTTP işleyicisinde `HttpClient.Timeout` | sonsuza çekilir; sınırı `TotalRequestTimeout` koyar | aynı (2.0.0) |
| Retry: açık devre / hız sınırı reddi | Polly varsayılanı yeniden dener (dönen döngü); Microsoft standart işleyicisi denemez | yeniden denenmez |
| Devre kesici: çağıran iptal etmediği halde gelen iptal (`HttpClient.Timeout`) | yok sayılır, devre takılan servise karşı açılmaz | hata sayılır |
| `OnRetry` / `DelayGenerator` deneme numarası | 0'dan başlar | aynı (2.0.0; önceden 1'den) |
| Varsayılanlar (CB 0,1 / 100 / 30 sn / 5 sn, Timeout 30 sn, Hedging 2 sn) | Polly ve Microsoft | aynı (2.0.0) |
| Retry varsayılanı | Polly: sabit 2 sn, jittersiz; Microsoft standart: üstel + jitter | üstel + jitter, 2 sn (Microsoft gibi) |
| Standart işleyici iki kez eklenirse | Microsoft sessizce yığar (16 fiziksel çağrı) | istemci oluşturulurken hata; önce `RemoveAllAegisHandlers()` |
| İç işleyicinin her denemede eklediği başlık | Microsoft aynı isteği tekrar gönderir, başlık çoğalır | her deneme özgün başlıklarla başlar (.NET 6+) |

## 9. gRPC: Grpc.Net.Client `ServiceConfig` ve HTTP işleyicilerinden

HTTP katmanındaki Polly / `AddStandardResilienceHandler` gRPC hatasını göremez (`grpc-status` trailer'dadır, yanıt HTTP 200).
Bunları gRPC istemcisinden kaldırıp Aegis interceptor'ını ekleyin:

```csharp
services.AddGrpcClient<Siparis.SiparisClient>(o => o.Address = adres)
    .AddStandardAegisGrpcResilience(o =>
    {
        o.Retry.MaxRetryAttempts = 4;              // ServiceConfig MaxAttempts = 5 (ilk çağrı dahil)
        o.Retry.Delay = TimeSpan.FromSeconds(1);   // InitialBackoff
        o.Retry.MaxDelay = TimeSpan.FromSeconds(5);// MaxBackoff
        o.Retry.Budget = new RetryBudget(maxTokens: 10, tokenRatio: 0.1); // RetryThrottling
    })
    .AddAegisGrpcOutlierDetection();              // isteğe bağlı, .NET 8+
```

| Grpc.Net.Client `ServiceConfig` | Aegis |
|---|---|
| `RetryPolicy.MaxAttempts` (ilk çağrı dahil) | `Retry.MaxRetryAttempts` = `MaxAttempts - 1` |
| `InitialBackoff` / `MaxBackoff` / `BackoffMultiplier` | `Retry.Delay` / `Retry.MaxDelay` / `BackoffType = Exponential` (jitter açık) |
| `RetryableStatusCodes` | `Retry.ShouldHandle` (varsayılan `AegisGrpcTransientErrors`: `Unavailable` + pushback'li `ResourceExhausted`) |
| `HedgingPolicy` (`MaxAttempts`, `HedgingDelay`, `NonFatalStatusCodes`) | `AddAegisGrpcResilience(p => p.AddHedging(...))` |
| `RetryThrottling` (`MaxTokens`, `TokenRatio`) | `RetryBudget(maxTokens, tokenRatio)` |
| `MaxRetryBufferPerCallSize` | `AegisGrpcClientOptions.MaxRetryBufferBytes` (1 MB) |
| — (yok) | devre kesici, deneme zaman aşımı, hız sınırı, uç nokta ayıklama, metot bazında telemetri |

Aynı çağrının iki kez yeniden denenmemesi için `ServiceConfig` içindeki `RetryPolicy` / `HedgingPolicy`'yi kaldırın.
Sunucuda `services.AddGrpc(o => o.AddAegisResilience(...))` retleri `ResourceExhausted`/`Unavailable` ve pushback ile döndürür.

## 10. Geçiş kontrol listesi

1. Paketleri ekleyin (bölüm 1); Polly paketlerini henüz kaldırmayın.
2. Bir servisi seçin; boru hattını bölüm 3'teki gibi yeniden yazın, adı aynı tutun.
3. `catch (TimeoutRejectedException)` → `catch (AegisTimeoutException)`; diğer istisna adları aynı.
4. DI kaydını `AddAegisPipeline`, çözümlemeyi `IAegisPipelineRegistry` ile değiştirin.
5. HttpClient'larda `AddStandardResilienceHandler` → `AddStandardAegisHandler`; son yanıt beklenen yerlerde
   `ReturnFinalResponse = true`.
6. Panolarda `resilience.polly.*` metriklerini `aegis.*` ile değiştirin.
7. gRPC istemcilerinde `ServiceConfig` retry/hedging yerine `AddStandardAegisGrpcResilience` (bölüm 9).
8. Testlerde `Polly.Testing` yerine `Aegis.Resilience.Testing` (`GetPipelineDescriptor`, `GetOptions<T>()`).
9. Tüm servisler taşındığında Polly paketlerini kaldırın.
