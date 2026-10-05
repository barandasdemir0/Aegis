# Değişiklik Günlüğü (Changelog)

Bu proje [Semantic Versioning](https://semver.org/lang/tr/) kurallarını izler.

## [2.0.0] — Standartlara hizalama (bağımsız doğrulama bulguları)

Paketlenmiş 1.5.0, proje içi test ve belgelerden bağımsız bir düzenekte Polly 8.8.0 ve Microsoft 10.10.0 ile yeniden
sınandı. Bulunan sapmalar, sektör standardı araştırılarak ona göre düzeltildi. Varsayılan davranış ve bir seçeneğin anlamı
değiştiği için **ana sürüm** artışı.

### Kırıcı değişiklikler

- **Paket ve namespace adları `Aegis.Resilience.*` oldu** (`Aegis.Core` → `Aegis.Resilience.Core`, `Aegis.Grpc` →
  `Aegis.Resilience.Grpc` …). `Aegis.Core` paket kimliği nuget.org'da ilgisiz bir projeye ait olduğu için. Geçiş: paket
  başvurularında ve `using` satırlarında `Aegis.` önekini `Aegis.Resilience.` yapın; tür adları (`AegisPipeline` …) aynı.
- **`MaxHedgedAttempts` artık birincile EK deneme sayısıdır** (Polly ve Microsoft ile aynı anlam; varsayılan 1 = toplam 2
  deneme). Önceden birincil dahil toplamdı: Polly'den `MaxHedgedAttempts = 1` ile geçen biri hiç yedek deneme almıyordu.
  `HedgingOptions`, `AegisHttpStandardHedgingOptions`, `MultiEndpointHedgingOptions`. Geçiş: mevcut değerleri 1 azaltın.
- **Eşzamanlılık sınırlayıcısı doluyken varsayılan olarak beklemeden reddeder.** Yeni `ConcurrencyLimiterOptions.QueueLimit`
  (varsayılan 0) kuyruğu sınırlar; `QueueTimeout` yalnızca kuyrukta beklerken geçerlidir. Önceden her çağrı sınırsız sayıda
  2 sn bekliyordu. `AdaptiveConcurrencyOptions.QueueTimeout` varsayılanı da 0. Standart: Polly ve Microsoft standart işleyicisi
  `QueueLimit = 0`, .NET `ConcurrencyLimiter` kuyruk sınırını zorunlu tutar; Azure "Bulkhead" ve yük atma ilkesi: aşırı
  yükte kuyruk gecikmeyi ve belleği büyütür, istemci vazgeçmişken iş yapılır. Geçiş: bekleme gerekiyorsa `QueueLimit` verin.
- **Standart HTTP işleyicileri `HttpClient.Timeout`'u sonsuza çeker** (Microsoft `AddStandardResilienceHandler` ile aynı).
  Önceden HttpClient'ın 100 sn'si, daha uzun bir `TotalRequestTimeout`'u sessizce kesiyordu.
- **HTTP işleyicilerinin kiraladığı bağlam istek bitince istekten ayrılır ve havuza döner** (Microsoft ile aynı). İstek
  sonrası `request.GetAegisContext()` null döner; kullanıcının `SetAegisContext` ile verdiği bağlam korunur.
- **Pano müdahalesi güvenli varsayılan:** `MapAegisDashboard` yetki politikası olmadan çağrılırsa Isolate/Reset yalnızca yerel
  makineden kabul edilir (uzak istemci 403). Önceden ağa erişen herkes üretimde devreleri açabiliyordu. Okuma değişmedi.
- **`MultiEndpointHedgingHandler` idempotent olmayan isteği birden fazla uç noktaya göndermez** (Idempotency-Key'siz POST →
  yalnızca birincil, bir kez). Önceden çift işlem riski vardı. Bilinçli açmak için `MultiEndpointHedgingOptions.AllowNonIdempotentHedging`.

### Pano (ekran görüntüsü çekerken bulundu)

- **İzole devre ayrı etiketle gösteriliyor:** elle izole edilen devre "AÇIK" yerine mavi **İZOLE (bakım)** etiketiyle görünür;
  bilerek kesilen devre çöken servisle karışmaz.
- **Telefonda kullanılabilir:** dar ekranda kartlar ve istatistikler taşıyordu (340 px / 200 px en küçük sütun, kaymayan
  başlık). Izgaralar `min(…, 100%)`, başlık ve düğmeler alt satıra kayar; 390 px genişlikte doğrulandı.

### Belgeler

- README'ye panonun beş gerçek ekran görüntüsü eklendi (sağlıklı, kesinti, toparlanma, bakım, telefon); `docs/images/`.
- README baştan yazıldı: Mermaid diyagramları (paket mimarisi, boru hattı akışı, devre durumları, dağıtık mimari, HTTP ve
  gRPC akışları), ayrıntılı pano ve sağlık kontrolü bölümleri, gRPC bölümü, derin .NET 10 bölümü. Yanlış API adları
  (`AddAegisProxy`, `AddAegisHealthChecks`), parametre adı (`maxConcurrency:` → `maxConcurrent:`), metrik adı
  (`aegis.timeouts.total` → `aegis.timeout.total`) ve "anlık grafikler" iddiası (pano grafik çizmez) düzeltildi.
  README örnekleri artık testle derlenip çalıştırılıyor (`ReadmeSnippetTests`).
- XML belgeleri: `CircuitBreakerOptionsBase` 2.0.0 öncesi varsayılanları gösteriyordu (0,5 / 10 sn / 5 / 15 sn → 0,1 / 30 sn /
  100 / 5 sn); Retry, Timeout ve Hedging seçeneklerinin en çok kullanılan özelliklerine eksik açıklamalar eklendi.
- USAGE: gRPC paketleri kurulum listesine ve içindekilere eklendi; yalnızca .NET 8+ olan paket sayısı düzeltildi (3 → 5).

### Depo düzeni

- İngilizce açık kaynak düzeni: belgeler `docs/` (USAGE, MIGRATION, POLLY-COMPARISON, BENCHMARKS, TEST-INFRASTRUCTURE,
  `reports/`), tek örnek proje `samples/RealWorld`. `AegisShowcase` ve `ExampleProject` kaldırıldı (tüm özellikleri
  RealWorld kapsıyor; Kubernetes doğrulama raporu `docs/reports/` altında arşivlendi). Ham BenchmarkDotNet çıktıları git'ten
  çıkarıldı; özet `docs/BENCHMARKS.md`. AOT duman testi ve benchmark projesi `Aegis.slnx`'e eklendi.

### Gerçek proje doğrulaması (`samples/RealWorld/`, gerçek NuGet paketleriyle)

- **gRPC istemci/çift yönlü akış protobuf ile çalışmıyordu:** tampon boyutu basit `Serializer` ile ölçülüyordu; Grpc.Tools'un
  ürettiği marshaller yalnızca bağlamsal serileştiriciyi destekler (`NotImplementedException`). Artık bağlamsal serileştirici
  kullanılır (`MessageSizer`).
- **Token'lı akış yazma (`WriteAsync(mesaj, ct)`) `NotSupportedException` fırlatıyordu;** artık desteklenir.
- **`AddAegisCheck` ayarları kontrol başına:** farklı `configureOptions` ile iki kontrol (canlılık / hazır olma) birbirini
  ezmiyor. Ayar verilmeyen kayıt uygulama geneli `Configure<AegisHealthCheckOptions>` ayarını kullanmaya devam eder.
- RealWorld genişletildi: 79 test, kütüphanenin 75 genel genişletme metodunun tamamı + .NET Framework 4.8 Web API 2.
- Belge: gRPC sunucusunun eşzamanlılık reddi pushback taşımaz (bekleme süresi bilinmez).

### Kod incelemesi düzeltmeleri (satır satır tarama)

- **Hedging:** retry bütçesi yeni denemeye izin vermediğinde özgün istisna yerine genel "tamamı başarısız" hatası dönüyordu.
- **HTTP:** uzunluğu bilinmeyen (geri sarılamayan) akış gövdesi tampon sınırını aşınca istek hiç gönderilmeden düşüyordu; artık
  okunan kısım + kalan akış tek denemede eksiksiz gider. Uzunluğu bilinmeyen diğer içerik (ör. `JsonContent`) sınırı aşarsa
  yeniden denenemez sayılır ama gönderilir.
- **Kötümser zaman aşımı:** işlem zamanında bitince gecikme zamanlayıcısı süre sonuna kadar yaşıyordu (çağrı başına sızıntı);
  baştan iptal edilmiş çağrıda istisna sonuç yerine fırlatılabiliyordu.
- **DI proxy:** senkron metotlar artık çağıranın iş parçacığında çalışır (önceden `Task.Run` ile havuza atlıyordu).
- **Bellek içi hız sınırı deposu:** canlı anahtar eşiği aşıldığında her istek tüm sözlüğü tarıyordu (çok IP'li yükte CPU
  tüketimi); temizlik saniyede bir ile sınırlandı.
- **gRPC:** akış erken bırakılınca iptal `DeadlineExceeded` sayılıyordu; deadline zamanlayıcısı saatten önce tetiklenince
  nadiren `TaskCanceledException` dönüyordu.
- StaleFallback arka plan yenilemesi havuza dönmüş bağlamı okumuyor; yinelenen yorum bloğu temizlendi.

### Standartlara göre düzeltmeler (araştırma: Polly belgeleri, dotnet/extensions issue'ları, topluluk kaynakları)

- **Retry retleri yeniden denemez:** `RateLimitRejectedException` artık `BrokenCircuitException` gibi yeniden denenmez (önceden
  hemen 3 kez deneniyordu). Ret hedefe gidilmediği anlamına gelir; Microsoft standart işleyicisiyle aynı.
- **Devre kesici, çağıranın istemediği iptali hata sayar:** `HttpClient.Timeout` gibi kaynaklardan gelen `TaskCanceledException`
  takılan bağımlılığı gösterir. Polly bunu yok sayar (devre hiç açılmaz); çağıranın kendi iptali hâlâ sayılmaz.
  `CircuitBreakerRules.IsCircuitNeutral`, `CircuitBreakerOptionsBase.IsFailure(Exception, CancellationToken)`.
- **Deneme numarası 0'dan başlar** (`RetryAttemptContext.AttemptNumber`; Polly ile aynı). Gecikme hesabı değişmedi.
- **Varsayılanlar Polly ve Microsoft ile aynı:** devre kesici 0,1 / 100 / 30 sn / 5 sn (önceden 0,5 / 5 / 10 sn / 15 sn),
  Timeout 30 sn (önceden 10), Hedging gecikmesi 2 sn (önceden 500 ms), Retry tabanı 2 sn (önceden 500 ms; üstel + jitter korunur).
- **HTTP: iç işleyici başlıkları denemeler arasında çoğalmaz.** Klon yalnızca özgün başlıkları alır (.NET 6+).
- **HTTP: standart işleyici iki kez eklenirse istemci oluşturulurken hata** (sessiz 16× trafik yerine). Geçersiz kılmak için önce
  `RemoveAllAegisHandlers()`.
### Eklendi (ikinci araştırma turu: gRPC A6, Envoy/Finagle, resilience4j, dotnet/extensions issue'ları)

- **`RetryBudget` (retry bütçesi):** gRPC "retry throttling" algoritması. `RetryOptions.Budget` verilirse hata oranı yükselince
  yeniden denemeler kendiliğinden durur (retry fırtınası trafiği katlayamaz), sağlık dönünce açılır; birden çok boru hattında
  paylaşılabilir. Bütçe tükenince `OnRetryBudgetExhausted` telemetri olayı. Polly ve Microsoft'ta yok; varsayılan kapalı.
- **Takılan deneme isteği koruması:** HalfOpen'daki deneme isteği bir `BreakDuration`'dan uzun sürerse terk edilir ve yenisine
  izin verilir (önceden devre sonsuza dek HalfOpen'da kalıp herkesi reddedebiliyordu; resilience4j
  `maxWaitDurationInHalfOpenState`). Dağıtık devrede zaten vardı.
- dotnet/extensions #5699 (standart hedging toplam zaman aşımına uymuyor) Aegis'te yeniden üretilemedi; regresyon testi eklendi.
- **Hedging de retry bütçesini kullanır** (`HedgingOptions.Budget`, `AegisHttpStandardHedgingOptions.Budget`): gRPC A6 gibi ilk
  istek her zaman gider, ek denemeler yalnızca bütçe izin verirse başlar; iptal edilen kaybeden deneme bütçeyi tüketmez.
- **`CircuitBreakerOptions.HalfOpenSuccessThreshold`** (varsayılan 1 = Polly): devrenin kapanması için art arda N başarılı deneme
  isteği; denemeler yine birer birer, herhangi bir hata devreyi yeniden açar (resilience4j `permittedNumberOfCallsInHalfOpenState`).
- **`CircuitBreakerOptions.SamplingCount`**: sayı tabanlı pencere, oran son N çağrıdan (resilience4j `COUNT_BASED`). Null:
  zaman tabanlı (varsayılan). `MinimumThroughput`'tan küçükse kurulumda hata.
- **`CircuitBreakerOptions.Mode`**: `Shadow` (gölge: olaylar ve metrikler çalışır, istek reddedilmez; elle izolasyon yine
  uygulanır) ve `Disabled` (geçirgen) — resilience4j `METRICS_ONLY` / `DISABLED`. Canlı değiştirilebilir.
### Eklendi — gRPC (`Aegis.Resilience.Grpc`, `Aegis.Resilience.Grpc.AspNetCore`)

HTTP katmanındaki dayanıklılık (Polly, Microsoft `AddStandardResilienceHandler`) gRPC'de çalışmaz: hata trailer'dadır ve yanıt
HTTP 200'dür; gRPC çağrısı POST olduğu için yeniden denenmez; tamponlama akışları bozar. Aegis dayanıklılığı gRPC katmanında uygular.

- **İstemci interceptor'ı** (`AegisGrpcClientInterceptor`, `AddStandardAegisGrpcResilience` / `AddAegisGrpcResilience`):
  - unary (async + senkron) ve sunucu akışı için retry, hedging, devre kesici, zaman aşımı ve hız sınırı,
  - gRPC A6 commit kuralı, tüm denemeleri kapsayan deadline,
  - `grpc-retry-pushback-ms` (pozitif = bekle, negatif = deneme) ve `grpc-previous-rpc-attempts`,
  - retry bütçesi (gRPC throttling), retlerin `RpcException`'a çevrilmesi,
  - `RpcException(Cancelled)` çevrimi (deneme zaman aşımı tanınır).
- **Durum sınıflandırması** (`AegisGrpcTransientErrors`): yalnızca `Unavailable` yeniden denenir; pozitif pushback'li
  `ResourceExhausted` de denenir. Devre kesici yalnızca sunucu tarafı hataları sayar.
- **Sunucu interceptor'ı** (`AegisGrpcServerInterceptor`, `GrpcServiceOptions.AddAegisResilience`):
  - retler gRPC durumu ve pushback ile döner,
  - sunucu zaman aşımı servis kodunu gerçekten keser (`GetHttpContext()` korunur),
  - Retry ve Hedging içeren boru hattı reddedilir.
- **HTTP işleyicileri gRPC isteğini dokunmadan geçirir** (önceden tamponlama ve toplam zaman aşımı akışları bozabiliyordu).
- **ASP.NET Core gelen istek koruması gRPC isteğine gRPC yanıtı verir** (HTTP 429 yerine `grpc-status: 8` ve pushback; mesaj
  yüzde kodlanır).
- **İstemci ve çift yönlü akışta retry:** tamponlu yeniden oynatma (`AegisGrpcClientOptions.MaxRetryBufferBytes`, 1 MB);
  sınır aşılınca veya ilk yanıtla commit; geri basınçlı yazma.
- **Uç nokta ayıklama** (`AddAegisGrpcOutlierDetection`, .NET 8+): Envoy varsayılanlarıyla, `grpc-status` hatalarını da sayar.
- **Performans (.NET 10):** interceptor çağrı başına 688 B (Polly interceptor 720 B), standart zincir Polly'nin 0,95×'i.
  Durum nesneli Grpc.Core yapıcıları; çağrı başına closure yok.
- **Düzeltme:** deadline zamanlayıcısı saatten önce tetiklendiğinde nadiren `DeadlineExceeded` yerine `TaskCanceledException` dönüyordu.
- **Testler:** 28 uçtan uca test, gerçek Kestrel HTTP/2 ve gerçek `GrpcChannel` ile.
### Düzeltildi

- `OnRetry` ve `OnTimeout` istisna fırlatırsa artık diğer olaylar gibi yutulur ve `aegis.callback.errors.total` ile sayılır;
  yeniden deneme ve zaman aşımı sonucu bozulmaz. Önceden bu iki olay kuralın dışında kalmıştı.

### İç yapı (davranış değişmedi)

- **DRY:** ASP.NET Core ve Web API 2 gelen istek hız sınırı ortak motoru kullanır (`Aegis.Resilience.Distributed.Abstractions`:
  `InboundRateLimitEngine<TRule>`, `InboundRateLimitRule`, `InboundRateLimitOptionsBase`, `InboundEndpointPattern`,
  `InboundIpRule`). `AegisInboundRateLimitRule` / `AegisWebApiRateLimitRule` adları korunur (ortak tabandan türer).
- **DRY:** Yerel ve dağıtık devre kesici olayları `CircuitBreakerEventContext.Opened/Closed/HalfOpened/Manual` ile kurulur.
- **SRP:** Uzun yürütme metotları adlandırılmış adımlara bölündü (`AegisPipeline`, `HedgingStrategy`,
  `HttpResilienceExecutor` → `HttpAttempts` + `AttemptSuppression`, `MultiEndpointHedgingHandler`).
- **Okunabilirlik:** Her üst düzey tür kendi dosyasında (42 dosya bölündü).
- **Law of Demeter:** Testler ve örnekler `pipeline.Strategies[...]` yerine `CircuitBreakerManualControl`,
  `CircuitBreakerStateProvider` ya da kendi oluşturdukları stratejiyi kullanır.
- **Tutarlılık:** Devre kesici ve hedging hata mesajları düzyazıda Türkçe ("devre kesici", "yedek deneme"); kod
  tanımlayıcıları (`HalfOpen`, `Retry`) olduğu gibi.
### Performans

- HTTP standart işleyicisi istek başına bellek: 2.176 B → 1.264 B (Microsoft: 1.264 B; eşit); süre Microsoft'un 0,74×'ü. Bağlam havuzdan kiralanır,
  yürütücü tek async katmana indi, closure/delegate yerine durumlu statik geri çağrı, bağlam temizliği tahsissiz.
## [1.5.0] — İz (trace) desteği

Yeni genel API eklendiği için küçük sürüm artışı. Mevcut API ve varsayılan davranış değişmedi; iz yalnızca bir dinleyici
(ör. OpenTelemetry `AddSource("Aegis")`) bağlıysa çalışır.

Sürüm notu: `Aegis.Resilience.Extensions.Aspire` ve `Aegis.Resilience.WebApi` 1.4.0'da paketlenmişti ama genel API dosyaları `Unshipped`'de kalmıştı; bu
sürümde `Shipped`'e taşındı (davranış değişikliği yok, yalnızca API sözleşmesi kaydı).

### Eklendi

- **İz (trace) desteği:** her boru hattı yürütmesi için bir `Activity` (span). Kaynak adı `Aegis` (metrik adıyla aynı):
  `tracing.AddSource(AegisTelemetry.ActivitySourceName)`.
  - **Span:** adı `Aegis <boru hattı adı>`, türü `Internal`. Etiketler: `pipeline.name`, `pipeline.instance` (varsa), `operation.key`
    (varsa). Başarısızlıkta durum `Error` ve `exception.type` etiketi; **istisna iletisi yazılmaz** (hassas veri sızdırabilir).
  - **Span olayları:** strateji olayları (`OnRetry`, `OnCircuitOpened`, `OnTimeout`, `OnHedging`, `OnFallback` ...) span üzerinde
    `ActivityEvent` olarak. Sorunsuz tamamlanan, ele alınmayan denemeler olay üretmez.
  - **İç içe span'lar:** geri çağrının içinde (eşzamansız sınırlar dahil) açılan span'lar (ör. `HttpClient`) Aegis span'ının çocuğu olur.
  - **Çağıranın bağlamı korunur:** çağrı sürerken bile çağıranın `Activity.Current` değeri yerindedir.
  - **Boru hattına girmeden iptal edilmiş çağrı span üretmez** (hiçbir strateji çalışmaz).
- **`AddAegisServiceDefaults`:** `AegisServiceDefaultsOptions.AddTracing` (varsayılan açık) Aegis iz kaynağını TracerProvider'a ekler.
- **Maliyet (ölçüldü, P çekirdeklere sabitlenmiş, `TracingBenchmarks`):**
  - Dinleyici yokken tek bir `ActivitySource.HasListeners()` denetimi; ölçülebilir maliyet yok (boş boru hattı 47,7–48,2 ns).
  - Dinleyici bağlı, örnekleme kapalı: standart zincirde +%9–13, tahsis yok.
  - Tam kayıt: standart zincirde 630 → 1.180 ns ve çağrı başına 520 B (span, etiketler, olaylar).
- **Doğrulama:** 12 yeni birim test, .NET Framework 4.8'de bir uyumluluk testi, Native AOT smoke testinde bellek içi dışa aktarıcıyla bir
  senaryo ve **gerçek OpenTelemetry Collector'a gRPC ve HTTP/protobuf ile iz gönderimi** (`tests/docker/TEST-ALTYAPISI.md` bölüm 6.1),
  batırma testlerine "iz açıkken fırtına" senaryosu (32×300 çağrı: span sızıntısı, ebeveyn karışması, bağlam sızıntısı yok).

### Notlar

- Polly 8.8.0'ın genel API'sinde (Core, Extensions, RateLimiting, Testing) ve Microsoft.Extensions.Resilience / Http.Resilience
  10.10.0 derlemelerinde `ActivitySource` kullanımı yok (yalnızca metrik). `Polly.Extensions` derlemesinin içi bu çalışmada doğrulanmadı.
- Benchmark yöntemi: bu makinenin işlemcisi hibrit (P + E çekirdek). Süreç E çekirdeğe düşerse ölçüm ~2× yavaş çıkar (111 ns gibi
  iki modlu dağılım). Karşılaştırmalı mikro ölçümlerde `--affinity 65535` ile P çekirdeklere sabitlemek gerekir.

## [1.4.0] — Aspire entegrasyonu, Web API 2 hız sınırı, Polly geçiş rehberi

### Eklendi

- **`Aegis.Resilience.Extensions.Aspire`:** `builder.AddAegisServiceDefaults()` tek çağrıyla tüm HttpClient'lara standart
  dayanıklılık işleyicisi (`Aegis:Http` bölümünden, değişince yeniden kurulur), `Aegis` metrikleri OpenTelemetry'ye ve
  Aegis sağlık kontrolü. Aspire şablonundaki `AddStandardResilienceHandler` eşdeğeri; parçalar tek tek kapatılabilir.
- **`Aegis.Resilience.WebApi`:** klasik ASP.NET Web API 2 (.NET Framework) için gelen istek hız sınırı (WebApiThrottle eşdeğeri).
  `config.UseAegisRateLimiting(o => ...)`: IP / başlık / kullanıcı bölümleme, uç nokta kuralları, IP-CIDR / istemci /
  uç nokta beyaz listesi, `RateLimit-*` ve `Retry-After` başlıkları, özel red yanıtı. Sayaçlar `IDistributedRateLimitStore`'da;
  Redis deposu verilirse küme genelinde ortak kota.
- **`GECIS-REHBERI.md`:** Polly v8 / Microsoft.Extensions.Resilience'tan adım adım geçiş. Örnekler
  `MigrationGuideSnippetTests` ile derlenip koşar.
- **`.claude/skills/aegis-rakip-analizi`:** rakip analizini kanıta dayalı tekrarlamak için proje skill'i.

## [1.3.0] — Polly 8.8 ve Microsoft 10.10 eşitliği, batırma testleri, kırılmazlık düzeltmeleri

Rakiplerin son 10 sürümü kaynaktan incelendi (sürümler NuGet API'sinden doğrulandı, 2026-10-01): Polly 8.5.2 → 8.8.0,
Microsoft.Extensions.(Http.)Resilience 10.1.0 → 10.10.0. Kapanmamış her yetenek eklendi. Yeni
**batırma (torture) projesi** Aegis'i ve rakiplerin son sürümlerini aynı acımasız senaryolarda defalarca dener.
Bulduğu Aegis hatası düzeltildi; rakiplerde bulduklarıyla birlikte `tests/Aegis.TortureTests` raporunda.

### Eklendi — Polly 8.5.2 → 8.8.0 eşitliği

- **Devre reddi ayrıntısı:**
  - `BrokenCircuitException.RetryAfter`: açık devrenin deneme isteği kabul etmesine kalan süre.
  - `IsolatedCircuitException`: elle izole edilmiş devre. `BrokenCircuitException`'dan türer; mevcut `catch` blokları
    aynen çalışır.
- **Devre olayları:**
  - `CircuitBreakerEventContext.HalfOpenAttempts`: art arda başarısız deneme isteği sayısı. `BreakDurationGenerator`
    ile üstel açık kalma süresi yazılabilir.
  - `IsManual`: geçiş ManualControl veya pano ile mi yapıldı. Elle geçişler artık `OnOpened`/`OnClosed` tetikler.
  - `Exception` / `Result`: geçişi tetikleyen sonuç. Sonuç yalnızca olay oluşturulunca kutulanır.
  - Dağıtık devre kesici de aynı alanları ve `IsolatedCircuitException`'ı destekler. `RetryAfter` orada null'dır, çünkü
    kalan süre dağıtık depodan okunmaz.
- **Kaos:**
  - `ChaosOptions.LatencyGenerator`: çağrı başına gecikme.
  - `FaultGenerator` null dönerse hata enjekte edilmez.
  - Boş `ChaosOutcomeGenerator` çağrıyı aynen geçirir.
  - Ağırlık toplamı artık taşmaz (`long`).
- **Telemetri:**
  - Boru hattı örnek adı: `AegisPipelineBuilder.InstanceName` / `WithInstanceName(...)`.
    - Olayda `AegisTelemetryEvent.PipelineInstance`, metrikte `pipeline.instance` etiketi.
  - `PipelineExecuting` olayı: Polly gibi yalnızca dinleyicilere gider, metrik yazılmaz.
  - `AegisException.TelemetrySource`: reddi üreten boru hattı / örnek / strateji. Devre, zaman aşımı ve hız sınırı
    redlerinde doldurulur.
- **Hedging:** `HedgingOptions.ShouldHandle` (istisna koşulu). Ele alınmayan istisna yedeklemeyi bitirir ve çağırana
  iletilir; null (varsayılan) eski davranıştır.
- **Yardımcılar:**
  - `Outcome.FromResult` / `FromException` / `FromResultAsValueTask` / `FromExceptionAsValueTask`.
  - `Outcome<T>.ThrowIfException()`.
  - `AegisPipeline.Empty`.
  - Kayıt defterlerinde `IAsyncDisposable` (`await using`).
  - `RateLimiterRejectedArguments.Metadata`: System.Threading.RateLimiting kiralamasının meta verisi.
- **DI kurulum bağlamı** (Polly: `AddResiliencePipeline(key, (builder, context) => ...)`):
  - `AddAegisPipelineWithContext(name, (builder, context) => ...)`. `AegisPipelineContext` şunları sunar:
    - `GetOptions`
    - `EnableReloads<T>()` ve `EnableReloads(IOptionsMonitor<T>)` (Polly 8.8)
    - `AddReloadToken`
    - `OnPipelineDisposed`
  - Her neslin abonelikleri o nesille birlikte bırakılır.
  - Hiç yeniden kurma kaynağı yoksa düz boru hattı döner (sarmalayıcı maliyeti yok).

### Eklendi — Microsoft.Extensions.Http.Resilience 10.10 eşitliği

- **`AegisHttpTransientErrors`:** `IsTransient(HttpStatusCode | HttpResponseMessage | Exception)`,
  `IsTransientForHedging`, `IsConnectionTimeout`, `AegisPredicateBuilder.HandleTransientHttpErrors()`. Tek tanım; tüm
  işleyiciler bunu kullanır.
- **`RetryOptions.ShouldRetryAfterHeader`** (varsayılan true): sunucunun `Retry-After` süresi kullanılsın mı. Süre
  `MaxDelay` ile sınırlanır.
- **Servis sağlayıcılı standart işleyiciler:** `AddStandardAegisHandler((options, serviceProvider) => ...)` ve hedging
  eşdeğeri (Microsoft: `.Configure((o, sp) => ...)`).
- **Anahtar başına DI bağlamlı boru hattı:** `AddAegisResilienceHandler(configure, selectPipelineBy, maxPipelines)`
  (Microsoft: `SelectPipelineBy` / `SelectPipelineByAuthority`).
  - `AegisHttpPipelineSelectors.ByAuthority` hazır seçicidir.
  - Bağlamda `InstanceName` = anahtar.
  - Kardinalite sınırı vardır; kurulum hatası önbelleğe alınmaz.
- **`AegisContext.GetRequestMessage()`:** bildirim ve koşullarda isteğe erişim.
- `AegisHttpHandlerContext` artık `AegisPipelineContext`'ten türer. `ServiceProvider`, `GetOptions`, `EnableReloads` ve
  `OnPipelineDisposed` taban sınıfa taşındı; bu ikili uyumludur. Yeni `AddReloadToken` ve `EnableReloads(IOptionsMonitor)`
  HTTP bağlamında da kullanılabilir.

### Davranış değişiklikleri (Polly / Microsoft ile hizalama)

- **Tüm 5xx yanıtları geçicidir** (Microsoft `statusCode >= 500`, Polly `HandleTransientHttpError` ile aynı). Önceden
  yalnızca 500/502/503/504 yeniden deneniyordu. Artık 501, 505, 507 ve Cloudflare 520–524 de deneniyor. Idempotent
  olmayan istekler korunmaya devam eder.
- **Bağlantı kurma zaman aşımı yeniden denenir.** Bu, `SocketsHttpHandler.ConnectTimeout`'un içinde
  `TimeoutException` taşıyan `OperationCanceledException`'ıdır; çağıran iptal etmemişken Microsoft ile aynı şekilde
  ele alınır.
  - Önceden iptal sayılıp hiç denenmiyordu.
  - Denemeler tükenirse özgün istisna yükselir; çağıranın iptali asla yeniden denenmez.
  - Microsoft ayrıca `Source == "System.Private.CoreLib"` denetler. Aegis denetlemez, çünkü .NET Framework'te kaynak
    `mscorlib`'tir.
- İzole devre artık `IsolatedCircuitException` fırlatır. Bu bir alt tiptir: `catch (BrokenCircuitException)` çalışır;
  yalnızca tam tip eşleştiren testler (`Assert.ThrowsAsync<BrokenCircuitException>`) güncellenmelidir.
- Kaos: `FaultGenerator` null dönerse artık yapay `InvalidOperationException` değil hiçbir şey enjekte edilmez
  (Polly 8.8).
- Yeni tip çıkarımlı `Outcome` sınıfı Polly ile aynı addadır. Aynı dosyada hem `using Polly;` hem
  `using Aegis.Resilience.Core.Abstractions;` varsa (geçiş sürecindeki karışık kod) `Outcome.FromResult(...)` belirsizleşir
  (CS0104). Bu, `Outcome<T>` için zaten geçerliydi. Çözüm: `Polly.Outcome...` ya da `using PollyOutcome = Polly.Outcome;`.

### Düzeltildi

- **Senkron `HttpClient.Send` dayanıklılığı atlıyordu.** Tüm Aegis HTTP işleyicileri yeni `AegisDelegatingHandler`
  tabanından türer ve `Send` aynı çekirdekten geçer. Hedging'de denemeler iş parçacığı havuzunda koşar.
- **Zamanlayıcı sınırı taşması** (batırma testi buldu): `TimeSpan.MaxValue` ya da yaklaşık 24,8 günü aşan zaman aşımı,
  gecikme veya bekleme çalışma anında `ArgumentOutOfRangeException` fırlatıyordu. `int.MaxValue` ms'yi aşan süre artık
  sonsuz sayılır (yalnızca iptal sonlandırır). Bu kural tek yerde (`AegisTimers`) uygulanır: zaman aşımı, retry, hedging,
  kaos, hız/eşzamanlılık sınırı ve önbellek tarama zamanlayıcısı.
- **Authority başına standart işleyici** `CircuitBreaker.ConsecutiveFailureThreshold`'u kopyalamıyordu (anahtar başına
  devrelerde sessizce kayboluyordu).

### Performans

- **Adaptive Concurrency senkron hızlı yol:** boşta kapasite + senkron tamamlanan geri çağrıda async durum makinesi
  oluşmaz. Tek iş parçacığında 360 → 283 ns (Polly'nin sabit sınırlayıcısına göre 1,18× → 0,95×). 64 işçide kazanç yok
  (848 ns; maliyet kilit çekişmesi). Yeni `AdaptiveConcurrencyBenchmarks` sınıfı ve `AdaptiveConcurrencyFastPathTests`.
- İç refactor'ler (hedging işleyicisi, zaman aşımı, telemetri, AOP kaydı): davranış ve performans değişmedi.

### Batırma (torture) testleri — `tests/Aegis.TortureTests`

Aegis, Polly 8.8.0 ve Microsoft.Extensions.Http.Resilience 10.10.0 aynı 15 senaryoda, farklı tohumlarla defalarca
denenir.

- **Senaryolar:**
  - thread fırtınası + rastgele iptal
  - izin sızıntısı
  - tek iş parçacıklı bağlamda sync-over-async
  - çağıran token yayılımı
  - uç değerler / taşma
  - fırlatan geri çağrılar
  - dispose yarışı
  - geçersiz değerli yeniden yükleme fırtınası
  - 100/1000 yıllık saat sıçraması
  - fazla kabul
  - 1M çağrı bellek
  - hedging yanıt sızıntısı
  - senkron Send
  - bozuk `Retry-After`
  - geri sarılamayan gövde
- **Sonuç (10 tur):**
  - Aegis 15/15 senaryoda tüm turları geçti.
  - Polly'de fırlatan `OnHalfOpened` veya telemetri dinleyicisi devreyi kalıcı olarak kilitledi.
  - Microsoft'ta `Retry-After: 2147483647` ve 9999 yılı tarihi isteği `ArgumentOutOfRangeException` ile düşürdü.
- **Çalıştırma:** `dotnet run -c Release --project tests\Aegis.TortureTests -- 10`

## [1.2.0] — Microsoft HTTP boşlukları ve NuGet'teki ilk 20 resilience paketiyle eşitlik

Hedef genişletildi: yalnızca Polly ve Microsoft değil, NuGet'teki ilk 20 resilience paketi (gerçek indirme sayısına göre,
ayrıntı `POLLY-ANALIZ.md` bölüm 10) karşısında da eksik yetenek kalmaması. Hiçbir mevcut API veya varsayılan davranış
değişmedi; tüm yenilikler isteğe bağlıdır. Üç yeni paket: `Aegis.Resilience.Data.SqlClient`, `Aegis.Resilience.Extensions.Caching`,
`Aegis.Resilience.AspNetCore`.

### Eklendi — Microsoft.Extensions.Http.Resilience boşlukları

- **`DisableRetryFor(HttpMethod...)` / `DisableRetryForUnsafeHttpMethods()`** (Microsoft: `DisableFor` /
  `DisableForUnsafeHttpMethods`): Standart handler'da ve DI bağlamlı handler'da, seçilen yöntemlerde yeniden deneme
  tamamen kapatılır. Açık kapatma `Idempotency-Key`'den ve `AllowNonIdempotentRetry`'dan önce gelir.
- **DI bağlamlı özel handler** (Microsoft: `AddResilienceHandler(name, (builder, context) => ...)`):
  `AddAegisResilienceHandler((pipeline, context) => ...)`.
  - Bağlam şunları sunar: `ServiceProvider`, `ClientName`, `GetOptions<T>()`, `EnableReloads<T>()`,
    `OnPipelineDisposed()` ve HTTP kuralları.
  - Boru hattı servis sağlayıcı başına bir kez kurulur ve sağlayıcıyla dispose edilir. DI telemetrisi (ILogger)
    otomatik uygulanır.
- **`RemoveAllAegisHandlers()`** (Microsoft: `RemoveAllResilienceHandlers`): `ConfigureHttpClientDefaults` ile eklenen
  Aegis handler'larını tek bir istemciden kaldırır. Aegis dışı handler'lar ve sonradan eklenenler korunur.
- **`ReturnFinalResponse`** (Microsoft davranışı): Denemeler tükenince son geçici yanıt (5xx/408/429) istisna yerine
  döner.
  - Varsayılan kapalıdır; kapalıyken istisna tipi ve telemetri etiketi birebir aynı kalır.
  - Açıkken önceki denemelerin ve kaybeden hedging akışlarının yanıtları dispose edilir.

### Eklendi — NuGet ilk 20 paketindeki yetenekler

- **Art arda hatada açılan devre** (Polly v7 `CircuitBreaker(n, süre)`): `CircuitBreakerOptions.ConsecutiveFailureThreshold`.
  - Oran kuralına ek bir koşuldur; `MinimumThroughput` beklenmez.
  - Araya giren tek başarı sayacı sıfırlar.
- **Cache** (Polly.Caching.Memory / Polly.Caching.Distributed):
  - `SlidingExpiration` (kayan süre).
  - `TtlGenerator`: sonuca göre süre; sıfır dönerse önbelleğe alınmaz.
  - Takılabilir depo `IAegisCacheStore`.
  - `OnCacheError`: depo hatası çağrıyı asla düşürmez; okuma hatası ıskalama sayılır, yazma hatası yok sayılır.
  - `InvalidateAsync`.
  - **`Aegis.Resilience.Extensions.Caching`**: `IDistributedCache` deposu (Redis, SQL Server...). Anahtar öneki ve
    değiştirilebilir serileştirici var; System.Text.Json kaynak üreticili bağlamla AOT uyumludur.
- **`Aegis.Resilience.Data.SqlClient`** (Enterprise Library Transient Fault Handling / Topaz eşdeğeri):
  - `AegisSqlTransientErrors.IsTransient` ve `HandleSqlTransientErrors()`.
  - EF Core'la aynı güncel 175 hata numarası; 203 yalnızca `Win32Exception` ile, -2 bilerek hariç.
  - İç istisna zincirini tarar (ORM sarmalayıcıları); Topaz bunu yapmaz.
- **Dağıtık hız sınırlayıcı** (AspNetCoreRateLimit.Redis / RedisRateLimiting):
  - `AddDistributedRateLimiter(store, ...)`: token bucket, sabit pencere, kayan pencere; bölüm (kiracı/IP) desteği.
  - `RedisRateLimitStore`: tek atomik Lua betiği; saat olarak Redis `TIME` kullanılır.
  - Redis erişilemezken sınırsız geçiş yerine pod başına yerel sınıra düşülür.
  - `InMemoryDistributedRateLimitStore`: aynı algoritmalar; Lua ile eşdeğerliği gerçek Redis'te testle doğrulandı.
- **`Aegis.Resilience.AspNetCore`** (AspNetCoreRateLimit / WebApiThrottle eşdeğeri, .NET 8+):
  - **Gelen istek hız sınırlama:**
    - bölümleme: IP / başlık / kullanıcı
    - `GET:/api/*` gibi uç nokta desenleri, çoklu kural
    - beyaz listeler: IP ve CIDR, istemci, uç nokta
    - `RateLimit-Limit` / `RateLimit-Remaining` / `Retry-After` başlıkları, özel red yanıtı
    - `appsettings.json`'dan yeniden yükleme; geçersiz yeni ayar yüklenmez
    - Redis deposuyla küme genelinde tek kota
  - **Uç nokta başına Aegis boru hattı** (`RequireAegisPipeline` / `[AegisInboundPipeline]`):
    - Gelen isteğe eşzamanlılık sınırı, zaman aşımı ve devre kesici uygular; 429 / 504 / 503 döner.
    - Zaman aşımı uç noktayı gerçekten iptal eder.
    - Retry/Hedging içeren boru hattı reddedilir, çünkü isteği sunucuda yeniden çalıştırmak güvenli değildir.

### Performans — rakip paketlere karşı ölçümle bulunan ve giderilen kayıplar

İlk ölçümler üç kategoride Aegis aleyhine çıktı:
- Yeni `EcosystemBenchmarks`'ta (Polly v7 + Polly.Caching.Memory, Polly.RateLimiting, AspNetCoreRateLimit) token bucket
  ve art arda hata devresi.
- 64 iş parçacıklı eşzamanlı yükte eşzamanlılık sınırlayıcı.

Nedenleri bulunup giderildi; hiçbir davranış değişmedi.

| Kategori | İlk ölçüm | Son ölçüm |
|---|---|---|
| Token bucket (Polly.RateLimiting) | 1,06× (yavaş) | **0,73×** |
| Art arda hata devresi (Polly v7) | 1,55× (yavaş) | eşit: 0,94–1,08× (dört koşu; 0 B'ye karşı 496 B) |
| Cache isabeti (Polly.Caching.Memory) | 0,98× | **0,70×** (0 B'ye karşı 384 B) |
| Dağıtık sınırlayıcı, bellek içi depo | 936 ns | **188 ns** |
| Sunucu tarafı hız sınırı (AspNetCoreRateLimit) | 0,41× | **0,22×** |
| Eşzamanlılık sınırlayıcı (Polly 8.8, 64 iş parçacığı) | 0,95–1,34× | **0,14×** (tek iş parçacığında 0,31×) |

- **Senkron hızlı yollar:** Token bucket, devre kesici, cache ve dağıtık sınırlayıcı stratejileri tamamen `async`
  metottu. İzin varken, kapalı devrede ya da cache isabetinde bile her çağrı fazladan bir async durum makinesinden
  geçiyordu. Artık en sık yol senkron; bekleme, olay bildirimi ve dış depo yolları async kaldı.
- **Devre kesici:** Sonuç kaydı, kilitli senkron kayıt ve yalnızca durum değişince çalışan async bildirim olarak ikiye
  ayrıldı.
- **Kilit tipi:** Her çağrıda alınan kilitler .NET 9+'da `System.Threading.Lock` oldu (tek tanım: `AegisLock`); eski
  hedeflerde `object`. Polly v8 devre kesicisine karşı oran 0,65× → 0,41×.
- **Dağıtık depo:** Bellek içi hız sınırlayıcı deposu her çağrıda `ConcurrentDictionary.Count` okuyordu. Bu özellik
  sözlüğün tüm kilitlerini alır; anahtar sayısı artık ayrıca tutuluyor.
- **Eşzamanlılık sınırlayıcı:** İzinler kilitsiz sayılıyor (`Interlocked`). `SemaphoreSlim` yalnızca kuyrukta bekleyen
  varken uyandırma sinyali. 64 iş parçacığı altında semafor kilidinin çekişmesi giderildi.
  - Canlı küçültme yeni girişlere hemen uygulanır; uçuştaki işlemler kesilmez (AEGIS-112 sözleşmesi korunur).
  - Kuyruk yolu için ayrı test sınıfı eklendi: uyandırma, zaman aşımı, bekleme sırasında iptal, yoğun yükte limit.

### Değişti (davranış aynı)

- `Aegis.Resilience.Extensions.Telemetry` yalnızca .NET 8+ hedefler. Dayandığı Microsoft telemetri paketleri .NET
  Framework'ü desteklemiyor ("doesn't support net462" uyarısı); Microsoft'un kendi zenginleştiricisi de aynı kapsamda.
  1.1.0'daki netstandard2.0/net462 derlemesi desteklenmeyen bağımlılıklara dayanıyordu.
- Redis depolarının bağlantı yönetimi (arka plan bağlantısı, işlem süre sınırı, erişilebilirlik) ortak bir iç sınıfta
  toplandı. Devre kesici deposunun davranışı değişmedi; gerçek Redis matrisinde doğrulandı.
- HTTP handler'larının kuralları (geçici kodlar, yeniden gönderim, son yanıt) tek bir iç nesnede toplandı. Standart
  handler ve DI bağlamlı handler aynı yeniden yüklenebilir çekirdeği paylaşıyor.
- `AegisTelemetryConfiguration.Apply` ve `AegisCallbacks.InvokeSafely(Action, ...)` genel API oldu. Böylece DI dışında
  kurulan boru hatlarına da aynı telemetri verilebiliyor.

### Test ve kalite

- Önceki "0 uyarı" raporu eksikti: kullanılan filtre kodsuz uyarıları ve `xUnit1031`'i göstermiyordu. Tüm uyarılar
  giderildi ve tüm çözüm gerçekten 0 uyarı, 0 hatayla derleniyor. Engellemeyen `.Result` okumaları tek bir test
  yardımcısında toplandı.

## [1.1.0] — Platform kapsamı, AOT, API sözleşmesi ve kalan eşitlik boşlukları

Polly v8 ve Microsoft.Extensions.(Http.)Resilience karşısındaki son boşluklar kapandı. Hiçbir mevcut API veya
davranış kaldırılmadı; yeni özellikler geriye uyumlu olduğu için sürüm minor (1.1.0) artırıldı. Yeni paket:
`Aegis.Resilience.Extensions.Telemetry`.

### Eklendi — Platform

- **.NET Framework 4.6.2+ ve netstandard2.0:** Tüm paketler (Dashboard hariç; ASP.NET Core'a bağlıdır)
  `net8.0;net9.0;net10.0;netstandard2.0;net462` hedefler (Polly/Microsoft ile aynı kapsam).
  - Kaynak kod iki dünyada aynıdır. Eksik BCL API'leri dahili polyfill'lerle (C# 14 extension üyeleri,
    `src/Shared/Polyfills`) ve resmi `Microsoft.Bcl.TimeProvider` gibi paketlerle karşılanır. Polyfill'ler yalnızca
    eski hedeflerin ikililerine girer.
  - .NET 8+'da varsayılan arayüz üyesi olan 16 çalıştırma biçimi eski hedeflerde aynı imzalı genişletme metodudur.
    `AegisPipeline` için yine closure'suz hızlı yola yönlenir.
  - Yeni `tests/Aegis.CompatibilityTests` gerçek .NET Framework 4.8'de 16 uçtan uca senaryo koşar:
    stratejiler, sahte saat, telemetri/ILogger, DI, HTTP handler'ları, dağıtık devre.
- **Native AOT ve trimming:** .NET 8+ hedeflerinde `IsAotCompatible`; trim/AOT uyarıları hata sayılır.
  - DispatchProxy tabanlı AOP açıkça `[RequiresDynamicCode]` / `[RequiresUnreferencedCode]` ile işaretli.
  - Yapılandırma bağlama kaynak üreteciyle yapılır.
  - Dashboard `JsonSerializerContext` ve `RequestDelegate` uç noktalarıyla AOT uyumludur.
  - Doğrulama: `tests/Aegis.AotSmokeTest`, .NET çalışma zamanı içermeyen imajda 7 senaryo (sıfır uyarı).
- **Strong naming:** Tüm derlemeler `Aegis.snk` ile imzalı (public key token `607bc7b3658f794d`).
- **Genel API sözleşmesi:** `Microsoft.CodeAnalysis.PublicApiAnalyzers` eklendi ve 10 paketin API'si
  `PublicAPI/*.txt` dosyalarına kilitlendi. Kazara kırıcı değişiklik derlemeyi kırar.

### Eklendi — Özellik

- **Standart HTTP işleyicide uç nokta başına boru hattı** (Microsoft `SelectPipelineByAuthority` eşdeğeri):
  - `SelectPipelineByAuthority()` ve `SelectPipelineBy(selector)`: her authority kendi devre kesicisini ve
    eşzamanlılık sınırını alır.
  - `MaxPipelines` ile kardinalite koruması.
  - `StateProvider` ile seçici birlikte kullanılırsa fail-fast doğrulama hatası verilir.
- **Standart hedging işleyicisinde yapılandırma yeniden yükleme:** `IConfigurationSection` değişince çalışma zamanı
  yeniden kurulur. Uçuştaki istekler eski nesille biter; geçersiz ayar yüklenmez. Genel `Reloadable<T>` tipi
  standart handler, hedging handler ve `ReloadableAegisPipeline` tarafından ortak kullanılır.
- **Devre sağlık bilgisi:** `CircuitBreakerEventContext.Health` (`CircuitHealth`: başarı/hata/yavaş çağrı sayısı,
  `Throughput`, `FailureRate`). Microsoft `BreakDurationGenerator` sağlık argümanları eşdeğeridir.
- **Ağırlıklı kaos sonuçları:** `ChaosOutcomeGenerator` (`AddException<T>`, `AddException`, `AddResult`; ağırlıklı)
  ve `AddChaosOutcome(rate, generator)`. Polly `OutcomeGenerator` eşdeğeridir.
- **`Aegis.Resilience.Extensions.Telemetry` paketi** (Microsoft `AddResilienceEnricher` eşdeğeri):
  - Standart metriklere `error.type` (IExceptionSummarizer), `request.name` ve `request.dependency.name`
    etiketlerini ekler.
  - `context.SetRequestMetadata(...)` kullanılabilir; HTTP isteğindeki `RequestMetadata` da otomatik okunur.
- **Dashboard:** Manuel izole/sıfırla işlemleri `IManuallyControllableCircuit` üzerinden yapılır; yerel ve dağıtık
  devrelerde aynı şekilde çalışır.

### Performans

- **Yüksek eşzamanlılık:** Yeni `ConcurrentLoadBenchmarks` (64 işçi, aynı boru hattı) Aegis'in Retry'da 1,93×, CB'de
  1,37× Polly'den yavaş olduğunu ortaya çıkardı.
  - `AegisContextPool` ve `CancellationTokenSourcePool` artık iş parçacığına özel yuva kullanıyor.
  - Devre kesicinin kapalı durumu kilitsiz.
  - Sonuç: 5 eşzamanlı senaryonun hepsinde önde (0,08–0,79×).
- **Async durum makinesi havuzlama (.NET 8+):** İç katmanlar `PoolingAsyncValueTaskMethodBuilder` kullanıyor:
  Retry/CB çekirdekleri, Timeout iyimser yolu, Outcome dönüştürücüleri.
  - Gerçekten async geri çağrıda bellek Polly'nin yarısına iniyor (1.176 B'ye karşı 2.359 B).
  - Kullanıcıya dönen `ValueTask`'lar havuzlanmıyor, bu yüzden davranış değişmedi.
- **Toplam (BenchmarkDotNet):** 19 senaryonun 18'inde Polly 8.8'den hızlı, 1'inde eşit. Bellekte hiçbir senaryoda
  fazla değil (`benchmarks/BENCHMARK.md`).

### Test

- `Timeout_PooledTokenSources_NeverLeakCancellationToNextCalls` testinin 3. adımı, 50 ms'lik zaman aşımı ile 30 ms'lik
  çağıran iptalini yarıştırıyordu ve ağır paralel yükte bir kez düştü. Adım uzun zaman aşımlı ayrı bir boru hattına
  taşındı; doğruladığı davranış (çağıran iptalinin havuzlanmış kaynağa akması) aynı.

## [1.0.12] — Strateji, registry ve HTTP eşitliği (Aşama 3, 4, 5)

Polly v8 ve Microsoft.Extensions.Http.Resilience ile özellik eşitliği tamamlandı (ayrıntı: `POLLY-ANALIZ.md`).
Hiçbir mevcut API veya davranış kaldırılmadı. İki yeni paket var: `Aegis.Resilience.RateLimiting` ve `Aegis.Resilience.Testing`.

### Eklendi — Aşama 3 (stratejiler)

- **Circuit Breaker:** `CircuitBreakerManualControl` ve `CircuitBreakerStateProvider` (Polly eşdeğeri).
  - İkisi de kurulumdan önce oluşturulup seçeneklere verilir.
  - Kontrol birden çok devreyi yönetir; yerel ve dağıtık (Redis) devre kesicide çalışır.
  - `isIsolated: true` ile bağlanan devre izole başlar.
  - Dağıtık devre, kurucuda uzak depoya bloklayan yazma yapmaz; izolasyonu ilk çağrıda uygular.
- **`Aegis.Resilience.RateLimiting` paketi** (Polly.RateLimiting eşdeğeri): `AddRateLimiter(RateLimiter)`,
  `AddRateLimiter(PartitionedRateLimiter<AegisContext>)`, `AddTokenBucketRateLimiter`, `AddFixedWindowRateLimiter` ve
  tam denetimli `RateLimitingBridgeOptions`. Lease her durumda iade edilir; `RetryAfter` metaverisi istisnaya taşınır.
  `Core` bağımlılıksız kalır.
- **Hedging:**
  - `ActionGenerator` ile her denemede farklı işlem çalıştırılabilir.
  - Sonuca göre hedging: `ShouldHandleResult` / `ShouldHandleOutcome`. Atılan kötü sonuçlar dispose edilir; hepsi
    kötüyse son sonuç döner.
  - `HedgingOptions.AttemptNumberKey` ile deneme numarası, `MaxAttemptsKey` ile çağrı bazında deneme üst sınırı.
- **Ayrık kaos stratejileri:** `AddChaosFault`, `AddChaosLatency`, `AddChaosOutcome`, `AddChaosBehavior`. Birleşik
  `AddChaos` korunuyor.
- **Tipli boru hattı:** `IAegisPipeline<TResult>`, `builder.Build<TResult>()` ve `pipeline.AsTyped<TResult>()`. Aynı
  sıfır tahsisli çekirdeği kullanır.

### Eklendi — Aşama 4 (registry, yeniden yükleme, test)

- **`AegisPipelineRegistry<TKey>` / `IAegisPipelineProvider<TKey>`:** Statik anahtar (`TryAddBuilder`), dinamik anahtar
  (`DynamicBuilder`, ör. kiracı başına ayrı devre ve kota) ve `GetOrAddPipeline`. `MaxDynamicPipelines` kardinalite
  koruması Polly'de yok. DI: `AddAegisPipeline<TKey>(key, ...)` ve `AddAegisPipelines<TKey>(...)`.
- **`ReloadableAegisPipeline`:** Tüm boru hattını yeniden kurar.
  - Uçuştaki çağrılar eski nesille tamamlanır; eski nesil son çağrı bitince dispose edilir.
  - Kurulum hatasında eski nesil çalışmaya devam eder (`LastReloadError`).
  - DI: `AddAegisPipeline<TOptions>(name, (b, options, sp) => ...)`; `IOptionsMonitor` değişince yeniden kurar
    (Polly `EnableReloads` eşdeğeri).
- **`Aegis.Resilience.Testing` paketi** (Polly.Testing eşdeğeri): `GetPipelineDescriptor()`.
  - `Strategies`, `FirstStrategy`, `IsReloadable` ve `GetOptions<T>()` sunar.
  - `AddPipeline` ile iç içe kurulan boru hatları düzleştirilir.
  - Stratejilerin kurulum seçenekleri `AegisStrategy.Options` ile okunur.

### Eklendi — Aşama 5 (HTTP)

- **`AddStandardAegisHandler(Action<AegisHttpStandardResilienceOptions>)` ve `(IConfigurationSection)`:**
  - Zincir ve varsayılanlar Microsoft standart handler ile aynı: eşzamanlılık → toplam timeout → retry → CB → deneme
    timeout.
  - Tutarlılık doğrulaması fail-fast: deneme timeout'u toplamdan küçük olmalı; CB örnekleme penceresi deneme
    timeout'unun en az 2 katı olmalı.
  - Yapılandırma bölümü değişince boru hattı yeniden kurulur; geçersiz yeni ayar yüklenmez.
- **`AddStandardAegisHedgingHandler`** (Microsoft standart hedging eşdeğeri):
  - Toplam timeout + sonuca göre hedging.
  - Uç nokta (authority) başına eşzamanlılık sınırı, devre kesici ve deneme timeout'u.
  - Sıralı (`OrderedGroups`) ve ağırlıklı (`WeightedGroups` + `SelectionMode`) yönlendirme grupları.
  - İstek yolu ve sorgusu korunur; deneme sayısı grup sayısını aşmaz; idempotent olmayan istek hedging'e girmez.
  - `IConfigurationSection`'dan bağlanabilir.
- Eski parametreli `AddStandardAegisHandler(totalTimeout:, ...)` ve diğer tüm HTTP işleyicileri aynen duruyor.

### Düzeltildi

- Aşama 2'deki hedging `DelayGenerator` testinde sahte saat, zamanlayıcı kaydolmadan ilerletilebiliyordu; bu yüzden
  test yük altında aralıklı başarısız oluyordu. Ürün hatası değildi; test saati adım adım ilerletecek şekilde düzeltildi.

### Testler

- Yeni test sınıfları: `StrategyParityStage3Tests`, `RegistryReloadTestingStage4Tests`, `HttpStandardParityStage5Tests`.
  Toplam 352 test, 3 hedef çatıda (.NET 8/9/10) geçiyor.

## [1.0.11] — Telemetri ve olay eşitliği (Aşama 2), Aşama 1'in tamamlanması, Hedging belleği

Hiçbir mevcut API, davranış veya metrik kaldırılmadı. Eski `aegis.*` sayaçları aynen yayınlanıyor.

### Eklendi

- **Standart telemetri** (Polly `Polly.Extensions` telemetrisinin eşdeğeri):
  - Olay dinleyicisi soyutlaması `AegisTelemetryListener`, olay yapısı `AegisTelemetryEvent` ve önem düzeyleri
    `AegisEventSeverity`.
  - Olay adları Polly ile aynı (`AegisEventNames`); etiketler de aynı (`AegisTelemetryTags`).
  - Yeni metrikler: `aegis.strategy.events`, `aegis.strategy.attempt.duration`, `aegis.pipeline.duration`. Etiketleri:
    `event.name`, `event.severity`, `pipeline.name`, `strategy.name`, `operation.key`, `exception.type`,
    `attempt.number`, `attempt.handled`.
  - Metrik zenginleştiricileri (`MeteringEnrichers`) ve önem sağlayıcı (`SeverityProvider`).
  - Kayıt yolları: `AegisPipelineBuilder.TelemetryOptions`, `.WithTelemetry(...)` ve her strateji için
    `AegisStrategy.Telemetry`.
- **Olay yayan stratejiler:** Retry (`ExecutionAttempt`, `OnRetry`), Timeout, Circuit Breaker (yerel ve dağıtık), tüm
  hız/eşzamanlılık sınırlayıcıları, Hedging, Fallback, Chaos (4 tür), Cache, Stale Fallback ve Collapser. Boru hattı
  düzeyinde `PipelineExecuted` olayı ve süresi de yayınlanıyor.
- **`ILogger` günlüğü** (DI paketi): `AegisLoggingTelemetryListener`, Polly ile aynı mesaj biçimi ve EventId'lerle.
  `AddAegisPipeline` ile kurulan boru hatlarına `ILoggerFactory` varsa otomatik takılır; `ConfigureAegisTelemetry`
  ile kapatılabilir, sonuç biçimleyici ile maskelenebilir, ek dinleyici veya zenginleştirici eklenebilir. Sorunsuz
  denemeler `Debug` düzeyinde loglanır (Polly: `Information`; gürültü azaltıldı).
- **`AegisContext.ContinueOnCapturedContext`** (Polly eşdeğeri; Aşama 1'in son maddesi): tüm stratejilerdeki
  `await`'ler bu bayrağa uyuyor.
- **Fallback** (Aşama 3 maddeleri):
  - Sonuca göre, istisnasız yedek: `ShouldHandleResult`, `ShouldHandleOutcome`.
  - Hem istisnayı hem sonucu gören `FallbackAction`.
  - `OnFallback` bildirimi.
  - Eski `ShouldHandle` ve `FallbackHandler` aynen çalışıyor.
- **Hız sınırı:** `RateLimitRejectedException.RetryAfter` (token bucket ve kayan pencerede hesaplanıyor) ve tüm
  sınırlayıcılarda `OnRejected` bildirimi. Red yolu tek bir ortak yardımcıya taşındı (DRY).
- **Hedging:** `DelayGenerator` (deneme başına gecikme) ve `OnHedging` bildirimi.
- **Chaos:** `EnabledGenerator`, `InjectionRateGenerator` (çağrı bazında) ve `OnInjected` bildirimi.

### Performans

- **Hedging başarı yolu 144 B → 0 B:** birincil denemenin alt bağlamı havuzdan kiralanıyor; korelasyon kimliği
  ebeveynden tembel okunuyor. Görev asenkron yola geçerse kimlik o an sabitleniyor, yani izolasyon ve ortak kimlik
  garantileri (AEGIS-110/111/135) aynen korunuyor ve testle doğrulanıyor.
- **Açık devre reddi 1.440 B → 1.288 B** (Polly: 1.312 B): red mesajı durum başına bir kez biçimlendiriliyor.
- Telemetri dinleyicisi yokken başarı yolu 0 B kalıyor (testle korunuyor).
- **Ölçüm (Polly 8.8.0):**
  - Süre: 14 senaryonun 12'sinde Aegis daha hızlı, 2'sinde eşit (fark %2'nin altında).
  - Bellek: Hiçbir senaryoda Polly'den fazla ayırmıyor; 11 senaryoda 0 B.
  - Hedging 0,41× ve 0 B. Ayrıntı: `benchmarks/BENCHMARK.md`.

### Testler

- Yeni test sınıfları: `ContinueOnCapturedContextTests`, `HedgingContextPoolingTests`, `TelemetryParityTests`,
  `EventCallbackParityTests`. Toplam 324 test, 3 hedef çatıda (.NET 8/9/10) geçiyor.

## [1.0.10] — Polly v8 çekirdek API eşitliği (Aşama 1)

Kaynak: Polly `main` ve dotnet/extensions `main` genel API yüzeyi (`POLLY-ANALIZ.md`). Hiçbir mevcut API, davranış
veya özellik kaldırılmadı; tüm eklemeler geriye uyumludur.

### Eklendi

- **Tüm çalıştırma biçimleri** (`AegisPipeline` + `IAegisPipeline` varsayılan gövdeleri):
  - `ExecuteAsync(Func<CancellationToken, ValueTask<T>>, CancellationToken)` ve dönüşsüz eşi.
  - Closure'suz `TState` biçimleri: `ExecuteAsync((ctx, state) => ..., state, ctx?)`, `ExecuteAsync((state, ct) => ..., state, ct)`.
  - Fırlatmayan `ExecuteOutcomeAsync` (iki biçim): başarısızlık `Outcome<T>` olarak döner.
  - Senkron `Execute`: `Func<T>`, `Action`, `Func<AegisContext, T>`, `Action<AegisContext>`, `CancellationToken`
    ve `TState` biçimleri.
  - Arayüzü kendisi uygulayan (özel) boru hatları kırılmaz: yeni biçimler varsayılan gövdeyle eski iki metoda düşer.
- **`TimeProvider` desteği:** `AegisPipelineBuilder.TimeProvider` / `.WithTimeProvider(...)`, `AegisStrategy.TimeProvider`
  ve `UseTimeProvider(...)`. Retry, Timeout (iyimser ve kötümser), Circuit Breaker (açılma süresi + örnekleme penceresi),
  Hedging, Rate Limiter, Sliding Window, Adaptive Concurrency, Cache, Stale Fallback ve Chaos gecikmesi sahte saatle
  beklemeden test edilebilir. Sistem saatinde havuzlanmış CTS yolu aynen korunur.
- **`Randomizer`:** `RetryOptions.Randomizer`, `ChaosOptions.Randomizer` (deterministik jitter ve enjeksiyon).
- **Bağlam farkındalıklı async koşullar:** `AegisPredicate`, `OutcomeArguments<T>` (sonuç + bağlam + deneme no),
  `PredicateArguments`, akıcı `AegisPredicateBuilder` (`Handle<T>`, `Handle<T>(filtre)`, `HandleInner<T>`,
  `HandleAnyException`, `HandleResult<T>(koşul | değer)`). `RetryOptions.ShouldHandleOutcome` ve
  `CircuitBreakerOptionsBase.ShouldHandleOutcome` ile kullanılır. Değer tipi sonuç kuralları kutulama yapmaz
  (Polly'de bu yalnızca tipli pipeline ile mümkün). İptal ve açık devre reddi her durumda hariç tutulur.
- **Tipli özellik anahtarları:** `AegisPropertyKey<T>`, `TryGetProperty/SetProperty/GetPropertyOrDefault` aşırı yüklemeleri
  (string anahtarlarla aynı sözlük).
- **`AegisContext.OperationKey`** (alt bağlamlara aktarılır, havuza dönüşte temizlenir).

### Düzeltildi

- Async biçimler başarısızlığı hiçbir koşulda eşzamanlı fırlatmaz; hata her zaman dönen görevin içindedir.
- AEGIS-134 (çağıranın token'ı) kuralı çekirdeğe taşındı: çağıranın token'ı stratejiler çalışmadan önce yakalanıyor,
  böylece `ExecuteOutcomeAsync` dahil tüm biçimlerde geçerli.

### Performans

Aegis, Polly'ye karşı ölçülen 14 senaryonun hepsinde eşit ya da daha hızlı: 9 eski ve 5 yeni çalıştırma biçimi
(ayrıntı: `benchmarks/BENCHMARK.md`). Timeout 1,09× → 0,98×, Circuit Breaker 1,02× → 0,84×. Yeni biçimler
0,75–0,80× ve 0 B; Polly aynı işlerde 48–56 B ayırıyor. Çekirdek yeniden yazımında boş pipeline'da görülen gerileme
(1,18×) ölçümle yakalanıp giderildi (0,96×).

- Terminal katmandaki çift try/catch kaldırıldı; koruma yalnızca `ExecuteOutcomeAsync` girişinde.
- `CancellationTokenSourcePool`: iş parçacığı başına yuva.
- Circuit Breaker: sistem saatinde sanal çağrısız zaman; yavaş çağrı eşiği yoksa süre ölçülmüyor; başarı kaydında
  gereksiz pencere taraması ve pencere yeniden hesabı kalktı (davranış aynı).

### Testler

- `ExecutionApiParityTests` (20) ve `TimeAndPredicateParityTests` (17): her biçim, sahte saatle Retry/Timeout/CB,
  Randomizer, koşullar, TState ve koşullu Retry başarı yolunda sıfır tahsis. Toplam 301 test × 3 TFM geçiyor.

## [1.0.9] — Sıfır tahsisli, istisnasız strateji zinciri (Polly'yi yakalama ve geçme)

Sonuç (BenchmarkDotNet, aynı koşu, Polly.Core 8.8.0 referans): 9 senaryonun 6'sında Aegis daha hızlı (Retry 0,48×,
Hedging 0,56×, standart zincir 0,70×), 2'sinde eşit (devre kesici 1,02×, açık devre reddi 1,03×), yalnızca Timeout
%9 geride. Başarı yolunda çoğu senaryoda **0 bayt** tahsis (Polly 24–48 B). Ayrıntı: `benchmarks/BENCHMARK.md`.

### Eklendi

- **`AegisStrategy` taban sınıfı ve `Outcome<TResult>` (AEGIS-166/167).** Yerleşik 15 stratejinin tamamı (yerel ve
  dağıtık devre kesici dahil) yeni hızlı yolu kullanıyor:
  - Sonraki adım bir struct "durum" olarak geçiyor ve statik, önbellekli bir delegate kullanılıyor; katman başına
    closure tahsisi (96 B) kalktı.
  - Başarısızlık katmanlar arasında `Outcome<T>` ile taşınıyor; istisna yalnızca en dışta bir kez fırlatılıyor.
    Açık devre reddi, hız sınırı reddi, zaman aşımı ve kaos hataları içeride hiç fırlatılmıyor. Kullanıcı
    istisnalarının özgün yığın izi korunuyor.
  - Strateji içinden sızan beklenmedik istisnalar katman sınırında yakalanıp sonuca çevriliyor; dış stratejiler
    bunu eskisi gibi normal bir başarısızlık olarak görüyor.

### Değişmedi (uyumluluk)

- **`IAegisStrategy` arayüzü aynen duruyor.** Kullanıcıların yazdığı özel stratejiler eski yoldan (closure'lı,
  istisna fırlatan) çalışmaya devam ediyor ve yerleşik stratejilerle karışık kullanılabiliyor. Test ediliyor: özel
  strateji içteki Retry'ın tükenen istisnasını `try/catch` ile yakalayabiliyor. Stratejiler üzerinde doğrudan
  çağrılan `ExecuteAsync` de aynı imzayla çalışıyor.

### Performans

- **AEGIS-168 — Telemetri yalnızca dinleyici varken:** Boru hattı her çağrıda süre ölçüp iki metrik yazıyordu.
  Artık `Instrument.Enabled` kontrol ediliyor. Boş boru hattı 1,84× → 0,93×.
- **AEGIS-169 — Hedging:** Birincil deneme eşzamanlı tamamlanırsa `Task`, liste, zamanlayıcı ve deneme nesnesi hiç
  oluşturulmuyor; deneme CTS'leri havuzdan geliyor ve deneme gerçekten bittiğinde iade ediliyor. 1,35× → 0,56×.
- **AEGIS-170 — Devre kesicide kutulama:** `IsFailureResult(object?)` her başarılı çağrıda sonucu kutuluyordu
  (`ShouldHandleResult` tanımlı olmasa bile; değer tiplerinde 24 B). Artık generic.

### Düzeltildi

- **StaleFallback arka plan yenilemesinin alt bağlamı geç oluşturuluyordu.** Alt bağlam `Task.Run` içinde
  oluşturuluyordu. Bu kod çalıştığında çağıranın havuzlanmış bağlamı çoktan havuza dönüp sıfırlanmış olabiliyor,
  yenileme kiracı/anahtar bilgisini kaybediyordu. Artık bağlam havuza dönmeden önce kopyalanıyor.
- **Hedging'de `ValueTask`'ın iki kez tüketilmesi (CA2012).** Hızlı yol yazılırken analizör yakaladı: eşzamanlı başarısız
  birincil denemenin sonucu okunduktan sonra `AsTask()` ile ikinci kez tüketiliyordu. `IValueTaskSource` ile
  veri bozulmasına yol açabilirdi. Okunmuş sonuç artık `Task.FromResult` ile sarılıyor.

### Testler (275 → 279)

- `OutcomeFastPathTests`: özel strateji uyumluluğu, yığın izinin korunması, açık devre reddinin tip/mesajı ve
  **başarı yolunda sıfır tahsis** (Timeout+Retry+CB+Eşzamanlılık+Timeout zinciri, çağrı başına < 8 B;
  eskiden ~400–850 B).
- `AdaptiveConcurrency_ConcurrentReads`: tek çekirdekte adaptif limitin tasarım gereği daralıp reddetmesi artık
  geçerli sonuç sayılıyor (1 CPU Linux konteynerde 15 koşuda 1 kez görülmüştü).

## [1.0.8] — Polly ile performans karşılaştırması ve internetsiz demo

### Performans (benchmark ile bulundu — bkz. `benchmarks/BENCHMARK.md`)

- **AEGIS-164 — 1.0.7'de eklenen bir performans gerilemesi giderildi.** Retry ve Hedging her çağrıda "ek deneme
  bastırılmış mı?" işaretini `context.Properties` üzerinden okuyordu (AEGIS-152 düzeltmesiyle geldi). Bu erişim,
  hiç kullanılmasa bile bağlamda bir `ConcurrentDictionary` oluşturuyordu. Havuza iadedeki `Clear()` da çekirdek
  sayısı kadar kilidin hepsini alıyordu. Retry'ın başarı yolu Polly'den **4,45 kat** yavaş ölçüldü (1.215 ns).
  Değişiklikler:
  - Okumalar artık sözlük oluşturmayan `TryGetProperty` ile yapılıyor (Retry, Hedging, Cache ve Partitioned'ın
    varsayılan anahtar yolları).
  - Sözlük tek kilitle (`concurrencyLevel: 1`) oluşturuluyor; iş parçacığı güvenliği korunuyor.
  - Havuza iadede sözlük yalnızca doluysa temizleniyor.

  Sonuç: Retry 4,45× → **0,66×** (Polly'den hızlı).
- **AEGIS-165 — Timeout başarı yolunda tahsis yok.** Her çağrı yeni bir bağlı `CancellationTokenSource` ve zamanlayıcı
  ayırıyordu (240 B). Artık iptal edilmemiş kaynaklar `TryReset()` ile sınırlı bir havuzda yeniden kullanılıyor ve
  çağıranın token'ı bağlı CTS yerine kayıt (`UnsafeRegister`) ile bağlanıyor. İptal edilmiş kaynak havuza asla
  dönmüyor. Yeni test AEGIS-163 bunu 20 zaman aşımı turu × 50 çağrıyla doğruluyor; çağıran iptalinin
  `OperationCanceledException.CancellationToken` ile akmaya devam ettiği de test ediliyor.
  - Standart zincir 1,54× → **0,69×**, bellek 856 → 408 B.

Kalan fark dürüstçe belgelendi: Polly bellek tahsisinde açık ara önde. Aegis strateji katmanı başına 96 B ayırıyor
(closure); bunu kapatmak `IAegisStrategy` arayüzünde kırıcı bir değişiklik gerektiriyor. İstisna yolları (açık devre
reddi) Polly'den 1,6 kat yavaş.

### Showcase

- **İnternet bağımlılığı kaldırıldı.** #09, #16, #17 ve #19 artık httpbin.org / postman-echo.com yerine showcase sürecinde
  çalışan yerel demo arka ucunu (`Common/DemoBackend.cs`, iki port = iki veri merkezi) çağırıyor. `--network none`
  ile dış ağı kapalı bir konteynerde 4 özelliğin de çalıştığı doğrulandı. `test-all.ps1`: 29/29, **0 atlanan**.
- **Eski #09 demosu yanıltıcıydı.** İki uç nokta `httpbin.org/delay/3` ve `/delay/0` olarak veriliyordu. Ancak
  `MultiEndpointHedgingHandler` yalnızca şema/host/portu değiştirip isteğin yolunu koruduğu için ikisi de aynı hızlı
  `httpbin.org/get`'e gidiyordu. Yani hedging gerçekte hiç gösterilmiyordu; test hızlı yanıta bakıp "PASS" diyordu.
  Artık yavaşlık veri merkezinin kendi davranışında; test kazananın gerçekten hızlı uç (`dc-b`) olduğunu doğruluyor.
- **#17 artık deterministik.** Arka uç aynı Idempotency-Key için 500, 500, 200 dönüyor ve her denemede gövdenin ilk
  denemedekiyle birebir aynı olup olmadığını SHA-256 ile ölçüyor. Test hem `attempts=3` hem `bodyIdentical=true`
  doğruluyor (eskiden httpbin rastgele döndüğü için başarı garanti değildi).

## [1.0.7] — Docker araçlarıyla genişletilmiş testlerin bulduğu hatalar

Yeni test altyapısı: Linux (Docker, 1 CPU / 512 MB dahil), Redis 6.2 / 7 / 8.10, Valkey 8.1 ve şifreli Redis
uyumluluk matrisi, Toxiproxy ile gerçek ağ kaosu, Trivy güvenlik taraması, OpenTelemetry Collector ile uçtan uca
metrik doğrulaması ve k6 soak testi. Test sayısı 264 → 274.

### Düzeltildi

- **AEGIS-161 — Yavaş (çökmemiş) Redis tüm uygulamayı yavaşlatıyordu.** Toxiproxy ile Redis'e 1,5 sn gecikme
  verildiğinde dağıtık devre kesiciden geçen **her çağrı 3 sn** sürdü (durum okuma + sonuç yazma). Fail-open
  yalnızca hata olunca devreye giriyordu, yavaşlıkta girmiyordu; koruma katmanı arızanın kendisi oluyordu.
  - Artık her Redis işlemi bir süre sınırıyla kesiliyor: `RedisCircuitBreakerStateStore.DefaultOperationTimeout`
    = 250 ms, ayarlanabilir (`AddAegisRedisStateStore(..., operationTimeout: ...)`). Sınır aşılınca yerel duruma
    düşülüyor.
  - Aynı senaryoda çağrı süresi 3 sn'den 1 sn'nin altına indi.
- **AEGIS-160 — Yanlış Redis adresi veya kesintisi sessiz bir "split-brain" üretiyordu.** Kümede doğrulandı:
  pod-1 devreyi açtı, pod-2 bozuk arka uca trafik göndermeye devam etti ve `/health` iki pod'da da "Healthy"
  dedi. Dağıtık korumanın kapalı olduğunu fark etmenin hiçbir yolu yoktu. Artık:
  - `ICircuitBreakerStateStore.IsAvailable` eklendi (varsayılan uygulamalı).
  - Redis deposu bu değeri gerçek bağlantı durumundan veriyor. Son 5 sn'de süre aşımı olduysa da `false`
    dönüyor; başarılı bir işlem bu işareti hemen temizliyor.
  - Dağıtık devre kesici bunu `IObservableSharedState` ile açıyor.
  - `AddAegisCheck` bu durumda `Degraded` ve "pod-yerel mod" açıklaması raporluyor
    (`SharedStateUnavailableStatus`).
- **AEGIS-159 — StaleFallback "anahtar başına tek yenileme" garantisi yük altında bozulabiliyordu.** Bir istek bayat
  girdiyi okuduktan hemen sonra askıya alınırsa, bu sırada başka isteğin yenilemesi bitip kilidi bırakabiliyordu.
  Uyanan istek eski anlık görüntüye bakıp önbellek zaten tazeyken ikinci bir yenileme başlatıyordu. İlk kez tek
  çekirdekli Linux konteynerde görüldü. Test kancasıyla deterministik yeniden üretildi: düzeltmesiz 2 yenileme,
  düzeltmeli 1. Artık yenileme hakkı alındıktan sonra girdinin değişip değişmediği kontrol ediliyor.
- **AEGIS-162 — Devre durum değişimi metriği etiketsizdi.** `aegis.circuitbreaker.state_changes.total` 7 yerde hiç
  etiket olmadan yazılıyordu. Hangi devrenin hangi duruma geçtiği panoda görülemiyor, "devre açıldı" alarmı
  kurulamıyordu. Artık `pipeline` ve `state` (`closed`/`open`/`half_open`/`isolated`) etiketleri var.
  OpenTelemetry Collector'da doğrulandı: `aegis_circuitbreaker_state_changes_total{pipeline="cluster-gateway",state="open"}`.
  Manuel `Isolate`/`Reset` işlemleri de etiketli sayılıyor.

### Test düzeltmeleri (kütüphane değişmedi)

Tek çekirdekli Linux konteyner, yalnızca test tasarımından kaynaklanan 4 aralıklı hata ortaya çıkardı:

- `AdaptiveConcurrency_ConcurrentReads`: Okuyucu iş parçacığı her değeri sınırsız bir torbaya ekliyordu ve 512 MB'da
  `OutOfMemoryException` alıyordu. Test patlayınca da okuyucu durdurulmuyor, sonsuza dek dönüp sonraki testlerin
  CPU'sunu çalıyordu. Artık anında kontrol ediliyor ve okuyucu `finally` içinde durduruluyor.
- `SyncOverAsync_…_ShouldNotDeadlock`: Kısa kuyruk süresi, iş parçacığı havuzu açlığında red üretiyordu. Bu
  kilitlenme değildir; artık bol kuyruk süresi veriliyor.
- `Telemetry_CountersAndDuration…` ve `CircuitBreaker_HalfOpenProbeFails…`: 30–50 ms'lik zamanlama payları tek
  çekirdekte aşılıyordu; paylar 10 katına çıkarıldı.
- Toxiproxy'deki `reset_peer`, .NET'in kendi iç yeniden denemesine takılıp Aegis'e ulaşmayabiliyordu; test
  yanıtın ortasında kesen `limit_data`'ya geçirildi.
- `StaleFallback_ShouldPreventStampede`: Sabit 200 ms bekleme, 1 CPU'da arka plan yenilemesi henüz başlamadan (0) doğruluyordu; artık önce yenilemenin görülmesi bekleniyor.
- Gerçek Redis semantik testleri (tek deneme, paylaşım) artık 5 sn işlem sınırı kullanıyor. Varsayılan 250 ms, 1 CPU + paralel yükte aşılıp tasarım gereği fail-open'a düşebiliyordu; yavaş Redis davranışı ayrı Toxiproxy testinde ölçülüyor.
- Root olmayan kullanıcıyla koşan Linux test imajı, NuGet önbelleği `/root` altında kaldığı için **0 test koşup
  sessizce "başarılı" dönüyordu**. Önbellek `/nuget`'a taşındı.

### Güvenlik

- `dotnet list package --vulnerable --include-transitive`: 7 paketin hiçbirinde bilinen açık yok.
- Trivy imaj taraması: `aegis-showcase` (Ubuntu 24.04 + .NET 10) içinde 0 yüksek/kritik açık.
- Trivy gizli bilgi taraması: depoda sızmış anahtar/şifre yok.
- Trivy yapılandırma taraması, demo Kubernetes manifestlerinde 12 bulgu verdi (root kullanıcı, yazılabilir kök
  dosya sistemi vb.). Hepsi giderildi: `runAsNonRoot`, `readOnlyRootFilesystem`, `allowPrivilegeEscalation: false`,
  tüm yetenekler düşürülmüş, seccomp `RuntimeDefault`. Sertleştirilmiş kümede senaryo paketi 25/25.

## [1.0.6] — Kubernetes küme testlerinin bulduğu hatalar

Showcase, kind ile kurulan 3 düğümlü bir Kubernetes kümesinde Redis ve 3 kopya olarak çalıştırıldı
(`AegisShowcase/k8s/`, bkz. `KUBERNETES.md`). Gerçek başlangıç sırası ve kesinti senaryoları, birim testlerinin
göremediği iki hatayı ortaya çıkardı. Test sayısı 260 → 264; net8/9/10 ve gerçek Redis'e karşı 0 hata.

### Düzeltildi

- **AEGIS-157 — Redis henüz yokken açılan uygulama her isteğe HTTP 500 dönüyordu.** `AddAegisRedisStateStore(string)`
  bağlantıyı StackExchange.Redis varsayılanı `abortConnect=true` ile ve senkron kuruyordu. Kubernetes'te pod'un
  Redis'ten önce hazır olması olağandır. Bu durumda depo oluşturulamıyor, istisna her isteğe sızıyor ve her
  istek bağlantıyı baştan deniyordu; istek başına 10–15 sn sürüp 500 dönüyordu. `/health` ise "Healthy"
  diyordu. Artık:
  - Bağlantı dizesi kurulumda ayrıştırılıyor (hatalı dize açılışta bildirilir).
  - `AbortOnConnectFail = false` ayarlanıyor.
  - Bağlantı **arka planda** kuruluyor. Hazır olana kadar depo beklemeden yerel duruma düşüyor. Deneme başarısız
    olursa bir sonraki kullanımda yeniden başlatılıyor.

  Kümede ölçülen: Redis yokken istekler ~15 sn/500 yerine anında 200 dönüyor. Redis sonradan kurulunca
  pod'lar **yeniden başlatılmadan** bağlanıp durumu paylaşıyor.
- **AEGIS-158 — Redis kesintisinde her çağrı ~5 sn gecikiyordu.** StackExchange.Redis bağlantı yokken komutları
  varsayılan olarak kuyrukta bekletir (zaman aşımına kadar). Artık `BacklogPolicy.FailFast` kullanılıyor ve
  depo anında yerel duruma düşüyor. Birim testinde kesinti sırasındaki 3 depo işlemi 18 sn'den 0,9 sn'ye indi;
  çözümleme dahil artık 0,5 sn sınırı altında.

  > `AddAegisRedisStateStore(IConnectionMultiplexer)` ile kendi çoklayıcınızı veriyorsanız aynı iki ayarı
  > (`AbortOnConnectFail = false`, `BacklogPolicy = BacklogPolicy.FailFast`) sizin yapmanız gerekir.

### Test düzeltmesi (kütüphane değişmedi)

- `DistributedCircuitBreaker_OverRealRedis_HalfOpenAllowsSingleProbeAcrossPods` aralıklı düşüyordu (15 turda 6).
  Sebep testteki bir yarıştı. podA'nın deneme isteği beklenmeden başlatılıyordu ve podA Redis'ten durumu okurken
  podB deneme hakkını önce kapabiliyordu. Tekillik yine korunuyordu; yanlış olan, testin "hakkı A alır"
  varsayımıydı. Artık test, A'nın geri çağrısı başladıktan sonra B'yi çağırıyor.
- Ayrıca yarışın kendisini doğrudan ölçen yeni test eklendi: 3 pod'dan 30 eşzamanlı istekte yalnızca **1**
  geri çağrı çalışıyor.
- Aynı süreçte önce "HalfOpen, TTL'in saniyeye yuvarlanmasından erken geliyor" diye yanlış bir teşhis konmuş ve
  Redis saatine (Lua `TIME`) dayalı bir değişiklik yapılmıştı. StackExchange.Redis `StringGetWithExpiry` için
  zaten milisaniye hassasiyetli `PTTL` kullandığından bu değişiklik hiçbir sorunu çözmüyordu. KISS gereği geri
  alındı; 1.0.5 biçimi (`"1|{retentionMs}"`) korunuyor.

## [1.0.5] — Kod incelemesi (code-review) düzeltmeleri

`src/` üzerinde yüksek seviyeli `code-review` ile 10 bulgu çıktı; hepsi düzeltildi ve her biri için başarısız olan
senaryoyu yeniden üreten test yazıldı (`CodeReviewFixesTests`, `RealRedisIntegrationTests`). Test sayısı 233 → 260.
Üç çerçevede (net8/9/10) ve gerçek Redis'e karşı 0 hata; showcase 1.0.5 paketleriyle 29/29.

### Değişti (Breaking)

- **AEGIS-149 — Request Collapser `KeySelector` zorunlu:** Varsayılan `ctx => ctx.CorrelationId` kaldırıldı. Aynı
  bağlamda çalışan FARKLI işlemleri tek işleme indiriyordu; ikinci işlem hiç çalışmıyor, birincinin sonucunu
  alıyordu (sessiz yanlış veri) ya da farklı tipte sonuçta `InvalidCastException` alıyordu. Artık anahtar seçici
  verilmezse kurulumda `ArgumentException`. Ayrıca sonuç tipi uçuştaki anahtarın parçası oldu.
  **Geçiş:** `AddRequestCollapser(o => o.KeySelector = ctx => /* işlem + girdi */)`.
- **AEGIS-155 — `ICircuitBreakerStateStore`'a `TryAcquireProbeAsync` / `ReleaseProbeAsync` eklendi.** Bunlar
  varsayılan arayüz metotlarıdır, bu yüzden mevcut özel depolar derlenmeye devam eder. Ancak uygulanmazlarsa
  HalfOpen'da tek probe yalnızca pod içinde sağlanır. Arayüz belgesine davranış sözleşmesi eklendi:
  Open→HalfOpen, Closed yazınca sayaçların sıfırlanması, fail-open.
- **`DistributedCircuitBreakerOptions` ve `CircuitBreakerOptions` artık `CircuitBreakerOptionsBase`'ten türer.**
  Kaynak uyumludur. Dağıtık seçenekler `OnHalfOpened` ve `BreakDurationGenerator` kazandı.

### Düzeltildi

- **AEGIS-147 — Redis'li dağıtık devre kesici HalfOpen'a hiç geçmiyordu.** Open anahtarının TTL'i dolunca anahtar
  siliniyor ve `Closed` okunuyordu. Tüm pod'lar tek probe yapmadan tam trafiğe dönüyor, `OnClosed` hiç
  tetiklenmiyordu. Bellek içi depo ise HalfOpen üretiyordu, yani aynı arayüzün iki uygulaması farklı davranıyordu
  (Liskov ihlali). Artık Open değeri `BreakDuration + HalfOpenRetention` ömrüyle yazılıyor. HalfOpen, Redis'in
  kendi kalan TTL'inden türetiliyor (pod saat farkından bağımsız). Eski biçimdeki `"1"` değerleri geriye uyumlu
  okunuyor.
- **AEGIS-148 — Redis kesintisinde devre kalıcı açık kalıyordu (fail-closed).** Yerel yedek durum Open'ı süresiz
  tutuyordu; Redis dönene kadar arka uç iyileşmiş olsa bile tüm çağrılar reddediliyordu. Yedek durum artık
  süreli ve `BreakDuration` sonunda HalfOpen'a dönüyor.
- **AEGIS-150 — Durum okumak `OnHalfOpened` olayını yutuyordu.** `State` / `LastKnownState` Open→HalfOpen geçişini
  kendisi yapıp olayı atıyordu. Health check yoklaması olan her ortamda (yani canlıda) olay pratikte hiç
  gelmiyordu. Okuma artık yan etkisiz; geçiş ve olay yalnızca çağrı yolunda.
- **AEGIS-151 — Kullanıcı olayı patlayınca çağıranın sonucu kayboluyordu.** Örneğin başarılı bir probe'dan sonra
  `OnClosed` içindeki alarm çağrısı patlarsa, geçerli sonuç yerine alarm istisnası dönüyordu. Dışta Retry varsa
  zaten başarılı olmuş işlemi tekrarlatabiliyordu. `OnOpened` hatası da asıl arka uç hatasını gizliyordu.
  `OnOpened`/`OnClosed`/`OnHalfOpened` ve `BreakDurationGenerator` hataları artık `AegisCallbacks.InvokeSafelyAsync`
  ile yutuluyor ve yeni `aegis.callback.errors.total` sayacına yazılıyor (yerel ve dağıtık).
- **AEGIS-152 — Idempotent olmayan istekte gerçek hata gizleniyordu.** Idempotency-Key'siz bir POST ağ hatası
  alınca Retry ikinci denemeye giriyor, ikinci deneme yapay bir `InvalidOperationException` fırlatıyordu. Sonuç:
  gerçek `HttpRequestException` kayboluyor, Retry bu yapay hatayı backoff ile defalarca deniyor, devre kesici
  sahte hatalar sayıyordu. Artık HTTP katmanı `AegisContextKeys.SuppressAdditionalAttempts` işaretini koyuyor;
  Retry ve Hedging ek deneme üretmiyor, ilk denemenin gerçek sonucu veya istisnası dönüyor. İşaret çağrı sonunda
  bağlamdan kaldırılıyor. Tampon sınırını aşan gövdeler de aynı yoldan geçiyor.
- **AEGIS-153 — Devre kesici penceresi dokümandaki gibi kayan değil, sabit (tumbling) idi.** Pencere sınırına
  denk gelen hata patlamaları ikiye bölünüp devreyi hiç açmıyordu. Yerine 10 dilimli kayan pencere (`HealthWindow`)
  geldi. Canlı seçenek sağlayıcısı artık kilit altında çağrılmıyor ve çağrı başına iki kez çalıştırılmıyor.
- **AEGIS-154 — Eski probe yeni probe'un kilidini açabiliyordu.** `finally` bloğu bool bayrağı koşulsuz
  sıfırlıyordu; devre yeniden açılıp yeni probe başladıktan sonra biten eski probe, iki probe'un aynı anda
  geçmesine yol açıyordu. Probe artık kimlikle izleniyor.
- **AEGIS-155 — Dağıtık HalfOpen'da tek probe yalnızca pod içindeydi.** N pod, iyileşen arka uca aynı anda N probe
  gönderiyordu. Probe hakkı artık depo üzerinden küme çapında kiralanıyor (Redis `SET NX PX` + sahiplik kontrollü
  Lua ile bırakma). Kira `BreakDuration` sonunda kendiliğinden düşüyor, böylece çöken pod devreyi kilitlemiyor.
  Probe başarılı olunca pencere sayaçları sıfırlanıyor. Kayıt çağrıları artık çağıranın iptal jetonuyla değil
  `CancellationToken.None` ile yapılıyor; tamamlanmış bir sonuç sonradan gelen iptal yüzünden kaybolmuyor.

### İç yapı (DRY / SOLID)

- **AEGIS-156 — Paketler arası sözleşme tek kaynakta:** `AegisContextKeys` (`CacheKey`, `PartitionKey`,
  `RetryAfterDelay`, `SuppressAdditionalAttempts`). `HttpRetryAfterHelper.RetryAfterPropertyKey` artık bu
  sabite bağlı; düz metin kopyaları kaldırıldı.
- Hata sınıflandırması (`OperationCanceledException` ve `BrokenCircuitException` asla hata/yeniden deneme
  sayılmaz) üç kopyadan tek `CircuitBreakerRules.IsControlFlow`'a indirildi. Eşik hesabı ve break süresi çözümü
  `CircuitBreakerOptionsBase`'te; yerel ve dağıtık devre kesici aynı kuralları kullanıyor.
- `InMemoryCircuitBreakerStateStore` monotonik saate (`Stopwatch`) geçti; süre taşmalarına karşı korumalı.

### Bilinen sınırlar

- Dağıtık pencere, depoda sabit (tumbling) pencere olarak kaldı. Yavaş çağrı oranı yalnızca yerel devre
  kesicide var. Bu farklar dokümante edildi.
- `simplify` ve `security-review` skill'leri git diff üzerinden çalıştığı için bu turda koşulamadı (proje git
  deposu değil).

### Test altyapısı notu

- İlk koşuda net9'da 7 Redis testi `BadImageFormatException` ile düşmüştü. Sebep kod değil, bozuk bir derleme
  çıktısıydı: `bin/Release/net9.0/RESPite.dll` paketteki hiçbir sürümle eşleşmiyordu, ama boyutu ve tarihi aynı
  olduğu için MSBuild artımlı kopyada dosyayı yenilemiyordu. Klasör temizlenince düzeldi.

## [Yayınlanmadı → 1.0.4 ve öncesi]

### Değişti (Breaking / Davranış)

- **Hedef çerçeveler:** `net9.0` eklendi. Paketler artık `net8.0;net9.0;net10.0` hedeflerini içerir; her hedef
  `Microsoft.Extensions.*` paketlerinin kendi taban sürümüne bağlanır.
- **AEGIS-130 — Seçenek doğrulaması (fail-fast):** Tüm `*Options` sınıflarına `Validate()` eklendi ve strateji
  kurucularında çağrılır. Geçersiz değerler artık **kurulum anında** açıklayıcı `ArgumentOutOfRangeException`
  ile reddedilir (Polly / .NET RateLimiting ile aynı yaklaşım). Önceden `Math.Max(1, x)` gibi ifadelerle
  SESSİZCE düzeltiliyordu: `PermitLimit = 0` ("her şeyi engelle") 1 istek geçiriyor, `MaxHedgedAttempts = 0`
  1 sayılıyor, `Timeout = TimeSpan.FromTicks(500)` (ms yerine tick yazılması) "zaman aşımı yok" anlamına
  geliyordu. Kural özeti: sayaç/limitler ≥ 1, süreler > 0 (kuyruk süreleri ≥ 0), oranlar (0, 1], `Timeout`
  > 0 veya `Timeout.InfiniteTimeSpan`, `MaxRetryAttempts` ≥ 0.
- **AEGIS-131 — `Timeout ≤ 0` artık "bütçe tükendi" demek:** `TimeoutGenerator` kalan istek bütçesi olarak 0
  veya negatif döndürürse istek **hiç başlatılmadan** `AegisTimeoutException` fırlatılır. Eskiden bu değer
  "zaman aşımı yok" sayılıyordu — yani bütçesi biten istek SINIRSIZ çalışıyordu (deadline budget senaryosunun
  tam tersi). Zaman aşımını bilinçli kapatmak için `Timeout.InfiniteTimeSpan` kullanın.
- **AEGIS-129 — Fallback sessiz veri bozulmasına son:** `FallbackHandler` tanımlı değilse veya dönüş tipi
  uyumsuzsa (ör. `int` beklenirken `string`) artık açık `InvalidOperationException` fırlatılır (asıl hata
  `InnerException`'da). Eskiden `default(TResult)` dönüyordu: çağıran, çöken servisin sonucu olarak `0` /
  `null` görüyor ve bunu gerçek değer sanıyordu. `null`, yalnızca `TResult` null kabul eden bir tipse geçerli yedek değerdir.
- **`AdaptiveConcurrencyOptions.RttJitterToleranceRatio` varsayılanı 0.5 → 1.0** ve yeni `WarmupSamples`
  (varsayılan 5) seçeneği (bkz. AEGIS-127).

### Paketleme

- Sürüm **1.0.4**. `dotnet pack` çıktısı `artifacts/packages/` altına alınır; 7 paket, her biri `net8.0 / net9.0 / net10.0`
  + XML dokümantasyon + sembol paketi (`.snupkg`). `AegisShowcase` artık kaynak kod referansı yerine bu paketleri
  **gerçek NuGet tüketicisi gibi** kullanır (`AegisShowcase/nuget.config` yerel akış + kaynak eşleme).
- Paket geçişi bir dağıtım tuzağını ortaya çıkardı: NuGet global önbelleğinde 13 Eylül tarihli eski bir
  `Aegis.Resilience.Core 1.0.0` vardı; aynı sürüm numarası yüzünden NuGet yerel akıştaki yeni paketi değil onu
  kullandı ve `IObservableCircuitState` için `TypeLoadException` alındı. **Ders:** her yeniden paketlemede sürüm
  artırılır (`1.0.0 → … → 1.0.4`); aynı sürümü yeniden paketlemek gerekiyorsa
  `%USERPROFILE%\.nuget\packages\aegis.*` silinmelidir.

### Saldırı rehberi ilk koşusu (1.0.4)

Kullanıcı A01–A13'ü CMD'de elle koştu, A14–A27 ve B3/B5 otomatik koşturuldu. Kütüphanede **1** bulgu, showcase demolarında **4**:

- **AEGIS-146 — Kütüphane: strateji metrikleri yanlış etikete gidiyordu.** Kullanıcı kendi `AegisContext`'ini adsız
  verdiğinde retry/timeout/ratelimit sayaçları `pipeline="default"` etiketine, boru hattı girişindeki `executions`
  sayacı ise gerçek ada yazılıyordu → aynı isteğin metrikleri Prometheus/dashboard'ta iki ayrı seriye bölünüyordu.
  Boru hattı artık adsız bağlama kendi adını yazar. (Saldırı rehberi A27 ile yakalandı.)
- **Showcase demoları — paylaşılan sayaç hatası (F08, F20, F23):** Üç serviste "deneme numarası" singleton alanda
  tutuluyordu; 200 paralel istekte çift/tek sıralar karışıp F08'de %25 istek 2000ms sürüyor (hedge kazanamıyor), F20'de
  `localAttempts` 1-5 arasında dağılıyor, F23'te %10 istek retry hakkını bitirip **500** dönüyordu. Kütüphane her üçünde
  de doğru davranıyordu; sayaçlar istek/sipariş başına yerel yapıldı, F23'e retry tükenmesi için `success:false` yanıtı eklendi.
- **Showcase F27:** `MeterListener` etiket filtresi yapmadığı için paralel isteklerin ölçümleri karışıyordu; çağrıya özel
  boru hattı adıyla filtreleniyor. **F12:** `34.870000000000005` double artefaktı yuvarlandı.
- **Rehber düzeltmeleri:** A04 kuyruk matematiği (150ms'de gelen 3. isteğin reddi DOĞRUDUR: 150+200 < 400), A06 döngüsündeki
  `&&`/`||` (CMD "More?" hatası), A14 `%%` → `ForEach-Object`, A21 "bütçesiz istek" varsayımı (demo varsayılanı 150ms),
  A26 dashboard aksiyonlarının `X-Aegis-Action: true` CSRF başlığı gerektirmesi (curl ile dışarıdan Isolate **yapılamıyor** —
  önceki "zafiyet" tespiti fazla karamsardı; anonim görüntüleme uyarısı geçerli kalır).

### Düzeltildi (Bug Fixes) — Polly parite denetimi (2. tur: Builder / Telemetry / Simmy / RateLimiting)

- **AEGIS-140 — Builder iki kez `Build()` edilebiliyordu:** Aynı strateji ÖRNEKLERİ (devre kesici durumu, semaforlar,
  önbellek sözlükleri) iki boru hattı arasında sessizce paylaşılıyor, birini dispose etmek diğerini öldürüyordu.
  Artık `Build()` sonrası `AddStrategy`/`Build` → `InvalidOperationException` (Polly: AddPipeline_AfterUsed_Throws).
  Boş/whitespace boru hattı adı da reddedilir.
- **AEGIS-141 — `AegisPipeline.Dispose()` idempotent değildi:** İkinci çağrı kullanıcı stratejilerinin `Dispose`'unu
  tekrar çağırıyordu.
- **AEGIS-142 — `StaleFallback` arka plan yenilemesi ebeveyn `Properties`'i kaybediyordu:** Tenant/önbellek anahtarları
  yenileme çağrısına taşınmıyordu; `CreateChild` kullanılıyor.
- **AEGIS-143 — Adaptif eşzamanlılık: zamanlayıcı çözünürlüğü altındaki mutlak taban (PAKETLENMİŞ showcase'te yakalandı):**
  curl istemcisinde `Task.Delay(5)` bazen 0-2ms'de döndüğünden iki ardışık ~1ms örnek `minRtt=1ms`'yi onaylıyor;
  PowerShell istemcisinde aynı çağrı timer fazı gereği tutarlı ~14ms sürüyor → `14 > 1 + max(5, 1)` → "14 kat
  yavaşlama" → limit 2'ye çöküp ASLA toparlanmıyordu (5 dk'lık baseline yenilemesi yükselişi %10 ile sınırlıyordu).
  `MinRttJitterToleranceMs` varsayılanı **5 → 20ms** (Windows timer çözünürlüğü 15.6ms'nin üstü); baseline yenilemesi
  pencerenin gerçek minimumunu doğrudan benimser. Deterministik regresyon testi eklendi.
- **AEGIS-144 — Cache ve StaleFallback TTL için DUVAR SAATİ kullanıyordu:** `DateTimeOffset.UtcNow` tabanlı son
  kullanma; NTP düzeltmesi/yaz saati/elle saat değişiminde tüm önbellek anında "bayat" oluyor ya da saatlerce
  fazla yaşıyordu. Monotonik `Stopwatch` zaman damgalarına geçildi (rate limiter ve devre kesici zaten monotonikti).
- **AEGIS-145 — Canlı (`OptionsProvider`) seçenekler doğrulanmıyordu:** Statik seçenekler kurulumda doğrulanırken
  yapılandırma sunucusundan her çağrıda gelen dinamik seçenekler `Math.Max` yedekleriyle sessizce çalışıyordu.
  `DynamicOptionsResolver`: geçersiz veya istisna fırlatan canlı yapılandırma ATILIR, **son geçerli** seçeneklerle
  devam edilir (Polly reload semantiği) — hatalı yayın trafiği ne bozar ne düşürür. 13 stratejiye uygulandı.
- **Eklendi:** `AddPipeline(IAegisPipeline)` kompozisyonu — registry'den alınan paylaşılan bir boru hattı yerel zincire
  gömülür; dış boru hattı dispose edildiğinde iç boru hattı dispose edilmez (Polly: AddPipeline_EnsureNotDisposed).
- **Kaldırıldı (YAGNI):** `AegisAlertNotifier` — dayanıklılık çekirdeğinin içinde Slack/Teams webhook göndericisi
  (zaman aşımı olmayan statik `HttpClient`); hiçbir yerde kullanılmıyordu. `DynamicAegisOptionsBridge`'in 4 kopya
  metodu tek generic `Create<TOptions>` ile değiştirildi (KISS).

### Düzeltildi (Bug Fixes) — Polly parite denetimi

Polly'nin kendi test paketi (`Polly.Core.Tests`, `Polly.Extensions.Tests`, `Microsoft.Extensions.Http.Resilience`)
satır satır incelendi ve kütüphaneyi en çok zorlayan 58 senaryo Aegis'e birebir uyarlandı
(`PollyParityTests.cs`, `PollyParityHttpAndRegistryTests.cs`). İlk koşuda **12'si kırıldı**:

- **AEGIS-134 — Çağıranın iptali yanlış token taşıyordu (Polly #3086):** Timeout/Hedging, çağıranın token'ını
  kendi bağlı token'ıyla ikame eder; çağıran iptal ettiğinde sızan `OperationCanceledException` çağıranın hiç
  görmediği iç token'ı taşıyordu → `ex.CancellationToken == myToken` kontrolleri başarısız oluyor, iptal "yabancı"
  bir iptal gibi görünüyordu. Boru hattı girişinde istisna, orijinali `InnerException` olarak koruyarak çağıranın
  token'ıyla yeniden fırlatılır. Gerçek zaman aşımı ve ilgisiz token'lı iptaller etkilenmez. 5 dizilim
  (nested timeouts, retry↔timeout, hedging, full-stack) ile doğrulandı.
- **AEGIS-137 — Atılan `IDisposable` sonuçlar dispose edilmiyordu:** Result-based retry'da yeniden denenen sonuç ve
  hedging'de kaybeden denemenin sonucu (`HttpResponseMessage`, `Stream`, `DbConnection`) çağırana hiç ulaşmadığı
  halde serbest bırakılmıyordu → her retry/hedging bir soket sızdırıyordu. `DiscardedResultDisposer` eklendi
  (`OnRetry`'dan SONRA dispose edilir ki kullanıcı günlükleme için sonucu hâlâ okuyabilsin).
- **AEGIS-135 — Hedging alt bağlamlarının `CorrelationId`'si ebeveynden farklıydı:** `CreateChild` özelliği değil
  alanı kopyalıyordu; ebeveyn kimliğe henüz erişmemişse `null` kopyalanıyor ve her deneme kendi rastgele
  kimliğini üretiyordu → dağıtık izlemede hedging denemeleri birbirinden kopuk görünüyordu.
- **AEGIS-136 — `HedgingDelay = Timeout.InfiniteTimeSpan` tam tersine çalışıyordu:** Bu değer "ardışık yedekleme"
  modudur (yeni deneme yalnızca öncekiler başarısız olunca). `> Zero ? : Zero` kontrolü -1ms'yi SIFIRA çevirip
  tüm denemeleri aynı anda başlatıyordu; doğrulayıcı da değeri reddediyordu. Her ikisi düzeltildi.
- **AEGIS-133 — Önceden iptal edilmiş token ile callback çalışıyordu:** Cache/Fallback/RateLimiter gibi stratejiler
  girişte token kontrolü yapmıyordu. Boru hattı girişine merkezi `ThrowIfCancellationRequested` eklendi.
- **AEGIS-138 — Registry `Dispose()` sonrası boru hattını DİRİLTİYORDU:** `_pipelines.Clear()` sonrası gelen
  `GetPipeline` configurator'ı yeniden çalıştırıp yeni bir örnek kuruyordu; Timer/Semaphore içeren bu örnek bir
  daha asla dispose edilmiyordu. Artık `ObjectDisposedException`.
- **AEGIS-139 — Registry'de zehirli `Lazy<T>`:** Configurator tek bir kez patlarsa (açılışta yapılandırma servisi
  hazır değil vb.) `Lazy<T>` istisnayı sonsuza kadar önbellekliyor, o boru hattı uygulama ömrü boyunca ölü
  kalıyordu. Zehirli Lazy atomik olarak kaldırılır; sonraki çağrı yeniden dener.

Geçen senaryolar (kütüphanenin zaten doğru yaptığı): HalfOpen'da yalnızca tek probe / 50 eşzamanlı istek
reddi, `BrokenCircuitException` stack trace'inin 200 rejeksiyonda büyümemesi, `BreakDurationGenerator =
TimeSpan.MaxValue` taşmaması, farklı sonuç tipleri arasında devre durumu paylaşımı (#959), sonsuz retry'da
2.048 üstel gecikmenin taşmaması (#2163), gecikme sırasında iptal, tek iş parçacıklı `SynchronizationContext`
altında deadlock yokluğu, POST gövdesinin (seekable olmayan akış dâhil) her denemede birebir tekrar oynatılması,
idempotency anahtarı yokken POST'un asla yeniden denenmemesi, 10 durum kodu için geçici/kalıcı sınıflandırma,
`Retry-After` (delta ve HTTP tarihi) gecikmesinin uygulanması, başarısız yanıtların dispose edilip sonuncunun
edilmemesi, 500 paralel istekte bulkhead'in iç işleyiciye yansıması, eşzamanlı `GetPipeline`'da tek örnek.

- **AEGIS-128 — `ConcurrencyLimiter.Dispose()` uçuştaki istekleri SONSUZA KADAR askıda bırakıyordu:**
  `SemaphoreSlim.Dispose()`, kuyrukta bekleyen `WaitAsync` çağrılarını hiç uyandırmaz; ayrıca `finally`
  içindeki `Release()` `ObjectDisposedException` fırlatıp asıl sonucu maskeliyordu. Dispose yarış testinde
  (uçuşta 16 istek varken dispose) deadlock gerçekten gözlemlendi. Semafor artık kasıtlı olarak dispose
  edilmez (`WaitAsync` yönetilmeyen kaynak tutmaz); strateji `disposed` bayrağı ile yeni çağrıları
  `ObjectDisposedException` ile reddeder, uçuştakiler etkilenmeden tamamlanır.
- **AEGIS-127 — Adaptif eşzamanlılık limiti tek bir aykırı-hızlı örnekle çöküyordu:** Test host'ta gözlemlenen
  gerçek dizi: 27ms soğuk JIT, ardından Windows timer'ın erken tetiklediği **6.8ms**'lik tek örnek (12ms
  istenmişti). Algoritma bu tek örneği anında `minRtt` yapıyor, kararlı ~15.6ms'yi "2.3× yavaşlama" sanıp
  limiti her örnekte yarılayarak `MinConcurrency`'ye indiriyor ve orada bırakıyordu. Üretimde önbellekten dönen
  tek bir yanıt aynı etkiyi yaratır. Üç düzeltme: (a) **ısınma dönemi** — ilk `WarmupSamples` örnek limiti
  değiştirmez, EMA basit ortalama ile tohumlanır; (b) **aykırı değer reddi** — yeni minimum yalnızca ardışık
  iki örnek onaylarsa kabul edilir; (c) **Netflix Gradient2 uyumu** — gradyan ham `min/smoothed` yerine
  tolerans çizgisine (`min + max(5ms, min×ratio)`) göre hesaplanır, varsayılan tolerans 2× (Netflix
  `tolerance=2.0`). 5 deterministik regresyon testi eklendi (`RecordSampleForTesting` ile örnekler doğrudan beslenir).
- **AEGIS-132 — Sıfır RTT ölçümü adaptif algoritmayı 5 dakika donduruyordu:** Senkron tamamlanan çağrılarda
  (önbellek isabeti, `ValueTask.FromResult`) `Stopwatch` iki ardışık okumada aynı değeri verebilir → RTT tam
  `0.0` → `minRtt = 0` → gradyan kararındaki `_minRttMs > 0` koruması bir daha hiç geçmiyor → hedef servis
  çökse bile limit daralmıyordu ("10 → 10" olarak yük testinde gözlemlendi). Ölçümler artık 1µs tabanına çekilir.

- **AEGIS-126 — Cardinality/bellek koruması yük altında ÇALIŞMIYORDU (DoS riski):**
  `PartitionedRateLimiterStrategy` ve `CacheStrategy` tahliyesi (a) `Monitor.TryEnter` kullandığı için
  yoğun eşzamanlı yükte çoğu çağrıda TAMAMEN atlanıyor, (b) atlanmadığında da yalnızca sınırın %10-20'si
  kadar kayıt siliyordu. Sonuç: `MaxPartitions=20` iken 3.000 farklı kiracıyla yapılan yük testinde sözlük
  **2.772 kayda** kadar büyüdü — yani rastgele kiracı/önbellek anahtarı gönderen bir istemci belleği
  sınırsız şişirebiliyordu. Tahliye artık ekleme SONRASI, gerçek kilit altında ve sınırın %80'ine
  İNİLENE kadar yapılır. 5.000 eşzamanlı farklı anahtarla regresyon testi eklendi.
- **AEGIS-125 — `ConcurrentDictionary` üzerinde LINQ `OrderBy` çökmesi:** LRU tahliyesi kaynağı doğrudan
  sıralıyordu; `OrderBy` kaynağı `ICollection` görüp `Count` kadar dizi ayırır ve `CopyTo` çağırır, sözlük
  bu sırada değişirse **`ArgumentException`** veya yarım dolu dizi yüzünden **`NullReferenceException`**
  fırlar. 10.000 eşzamanlı işlemli yük testinde gerçekten gözlemlendi (10.000 işlemin 12'si düştü).
  Artık `ConcurrentDictionary.ToArray()` ile atomik anlık görüntü alınıyor.
- **AEGIS-123 — Önbellek tahliyesi LRU değil RASTGELE idi:** `_cache.Keys.Take(n)` kullanılıyordu;
  `ConcurrentDictionary` anahtar sırası garantisiz olduğu için sık kullanılan sıcak girdiler atılabiliyordu
  (kod yorumu "en eski girdileri tahliye et" diyordu — yanlış). Girdilere son erişim zamanı eklendi,
  tahliye gerçek LRU'ya çevrildi.
- **AEGIS-124 — `AdaptiveConcurrencyStrategy.CurrentLimit` kilitsiz `double` okuyordu:** C# bellek modeli
  8 baytlık tipler için atomik okuma garanti etmez. Kilit altına alındı; `ActiveExecutions` için
  `Volatile.Read` kullanıldı.

- **AEGIS-122a — Retry, açık devrede (`BrokenCircuitException`) gereksiz gecikme yaratıyordu:**
  `RetryStrategy` varsayılan olarak her istisnayı (kullanıcı `ShouldHandle` vermediyse) retry ediyordu;
  bu, `CircuitBreakerStrategy`'nin retry'ın İÇİNDE kullanıldığı standart dizilimde (`AddStandardResilience`
  dahil) devre AÇIKKEN bile isteğin `MaxRetryAttempts` kadar jitter gecikmesiyle (~2-3sn) beklemesine yol
  açıyordu — gerçek ağ çağrısı yapılmadığı halde. `RetryStrategy` artık `CircuitBreakerStrategy` ile aynı
  kuralı uygular: `BrokenCircuitException`, kullanıcının `ShouldHandle` ayarından bağımsız olarak asla retry
  edilmez. Canlı ExampleProject entegrasyon testinde doğrulandı: devre açıkken yanıt süresi ~2400ms'den
  <1ms'ye düştü.
- **AEGIS-122b — `AdaptiveConcurrencyStrategy` düşük RTT'lerde jitter'ı yavaşlama sanıyordu:**
  "Sağlıklı" sayılan RTT farkı sabit bir mutlak eşikle (5ms) sınırlıydı. Düşük mutlak gecikmeli ortamlarda
  (yerel ağ, konteyner-içi çağrılar, testler) işletim sistemi zamanlayıcı hassasiyetinden kaynaklanan normal
  dalgalanma bile bu eşiği kolayca aşıp gradyanı düşürüyor, strateji gerçekte sağlıklı olan sistemde
  eşzamanlılık limitini sürekli `MinConcurrency`'ye kilitliyordu. `AdaptiveConcurrencyOptions`'a
  `RttJitterToleranceRatio` (varsayılan 0.5) eklendi: gerçek tolerans artık `max(MinRttJitterToleranceMs,
  minRtt × RttJitterToleranceRatio)` olarak hesaplanır — minRtt büyüdükçe tolerans da orantılı büyür.
- **AEGIS-121 — Dashboard ve HealthCheck dağıtık Circuit Breaker'ı görmüyordu:** `MapAegisDashboard`
  ve `AegisHealthCheck` yalnızca somut `CircuitBreakerStrategy` tipini arıyordu; `DistributedCircuitBreakerStrategy`
  hiçbir zaman "Devre Kesici" olarak tanınmıyor, dashboard'da durumu boş görünüyor, health check devre açıkken bile
  `Healthy` raporluyordu. Her iki strateji de artık ortak `IObservableCircuitState` arayüzünü uygular; tüketiciler
  somut tipe değil arayüze bakar. Canlı entegrasyon testiyle doğrulandı (paylaşılan devre açıldığında dashboard
  `Open`/`Degraded`, health check `Degraded` raporluyor).

- **AEGIS-115 — `MultiEndpointHedgingHandler` çöküyordu:** Birincil uç nokta, `HedgingDelay`
  süresinden önce yanıt verdiğinde `Task.WhenAny(Task.WhenAny(...))` sonucu hatalı cast edildiği için
  `InvalidCastException` fırlatılıyordu. Bekleme mantığı düzeltildi ve regresyon testi eklendi.
- **AEGIS-110 — Hedging bağlamı kaybediyordu:** Her deneme için boş bir `AegisContext` üretildiğinden
  `Properties` (CacheKey, PartitionKey, Retry-After vb.) ve `CorrelationId` alt akışa taşınmıyordu.
  Artık alt bağlamlar çağıranın verilerini devralır ve kazanan denemenin yazdıkları geri birleştirilir.
- **AEGIS-103 — `AegisDynamicResilienceHandler` çift işlem riski:** Dinamik işleyicide gövde tamponlama,
  yeniden denemede istek klonlama ve idempotency koruması yoktu; `Idempotency-Key` taşımayan POST/PATCH
  istekleri sessizce tekrar gönderilebiliyordu. Her iki HTTP işleyicisi artık ortak
  `HttpResilienceExecutor` çekirdeğini kullanır.
- **AEGIS-114 — Hedging'de yanıt (bağlantı) sızıntısı:** Kaybeden ve terk edilen `HttpResponseMessage`
  örnekleri dispose edilmiyordu. Her deneme kendi iptal jetonuna sahiptir; kazananın içerik akışı iptal
  edilmez, diğerleri iptal edilip serbest bırakılır.
- **AEGIS-111 — Kötümser (Pessimistic) zaman aşımında bağlam yarışı:** Terk edilen görev, havuza iade
  edilmiş bir `AegisContext`'i değiştirebiliyordu. Kötümser mod artık izole alt bağlamla çalışır ve
  havuz mükerrer iadeyi yok sayar.
- **AEGIS-118 — AOP proxy istisnaları sarmalıyordu:** `TargetInvocationException` sarmalayıcısı
  nedeniyle kullanıcı tanımlı `ShouldHandle` koşulları hiçbir zaman eşleşmiyor, retry/circuit breaker
  devreye girmiyordu. Orijinal istisna yığın izi korunarak yükseltilir.
- **AEGIS-117 — AOP proxy iptal jetonunu yok sayıyordu:** Metot argümanındaki `CancellationToken`
  artık boru hattı bağlamına taşınır.
- **AEGIS-116 — `HttpClient` işleyici yenilemesinde durum kaybı:** Inline yapılandırılan boru hattı her
  `HandlerLifetime` (varsayılan 2 dk) dolduğunda yeniden kuruluyor, Circuit Breaker sayaçları sıfırlanıyor
  ve `Timer`/`SemaphoreSlim` kaynakları sızıyordu. Boru hattı artık bir kez kurulup paylaşılır.
- **AEGIS-119 — Kayıt defteri (registry) sızıntısı:** Yarış durumunda aynı boru hattı iki kez kurulabiliyor
  ve hiçbir boru hattı dispose edilmiyordu. Kurulum `Lazy` ile tekilleştirildi; registry `IDisposable` oldu.
- **AEGIS-120 — Dashboard XSS:** Boru hattı adları (`AddAegisHandlerByHost` ile host adından gelebilir)
  DOM'a kaçışsız yazılıyor ve `onclick` içine gömülüyordu. Tüm metinler kaçışlanır, olaylar
  `addEventListener` ile bağlanır.
- **AEGIS-112 — `ConcurrencyLimiter` canlı ayarı uygulamıyordu:** `OptionsProvider` ile güncellenen
  `MaxConcurrentExecutions` semafora yansıtılmıyor, yalnızca hata mesajında görünüyordu.
- `RateLimiterStrategy.AvailableTokens` dinamik ayarları yok sayıyordu.
- `CircuitBreakerOptions.OnHalfOpened` olayı hiçbir zaman tetiklenmiyordu.
- `ChaosStrategy.ResultGenerator` yanlış tipte sonuç ürettiğinde anlamsız `InvalidCastException` yerine
  açıklayıcı bir hata fırlatılır.
- `AegisContext.Properties` eşzamanlı erişime karşı güvenli hale getirildi.

### Test Kapsamı

- **Gerçek Redis entegrasyon testleri** (6 adet): Lua script'inin atomikliği (200 eşzamanlı kayıt),
  TTL ile otomatik durum sıfırlama, iki ayrı "pod"un Redis üzerinden durum paylaşımı ve Redis
  erişilemezken fail-open davranışı. Redis yoksa testler otomatik atlanır (`SkippableFact`), CI kırmızıya düşmez.
  Çalıştırmak için: `docker run -d -p 6379:6379 redis:7-alpine`
- **Yük testleri**: 10.000 eşzamanlı işlem (kilitlenme/sızıntı yok), 3.000 eşzamanlı bölüm oluşturma,
  5.000 eşzamanlı farklı önbellek anahtarı, 2.000 eşzamanlı circuit-breaker + hedging.
- **Kaynak sızıntısı testi**: 300 pipeline kurup dispose edildiğinde bellek artışı < 10 MB.
- **Doğrudan birim testleri**: `ConcurrencyLimiterStrategy` (izin sızıntısı, canlı limit değişimi) ve
  `SlidingWindowRateLimiterStrategy` (pencere yenilenmesi, kuyruk zaman aşımı) için — daha önce yalnızca
  dolaylı olarak kapsanıyorlardı.
- **Uzun süreli çalışma testi**: 5 ardışık pencere boyunca rate limiter kotasının tutarlı yenilenmesi.

- **Düşmanca (adversarial) stres testleri** (26 adet, `AdversarialStressTests.cs`): geçersiz yapılandırma (0 / negatif / NaN /
  sonsuz), 2.000 istekli iptal fırtınası, 3.000 istekli karışık istisna fırtınası (yalnızca bilinen istisna tipleri
  sızmalı), dispose yarışları (20 tur, uçuşta 16 istek), senkron bloklama (`GetAwaiter().GetResult()` × 50 paralel),
  patlayan kullanıcı callback'leri (`OnRetry`, `ShouldHandle`, `OnOpened`, `KeySelector`), sınır değerler
  (null sonuç, 100 KB anahtar, `\0`, emoji, değer tipleri/tuple/decimal), 65 denemeli üstel geri çekilme taşması.
- **CPU açlığı altında doğrulama:** Tam paket, 28 çekirdeğin tamamı meşgulken 3 çerçevede 2 tur koşturuldu
  (6/6 yeşil). Bu koşul, zamanlamaya bağlı iki test tasarım hatasını ortaya çıkardı ve düzeltildi
  (Collapser testi "100 çağıran 50ms içinde gelir" varsayıyordu; Hedging testi `using var registration` ile
  henüz çalışmamış iptal callback'ini kayıttan siliyordu — kütüphane her iki durumda da doğru davranıyordu).
- **Üç çerçevede tam koşu:** `net8.0`, `net9.0`, `net10.0` — her biri 3 tur, ardından gerçek Redis ile.

Toplam test sayısı: **101 → 228** (76 adedi Polly parite testi).

### Eklendi (Features)

- **AEGIS-113 — Dağıtık Circuit Breaker artık gerçekten çalışıyor:** `ICircuitBreakerStateStore` hiçbir
  strateji tarafından tüketilmiyordu (ölü kod). Yeni `DistributedCircuitBreakerStrategy` ve
  `AddDistributedCircuitBreaker(...)` ile devre durumu tüm pod'lar arasında paylaşılır; depo erişilemezse
  fail-open çalışır.
- `AegisContext.CreateChild(...)` ve `AegisContext.MergePropertiesFrom(...)`: paralel stratejiler için
  bağlam türetme/birleştirme API'si.
- `AegisDynamicResilienceHandler` ve `AddAegisDynamicHandler` için `allowNonIdempotentRetry` seçeneği.

### Değiştirildi (Chore / Build)

- **Çoklu hedefleme:** Tüm kütüphane paketleri artık `net10.0` **ve** `net8.0` (LTS) içerir.
  `net8.0` tüketicileri `Microsoft.Extensions.*` paketlerinin 8.x taban sürümlerine bağlanır
  (10.x yükseltmesine zorlanmaz). Çerçeveye özel tek API farkı (`LoadIntoBufferAsync` iptal jetonlu
  aşırı yüklemesi .NET 9+) `#if` ile ayrıştırıldı. Test paketi her iki çerçevede de koşar.
- Örnek uygulama artık NuGet paketi üretmiyor (`IsPackable=false`) — yanlışlıkla yayınlanma riski kalktı.
- MIT `LICENSE` dosyası eklendi; README lisans rozeti paket metadata'sı ile uyumlu hale getirildi.
- Deterministik derleme, SourceLink ve `.snupkg` sembol paketleri etkinleştirildi.
- Derleme uyarıları hata sayılır (`TreatWarningsAsErrors`), analizör seviyesi `latest-recommended`.
- CI: paketler artifact olarak yüklenir; `v*` etiketinde NuGet'e yayınlama adımı eklendi.
- Zamanlamaya duyarlı testler deterministik hale getirildi (sinyal tabanlı bekleme, ayrıştırılmış
  pencereler, koleksiyon paralelliği kapalı) — yük altında rastgele kırmızıya dönme sorunu giderildi.
