# 🧪 Aegis — Docker ile Genişletilmiş Test Altyapısı

Birim testleri tek başına Windows'ta koşar. Aşağıdaki gruplar, gerçek ortamda en sık görülen durumları Docker ile
yeniden üretir: Linux sunucu, küçük pod kaynakları, farklı Redis sürümleri, yavaş veya kopan ağ, güvenlik ve
telemetri. Tüm komutlar CMD içindir ve depo kökünden (`C:\Users\Baran\Desktop\Nuget`) çalıştırılır.
Önkoşul: Docker Desktop açık olmalı.

| Grup | Neyi kanıtlar | Araç |
|---|---|---|
| 1. Linux | 3 çerçevede tüm testler Linux'ta geçer | `dotnet/sdk:10.0` + 8/9 çalışma zamanları |
| 2. Kısıtlı kaynak | 1 CPU / 512 MB'da (küçük K8s pod'u) zamanlama yarışları | aynı imaj, `--cpus=1 --memory=512m` |
| 3. Redis matrisi | Lua betikleri ve komutlar her sürümde çalışır | redis 6.2 / 7 / 8, Valkey 8, şifreli Redis |
| 4. Ağ kaosu | Yavaş Redis, kopan bağlantı, yavaş/kesilen HTTP yanıtı | Toxiproxy + go-httpbin |
| 5. Güvenlik | Bağımlılık açıkları, imaj açıkları, sızmış gizli bilgi, K8s yapılandırması | `dotnet list package --vulnerable`, Trivy |
| 6. Telemetri | Metrikler OTLP ile gerçekten dışa aktarılır, etiketler doğrudur | OpenTelemetry Collector |
| 7. Soak | Rastgele anahtarlarla uzun yükte bellek sınırlı kalır | k6 + kind kümesi (arşiv: `docs/reports/kubernetes-verification.md`) |
| 8. Native AOT | Kütüphaneler sıfır trim/AOT uyarısıyla derlenir; yerel ikili çalışma zamanı içermeyen imajda çalışır | `dotnet/sdk:10.0` + clang → `runtime-deps:10.0` |
| 9. .NET Framework | netstandard2.0/net462 derlemeleri gerçek .NET Framework 4.8'de çalışır | Windows (Docker gerekmez) |
| 10. SQL Server | Gerçek kilitlenmede (1205) SQL geçici hata tanıma + retry | `mssql/server:2022` (EULA kullanıcı tarafından kabul edilir) |
| 11. Batırma testleri | Aynı acımasız saldırılarda Aegis ve rakiplerin son sürümleri: kilitlenme, sızıntı, taşma, zehirlenme | `tests/Aegis.TortureTests` (konsol) |

## Ortak hazırlık (bir kez)

```bash
docker network create aegis-test
```

```bash
docker run -d --name aegis-redis-test --network aegis-test -p 6379:6379 redis:7-alpine
```

## 1–2. Linux ve kısıtlı kaynak

```bash
docker build -f tests/docker/Dockerfile.linux-tests -t aegis-linux-tests .
```

```bash
docker run --rm --network aegis-test -e AEGIS_TEST_REDIS=aegis-redis-test:6379 aegis-linux-tests
```

Küçük pod taklidi (1 CPU, 512 MB):

```bash
docker run --rm --cpus=1 --memory=512m --network aegis-test -e AEGIS_TEST_REDIS=aegis-redis-test:6379 aegis-linux-tests
```

> İmaj, testleri root olmayan kullanıcıyla koşar. NuGet önbelleği `/nuget`'tadır. `/root` altında kalırsa root
> olmayan kullanıcı test SDK'sını okuyamaz ve `dotnet test` **0 test koşup "başarılı" döner**.

## 3. Redis uyumluluk matrisi

```bash
docker run -d --name aegis-redis62 -p 6380:6379 redis:6.2-alpine
```

```bash
docker run -d --name aegis-redis8 -p 6381:6379 redis:8-alpine
```

```bash
docker run -d --name aegis-valkey8 -p 6382:6379 valkey/valkey:8-alpine
```

```bash
docker run -d --name aegis-redisauth -p 6383:6379 redis:7-alpine redis-server --requirepass S3cret!x
```

Her biri için (örnek Redis 6.2):

```bash
set AEGIS_TEST_REDIS=localhost:6380
```

```bash
dotnet test tests/Aegis.Tests -c Release -f net10.0 --filter FullyQualifiedName~RealRedisIntegrationTests
```

Şifreli Redis için: `set AEGIS_TEST_REDIS=localhost:6383,password=S3cret!x`

## 4. Ağ kaosu (Toxiproxy)

```bash
docker run -d --name aegis-httpbin --network aegis-test mccutchen/go-httpbin
```

```bash
docker run -d --name aegis-toxiproxy --network aegis-test -p 8474:8474 -p 26379:26379 -p 28080:28080 -p 28081:28081 ghcr.io/shopify/toxiproxy:2.12.0
```

```bash
set AEGIS_TEST_TOXIPROXY=127.0.0.1
```

```bash
dotnet test tests/Aegis.Tests -c Release -f net10.0 --filter FullyQualifiedName~ToxiproxyChaosTests
```

| Test | Enjekte edilen | Beklenen |
|---|---|---|
| `SlowRedis_…` | Redis'e 1,5 sn gecikme | Korunan çağrı < 1 sn (işlem süre sınırı 250 ms) |
| `RedisConnectionCut_…` | Redis bağlantısı kesilir, sonra geri gelir | Hızlı fail-open; `IsSharedStateAvailable` false → true |
| `SlowHttpBackend_…` | HTTP arka uca 5 sn gecikme | 3 deneme × 300 ms zaman aşımı; 5 sn beklenmez |
| `ProbabilisticConnectionResets_…` | Bağlantıların %30'u yanıt ortasında kesilir | Retry ile 30/30 başarı |
| `MultiEndpointHedging_…` | Birincil uca 3 sn gecikme | İkincil uçtan < 1,5 sn yanıt |

## 5. Güvenlik

```bash
dotnet list src/Aegis.Resilience.Core package --vulnerable --include-transitive
```

İmaj taraması (önce bölüm 8'deki Native AOT imajını derleyin):

```bash
docker run --rm -v //var/run/docker.sock:/var/run/docker.sock aquasec/trivy:0.65.0 image --scanners vuln --severity HIGH,CRITICAL aegis-aot-smoke
```

Gizli bilgi ve yapılandırma taraması:

```bash
docker run --rm -v "%cd%:/src:ro" aquasec/trivy:0.65.0 fs --scanners secret,misconfig --severity MEDIUM,HIGH,CRITICAL --skip-dirs /src/artifacts /src
```

## 6. Telemetri (OpenTelemetry Collector)

```bash
docker run -d --name aegis-otel -p 4317:4317 -p 8889:8889 -v "%cd%\tests\docker\otel-collector.yaml:/etc/otelcol/config.yaml:ro" otel/opentelemetry-collector:0.131.0 --config /etc/otelcol/config.yaml
```

Collector'ın Prometheus çıktısı `http://127.0.0.1:8889/metrics` adresindedir. Aegis ölçümlerinin, günlüklerinin ve izlerinin
Collector'a gerçekten ulaştığı aşağıdaki duman testiyle otomatik doğrulanır (bölüm 6.1). Uygulama tarafındaki metrik etiketleri
ve iz span'ları ayrıca `samples/RealWorld` testlerinde (`ObservabilityTests`) doğrulanır.

### 6.1 Smoke testinde gerçek Collector (gRPC + HTTP/protobuf, native AOT dahil)

Smoke testi (`tests/Aegis.AotSmokeTest`) `AEGIS_SMOKE_COLLECTOR_HOST` verilirse iki ek senaryo koşar: her biri **gerçek
Collector'a** (biri gRPC, biri HTTP/protobuf) Aegis ölçümlerini, günlüklerini ve izlerini gönderir. Değişken yoksa atlanır.

- **Ölçümler:** uygulama Collector'ın Prometheus uç noktasını kazır, `aegis_executions_total` ve
  `aegis_retry_attempts_total` satırlarını ayrıştırır. Boru hattı adı etiketi (`pipeline`) ve değer (7 çağrı → 7 / 7) doğrulanır.
- **Günlükler:** Collector günlükleri `otlphttp` ile uygulamanın yerel alıcısına (5555) yeniden iletir. Uygulama gövdede benzersiz
  boru hattı adını ve `Resilience pipeline executed` iletisini arar. Collector ayrıca günlükleri `docker logs aegis-otel` çıktısına yazar.
- Yapılandırma: `tests/docker/otel-collector.yaml` (gRPC 4317, HTTP 4318, Prometheus 8889).

```bash
docker rm -f aegis-otel
```

```bash
docker run -d --name aegis-otel -p 4317:4317 -p 4318:4318 -p 8889:8889 -v "%cd%\tests\docker\otel-collector.yaml:/etc/otelcol/config.yaml:ro" otel/opentelemetry-collector:0.131.0 --config /etc/otelcol/config.yaml
```

Native AOT imajıyla (Collector'a `host.docker.internal` ile ulaşır; günlük alıcısı için 5555 yayımlanır):

```bash
docker build -f tests/docker/Dockerfile.aot -t aegis-aot-smoke .
```

```bash
docker run --rm -p 5555:5555 -e AEGIS_SMOKE_COLLECTOR_HOST=host.docker.internal aegis-aot-smoke
```

Uygulamayı JIT ile yerelde koşmak için: `set AEGIS_SMOKE_COLLECTOR_HOST=127.0.0.1` sonra
`dotnet run --project tests/Aegis.AotSmokeTest -c Release`.

> Git Bash kullanıyorsanız `-v` içindeki `/etc/otelcol/...` yolunu Windows yoluna çevirir ve Collector açılmaz; komuttan önce
> `MSYS_NO_PATHCONV=1` yazın. CMD'de bu sorun yoktur.
>
> Collector'ın `otlphttp` dışa aktarıcısı varsayılan olarak gzip kullanır. Yapılandırmada `compression: none` olmasının sebebi
> budur: sıkıştırılmış gövdede düz ASCII araması hiçbir şey bulmaz (ilk denemede günlükler bu yüzden "bulunamadı" göründü).

**Sınırlar:** Günlükler ve izler gövde düzeyinde doğrulanır (benzersiz boru hattı adı + günlük iletisi ya da `OnRetry` span olayı),
protobuf olarak alan alan ayrıştırılmaz. Prometheus yalnızca Collector'ın dışa aktardığı biçimi gösterir. İzler için Collector'ın
`traces` hattı da `otlphttp/applogs` ile uygulamanın alıcısına (`/v1/traces`) iletir.

## 7. Soak

Kümede uzun süreli yük (k6 soak) 1.x sürümlerinde eski vitrin uygulamasıyla koşuldu; sonuçlar arşivdedir:
`docs/reports/kubernetes-verification.md`. Süreç içi bellek sınırı (1 milyon çağrı, rastgele anahtarlar) her sürümde batırma
testleriyle (`tests/Aegis.TortureTests`) doğrulanır.

## 8. Native AOT

```bash
docker build -f tests/docker/Dockerfile.aot -t aegis-aot-smoke .
```

```bash
docker run --rm aegis-aot-smoke
```

> Yayın adımında `IlcTreatWarningsAsErrors` açıktır: tek bir trim/AOT uyarısı imaj derlemesini kırar. Çıktının son
> satırı `AOT DUMAN TESTI: TUMU GECTI` olmalıdır. 7 senaryo koşar: stratejiler, çalıştırma biçimleri, sahte saat,
> telemetri, DI + ILogger, yapılandırmadan HTTP standart işleyici ve dağıtık devre.

## 9. .NET Framework 4.8 (Windows)

```bash
dotnet test tests\Aegis.CompatibilityTests -c Release
```

> Test projesi `net48` hedefler ve kütüphanenin `net462` derlemelerini kullanır. Burada üç yol doğrulanır: eski
> hedeflerdeki genişletme metodu yolu, `Microsoft.Bcl.TimeProvider` ile sahte saat ve `HttpRequestMessage.Properties`
> yolu.

## 10. Gerçek SQL Server kilitlenmesi (isteğe bağlı)

`RealSqlServerTransientTests` gerçek bir kilitlenme (hata 1205) oluşturur. Test, Aegis retry'ının
`HandleSqlTransientErrors()` ile kurbanı yeniden çalıştırıp iki işlemi de tamamladığını doğrular.

> **Lisans:** SQL Server kapsayıcısı, Microsoft SQL Server lisans sözleşmesini kabul etmenizi gerektirir
> (`ACCEPT_EULA=Y`). Bu kabulü siz yapmalısınız; otomatik koşuda bu yüzden yoktur (`AEGIS_TEST_SQL` tanımlı değilse
> test atlanır). Şifre yalnızca bu yerel test kapsayıcısı içindir.

```bash
docker run -d --name aegis-sql -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=Aegis!Test2026" -p 14330:1433 mcr.microsoft.com/mssql/server:2022-latest
```

```bash
set AEGIS_TEST_SQL=Server=localhost,14330;User Id=sa;Password=Aegis!Test2026;TrustServerCertificate=True
```

```bash
dotnet test tests\Aegis.Tests -c Release -f net10.0 --filter FullyQualifiedName~RealSqlServer
```

## 11. Batırma (torture) testleri — Aegis'e karşı Polly 8.8.0 ve Microsoft 10.10.0 (Docker gerekmez)

`tests/Aegis.TortureTests` aynı 15 acımasız senaryoyu her kütüphaneye kendi API'siyle uygular ve farklı
tohumlarla defalarca koşar.
- Senaryolar arasında thread fırtınası, izin sızıntısı, sync-over-async, uç değerler, fırlatan geri çağrılar, dispose
  yarışı, saat sıçraması, 1M çağrı bellek, hedging yanıt sızıntısı, bozuk `Retry-After` ve geri sarılamayan gövde var.
- Kilitlenme (tur bütçesi aşımı), beklenmeyen istisna ve gözlenmeyen görev istisnası "kaldı" sayılır.
- Rapor Markdown matristir. Aegis'te kalan tur varsa çıkış kodu 1'dir; CI'da kapı olarak kullanılabilir.

Proje klasöründen, 10 tur (rapor `torture-report.md`):

```bash
cd tests\Aegis.TortureTests
```

```bash
dotnet run -c Release -- 10 torture-report.md
```

Bir hatayı yeniden üretmek için raporda yazan taban tohumu üçüncü bağımsız değişken olarak verin
(ör. `dotnet run -c Release -- 10 rapor.md 777`).

## Temizlik

```bash
docker rm -f aegis-redis-test aegis-redis62 aegis-redis8 aegis-valkey8 aegis-redisauth aegis-httpbin aegis-toxiproxy aegis-otel aegis-sql
```

```bash
docker network rm aegis-test
```

## Son koşunun sonuçları (1.5.0, iz desteği)

| Grup | Sonuç |
|---|---|
| Çözüm derlemesi (5 hedef) | 0 uyarı, 0 hata (PublicAPI dahil) |
| Windows (net8/9/10) | 530 test × 3; 524 geçti, 6 atlandı (1 SQL Server EULA + 5 Toxiproxy, ortam değişkeniyle ayrıca koşulur) |
| .NET Framework 4.8 | 29/29 (span, etiket, olay ve çağıran bağlamı gerçek .NET Framework'te) |
| Native AOT (runtime-deps:10.0) | 15/15 senaryo (bellek içi dışa aktarıcıyla iz senaryosu dahil), sıfır trim/AOT uyarısı |
| Native AOT + gerçek OpenTelemetry Collector 0.131.0 (bölüm 6.1) | 17/17: ölçüm (ad, değer, etiket), günlük **ve iz**, gRPC ve HTTP/protobuf'ta |
| Batırma testleri (10 tur) | Aegis 16/16 senaryo, her tur (yeni: "iz açıkken fırtına", Aegis'e özel); Polly ve Microsoft önceki birer noktada düştü |
| Benchmark (P çekirdeklere sabitlenmiş) | Dinleyicisiz iz kodu ölçülebilir maliyet eklemedi; ayrıntı `docs/BENCHMARKS.md` |

## Önceki koşunun sonuçları (1.4.0)

| Grup | Sonuç |
|---|---|
| Çözüm derlemesi (5 hedef + 15 paket) | 0 uyarı, 0 hata |
| Windows (net8/9/10) | 518 test × 3; 512 geçti, 6 atlandı (1 SQL Server EULA + 5 Toxiproxy, ortam değişkeniyle ayrıca koşuldu) |
| .NET Framework 4.8 (WebApi dahil) | 28/28 |
| Redis matrisi (Redis 6.2 / 8, Valkey 8, şifreli Redis 7, ayrıca 6389'daki Redis 7; Redis'li gelen istek + ekosistem + WebApi testleri dahil) | her birinde 53/53 |
| Toxiproxy ağ kaosu | 5/5 |
| Native AOT (runtime-deps:10.0) | 14/14 senaryo (Adaptive Concurrency, `Aegis.Resilience.Extensions.Aspire`, OpenTelemetry SDK'sıyla metrik dışa aktarımı ve OTLP/HTTP protobuf dışa aktarıcısının yerel alıcıya ulaşması dahil), sıfır trim/AOT uyarısı. `Aegis.Resilience.WebApi` yalnızca net462 olduğu için AOT dışı |
| Native AOT + **gerçek OpenTelemetry Collector 0.131.0** (bölüm 6.1) | 16/16 (14 + gRPC + HTTP/protobuf): ölçüm adı, değer (`executions=7`, `retries=7`), `pipeline` etiketi (Prometheus kazıması) ve günlük, iki protokolde de |
| Batırma testleri (10 tur, tohum 20261004) | Aegis tüm senaryolarda 10/10; Polly ve Microsoft önceki turdaki birer noktada düştü |
| Benchmark (4 grup) | 1.3.0'a göre gerileme yok (docs/BENCHMARKS.md) |
| Showcase (1.4.0 NuGet paketleriyle) | 29/29 |
| Kubernetes (1.4.0 imajı, 3 pod) | 25/25; k6 üç koşu 8.047–8.688 istek/sn, %0 hata |

## Önceki koşunun sonuçları (1.3.0)

| Grup | Sonuç |
|---|---|
| Windows (net8/9/10) | 504 test × 3; 498 geçti, 6 atlandı (SQL Server EULA gerektirir; Redis/Toxiproxy testleri ayrı koşuldu) |
| Redis matrisi (5 sunucu: Redis 6.2 / 7 / 8, Valkey 8, şifreli Redis 7) | her birinde 16/16 |
| Toxiproxy ağ kaosu | 5/5 |
| Redis'li gelen istek + ekosistem testleri | 37/37 |
| .NET Framework 4.8 | 21/21 |
| Native AOT (runtime-deps:10.0) | 11/11 senaryo (Adaptive Concurrency dahil), sıfır trim/AOT uyarısı |
| Batırma testleri (bölüm 11; Aegis, Polly 8.8.0, MS Http.Resilience 10.10.0; 10 tur, tohum 20261003) | Aegis 15/15 senaryo, her tur. Polly: fırlatan geri çağrı senaryosunda 0/10. Microsoft: bozuk `Retry-After` senaryosunda 0/10 |
| Tüm çözüm derlemesi (5 hedef) + 13 paket (1.3.0) | 0 uyarı, 0 hata |
| Kubernetes senaryoları (1.3.0 imajı, 3 pod) | 25/25; k6 8.267 istek/sn, %0 hata, p95 17,7 ms |
| Showcase uçtan uca (1.3.0 NuGet paketleriyle) | 29/29 |

## Önceki koşunun sonuçları (1.2.0)

| Grup | Sonuç |
|---|---|
| Windows (net8/9/10, gerçek Redis + Toxiproxy + Kestrel) | 445 test × 3; 439 geçti, 6 atlandı. 5 Toxiproxy testi çerçeve başına ayrıca koşuldu ve geçti; SQL Server testi EULA gerektirdiği için isteğe bağlı |
| Redis matrisi (yeni Lua hız sınırlayıcı dahil) | Redis 6.2, 8, Valkey 8, şifreli Redis 7: her birinde 17/17 |
| Eşzamanlılık sınırlayıcı kuyruk/stres testleri | 3 hedef × 5 tekrar temiz (72 test) + yeni kuyruk testleri 3 × 3 |
| Native AOT (runtime-deps:10.0) | 10/10 senaryo (ASP.NET Core sunucu tarafı koruma dahil), sıfır trim/AOT uyarısı |
| .NET Framework 4.8 | 21/21 |
| Tüm çözüm derlemesi (5 hedef) | 0 uyarı, 0 hata (PublicAPI ve AOT analizörleri dahil; kodsuz uyarılar da dahil, geniş filtreyle) |
| Kubernetes senaryoları (1.2.0 imajı, 3 pod) | 25/25; k6 8.067 istek/sn, %0 hata, p95 18,2 ms |
| Showcase uçtan uca | 29/29 |
| Gerçek SQL Server kilitlenmesi | Hazır, otomatik koşulmadı (Microsoft EULA'sını kullanıcı kabul etmeli; bölüm 10) |

## Önceki koşunun sonuçları (1.1.0)

| Grup | Sonuç |
|---|---|
| Windows (net8/9/10, gerçek Redis + Toxiproxy) | 382/382 × 3 (Toxiproxy çerçeve başına ayrı koşulur: proxy'ler paylaşılır) |
| Native AOT (runtime-deps:10.0) | 7/7 senaryo, sıfır trim/AOT uyarısı |
| .NET Framework 4.8 | 16/16 |
| Tüm çözüm derlemesi (5 hedef) | 0 uyarı, 0 hata (PublicAPI analizörü dahil) |
| Kubernetes senaryoları (1.1.0 imajı, 3 pod) | 25/25; k6 8.312 istek/sn, %0 hata, p95 17,4 ms |
| Showcase uçtan uca | 29/29 |

## Önceki koşunun sonuçları (1.0.9)

| Grup | Sonuç |
|---|---|
| Windows (net8/9/10, gerçek Redis + Toxiproxy) | 274/274 × 3, atlanan yok |
| Linux, 1 CPU / 512 MB, gerçek Redis + Toxiproxy | 5 tur × 3 çerçeve = **15/15 koşu temiz** (274/274) |
| Redis matrisi | Redis 6.2.24, 7, 8.10.2, Valkey 8.1.10, şifreli Redis 7: 10/10 |
| Ağ kaosu | 5/5, 5 ardışık turda kararlı |
| Güvenlik | Paketlerde 0 açık; imajda 0 yüksek/kritik; 0 sızmış gizli bilgi; K8s yapılandırma bulguları giderildi |
| Telemetri | Collector'da `aegis_*` sayaçları `pipeline` etiketiyle; devre değişimi `state="open"` ile |
| Soak (10 dk, 1.000 istek/sn, rastgele anahtarlar) | 600.000 istek, %0 hata; pod belleği 140–153 MB'da düz, 0 yeniden başlatma |
| Kubernetes senaryoları (sertleştirilmiş manifestler) | 25/25 |
| Showcase uçtan uca | 29/29 |
