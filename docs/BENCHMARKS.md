# ⚡ Aegis vs Polly — Performans Karşılaştırması

BenchmarkDotNet ile, iki kütüphanede **aynı yapılandırma ve aynı geri çağrıyla** ölçülmüştür. Geri çağrı hiçbir iş
yapmaz (`ValueTask.FromResult(1)`), dolayısıyla ölçülen şey **kütüphanenin kendi ek yüküdür**. Polly her kategoride
referanstır: `Oran = Aegis / Polly`; 1'in altı Aegis'in daha hızlı olduğunu gösterir.

**Ortam:** BenchmarkDotNet 0.15.8, .NET 10.0.12, Windows 11, Intel Core i7-14700HX, Polly.Core 8.8.0.

## Sonuçlar (iz desteği) — iz maliyeti ve ölçüm yöntemi notu

Polly'de iz yoktur; referans Aegis'in dinleyicisiz hâlidir. Aynı standart zincir (retry + devre kesici + zaman aşımı), tek iş parçacığı,
**P çekirdeklere sabitlenmiş** (`--affinity 65535`), `TracingBenchmarks`:

| Durum | Süre | Oran | Bellek |
|---|---:|---:|---|
| Dinleyici yok | 629,9 ns | 1,00× | 0 B |
| Dinleyici bağlı, örnekleme kapalı | 710,0 ns | 1,13× | 0 B |
| Tam kayıt (span + etiketler + olaylar) | 1.180,1 ns | 1,87× | 520 B |

Boş boru hattı, `HasListeners()` denetiminin A/B ölçümü (P çekirdeklere sabitlenmiş, 3'er koşu): denetimli kod 48,07 / 47,67 / 48,16 ns,
denetimsiz 51,14 / 48,91 / 47,89 ns. **Denetimin ölçülebilir maliyeti yok.** Ölçümle bulunan iki iyileştirme yapıldı:
- Örnekleme kapalıyken strateji olayları boşuna oluşturuluyordu (+179 ns, 24 B). `IsEnabled` artık yalnızca çalışan bir Aegis span'ı
  kayıt yapıyorsa `true`: +60–80 ns, 0 B.
- Sorunsuz tamamlanan ilk deneme için span olayı eklenmiyor: tam kayıtta 1.048 B → 520 B, 1.480 ns → 1.180 ns.

Dürüst notlar:
- **Tam kayıt yaklaşık 0,55 µs ve 0,5 KB ek maliyet getirir.** Bunun büyük kısmı .NET'in kendi `Activity` nesnesidir. Yüksek hacimde
  OpenTelemetry örnekleme oranını düşürmek (örneğin %10) bu maliyeti orantılı azaltır.
- **Hibrit çekirdek gürültüsü (ölçüm yöntemi):** bu makinenin işlemcisi (i7-14700HX) P + E çekirdeklerden oluşur. Süreç E çekirdeğe
  düşerse mikro ölçüm ~2× yavaş çıkar ve iki modlu dağılım verir (boş boru hattı için 111–119 ns; sabitlenmemiş koşularda
  birden çok kez görüldü; 1.3.0'ın ilk koşusundaki "multimodal" uyarısı da buydu). Karşılaştırmalı mikro ölçümlerde `--affinity 65535` ile P
  çekirdeklere sabitlemek gerekir. Önceki bölümlerdeki sabitlenmemiş sayılar bundan etkilenmiş olabilir; oranlar yine de tutarlıdır
  çünkü Polly ve Aegis aynı koşuda ölçülür.
- 64 iş parçacıklı grup iz kodundan etkilenmedi (standart 668 ns, `Limiter` 140–146 ns, `Retry` 18,6–23,3 ns; 1.4.0: 661 / 144 / 18,2).
  Tek bir koşuda `Limiter` 183 ns ve `Retry` 32 ns çıktı; aynı kodla üç tekrarda değerler 1.4.0'a döndü, bu gürültüydü.

## Sonuçlar (1.4.0) — 1.3.0'a göre gerileme kontrolü

1.4.0 (Aspire, Web API 2) çekirdek yürütme yoluna dokunmaz; yine de dört grup yeniden ölçüldü (aynı makine, ayrı süreçler,
sırayla, başka iş koşmadan). Oran = Aegis / rakip. Mutlak süreler koşudan koşuya ±%5 oynar; sonuç: sıralama ve
bellek değişmedi.

| Senaryo (tek iş parçacığı) | Polly | Aegis | Oran | Bellek (Polly / Aegis) |
|---|---:|---:|---:|---|
| Boş boru hattı | 91,8 ns | 47,6 ns | **0,52×** | 0 B / 0 B |
| Retry | 353,7 ns | 177,0 ns | **0,50×** | 24 B / 0 B |
| Retry, 1 hata | 3.462 ns | 2.842 ns | **0,82×** | 296 B / 272 B |
| Timeout | 304,0 ns | 259,5 ns | **0,85×** | 0 B / 0 B |
| Devre kesici | 403,3 ns | 168,1 ns | **0,42×** | 24 B / 0 B |
| Açık devre reddi | 7.858 ns | 7.508 ns | **0,96×** | 1.312 B / 1.312 B |
| Standart zincir | 1.587 ns | 1.027 ns | **0,65×** | 48 B / 0 B |
| Eşzamanlılık sınırı | 304,5 ns | 103,4 ns | **0,34×** | 40 B / 0 B |
| Hedging | 633,3 ns | 236,4 ns | **0,37×** | 0 B / 0 B |

> 64 iş parçacığı: son ölçüm 2026-10-04, .NET 10, güncel kod.

| 64 iş parçacığı | Polly | Aegis | Oran | Bellek (Polly / Aegis) |
|---|---:|---:|---:|---|
| Standart, senkron | 1.009 ns | 694,9 ns | **0,69×** | 48 B / 0 B |
| Standart, async | 1.936 ns | 1.113 ns | **0,58×** | 2.357 B / 1.200 B |
| Devre kesici | 852,4 ns | 458,5 ns | **0,54×** | 24 B / 0 B |
| Eşzamanlılık sınırı | 952,4 ns | 178,1 ns | **0,19×** | 40 B / 0 B |
| Retry | 247,5 ns | 20,2 ns | **0,08×** | 24 B / 0 B |

| Ekosistem | Rakip | Aegis | Oran | Bellek (rakip / Aegis) |
|---|---:|---:|---:|---|
| Cache isabeti (Polly.Caching.Memory) | 158,1 ns | 117,9 ns | **0,75×** | 384 B / 0 B |
| Art arda hata devresi (Polly v7) | 152,3 ns | 164,4 ns | **1,08×** (eşit) | 496 B / 0 B |
| Token bucket (Polly.RateLimiting) | 240,4 ns | 174,1 ns | **0,72×** | 0 B / 0 B |
| Dağıtık sınırlayıcı, bellek içi depo (Polly yerel kovasına karşı) | 240,4 ns | 195,4 ns | **0,81×** | 0 B / 0 B |
| Sunucu tarafı hız sınırı (AspNetCoreRateLimit) | 3.880 ns | 865,0 ns | **0,22×** | 4.704 B / 1.592 B |

| Adaptive Concurrency | Polly (sabit) | Aegis (sabit) | Aegis (adaptif) | Adaptif / Polly |
|---|---:|---:|---:|---:|
| Tek iş parçacığı | 310,0 ns | 102,3 ns | 293,4 ns | **0,95×** |
| 64 iş parçacığı | 988,5 ns | 138,2 ns | 860,5 ns | **0,87×** |

Dürüst notlar:
- Art arda hata devresinde hız eşit (1,08×; önceki koşularda 0,94–1,08×). Bellekte Aegis önde.
- Açık devre reddinde fark %4 ve gürültü sınırında; bellek aynı.
- Kümedeki k6 verimi üç koşuda 8.047–8.688/sn (%8 yayılım): 1.3.0 ile fark ölçülemiyor (`docs/reports/kubernetes-verification.md`).

## Sonuçlar (1.3.0) — güncel tek iş parçacıklı koşu

1.3.0'da sıcak yola iki ek geldi: `PipelineExecuting` olayı ve `AegisTimers` süre normalizasyonu. Ölçüm, ilk
uygulamanın boş boru hattında 3–5 ns'lik bir kayba yol açtığını gösterdi (50 ns, 1.2.0'da 46 ns). Dinleyici denetimi
`measureDuration` dalının içine alınınca 46,2 ns'ye döndü. Bu bulgu A/B ölçümle doğrulandı: denetim geçici olarak
kaldırıldığında 47,7 ns, dal içine alındığında 46,2 ns. Bellek: önceki gibi başarı yolunda 0 B.

Aynı yapılandırma ve aynı geri çağrıyla, tek iş parçacığı, Polly.Core 8.8.0 (oran = Aegis / Polly):

| Senaryo | Polly | Aegis | Oran | Bellek (Polly / Aegis) |
|  |---|---:|---:|---:|---||  |---|---:|---:|---:|---||---:|---:|---|
| Boş boru hattı | 87,9 ns | 50,2 ns | **0,57×** | 0 B / 0 B |
| Retry | 348,6 ns | 170,5 ns | **0,49×** | 24 B / 0 B |
| Retry, 1 hata | 3.143 ns | 2.726 ns | **0,87×** | 296 B / 272 B |
| Timeout | 294,6 ns | 259,3 ns | **0,88×** | 0 B / 0 B |
| Devre kesici | 387,1 ns | 162,1 ns | **0,42×** | 24 B / 0 B |
| Açık devre reddi | 7.502 ns | 7.254 ns | **0,97×** | 1.312 B / 1.312 B |
| Standart zincir | 1.545 ns | 974,8 ns | **0,63×** | 48 B / 0 B |
| Eşzamanlılık sınırı | 298,6 ns | 95,2 ns | **0,32×** | 40 B / 0 B |
| Hedging | 598,4 ns | 220,4 ns | **0,37×** | 0 B / 0 B |

Dürüst notlar:
- Boş boru hattı tek başına ölçüldüğünde 0,52× (46,2 ns), tam koşuda 0,57×. Aynı kodun iki koşusu arasındaki fark ölçüm
  gürültüsüdür; ikisi de Polly'nin belirgin önünde.
- Açık devre reddinde fark %3 ve gürültü sınırında: pratikte eşit. Süre istisna oluşturma maliyetinin altında kalır
  (1.312 B iki tarafta da aynı). `RetryAfter` hesabı bu yola ek tahsis eklemedi.
- Diğer iki grup aynı gün, aynı makinede, 1.3.0 koduyla ayrı süreçlerde ölçüldü (aşağıdaki tablolar).

64 iş parçacığı (`ConcurrentLoadBenchmarks`):

| Senaryo | Polly | Aegis | Oran | Bellek (Polly / Aegis) |
|---|---:|---:|---:|---|
| Standart, senkron | 1.269 ns | 788,9 ns | **0,62×** | 48 B / 0 B |
| Standart, async | 2.443 ns | 1.523 ns | **0,62×** | 2.359 B / 1.177 B |
| Devre kesici | 1.158 ns | 466,3 ns | **0,40×** | 24 B / 0 B |
| Eşzamanlılık sınırı | 968,7 ns | 145,4 ns | **0,15×** | 40 B / 0 B |
| Retry | 260,1 ns | 21,2 ns | **0,08×** | 24 B / 0 B |

Ekosistem (`EcosystemBenchmarks`):

| Kategori | Rakip | Aegis | Oran | Bellek (rakip / Aegis) |
|---|---:|---:|---:|---|
| Cache isabeti (Polly.Caching.Memory) | 162,1 ns | 116,8 ns | **0,72×** | 384 B / 0 B |
| Art arda hata devresi (Polly v7) | 156,7 ns | 168,4 ns | **1,07×** (eşit) | 496 B / 0 B |
| Token bucket (Polly.RateLimiting) | 231,6 ns | 173,0 ns | **0,75×** | 0 B / 0 B |
| Dağıtık sınırlayıcı, bellek içi depo (Polly'nin yerel kovasına karşı) | 231,6 ns | 192,6 ns | **0,83×** | 0 B / 0 B |
| Sunucu tarafı hız sınırı (AspNetCoreRateLimit) | 3.773 ns | 856,5 ns | **0,23×** | 4.704 B / 1.592 B |

Art arda hata devresinde hız eşit (1,07×; önceki dört koşuda 0,94–1,08×), bellekte Aegis önde.

Adaptive Concurrency (`AdaptiveConcurrencyBenchmarks`). Polly'de adaptif sınırlayıcı yoktur; referans Polly'nin sabit
eşzamanlılık sınırlayıcısıdır. Aegis'in sabit sınırlayıcısı da eklendi ki adaptifin ek maliyeti görünsün. Adaptif sürümde
senkron hızlı yol eklendi (boşta kapasite + senkron tamamlanan geri çağrı async durum makinesi olmadan biter); tabloda
hızlı yol öncesi ve sonrası birlikte:

| Senaryo | Polly (sabit) | Aegis (sabit) | Adaptif, önce | Adaptif, sonra | Sonra / Polly | Bellek (Polly / Aegis adaptif) |
|---|---:|---:|---:|---:|---:|---|
| Tek iş parçacığı | 298,0 ns | 95,4 ns | 359,6 ns | 282,7 ns | **0,95×** | 40 B / 0 B |
| 64 iş parçacığı, aynı boru hattı | 904,0 ns | 146,4 ns | 847,8 ns | 847,6 ns | **0,94×** | 40 B / 0 B |

Dürüst notlar:
- **Hızlı yol tek iş parçacığında %21 kazandırdı (360 → 283 ns)**; adaptif sürüm artık Polly'nin sabit sınırlayıcısının
  önünde (önceden %18 gerideydi). Aynı kalıp devre kesici ve hız sınırlayıcıda zaten vardı.
- **64 iş parçacığında kazanç yok (848 → 848 ns).** Orada maliyet kilit çekişmesidir; adaptif strateji çağrı başına iki
  kez kilit alır (izin al, RTT'yi kaydet), hızlı yol bunu değiştirmez. Polly'ye göre yine hafif önde (0,94×), fark gürültü sınırında.
- **Adaptif hâlâ Aegis'in sabit sınırlayıcısından 3,0× (tek) / 5,8× (64 işçi) yavaş.** Bu algoritmanın doğal maliyetidir:
  iki zaman damgası, kilit altında gradyan hesabı. Kilitsiz yapmak ısınma, EMA ve minRTT durumunun atomik güncellenmesini
  gerektirir; doğruluk riski kazançtan büyük olduğu için yapılmadı. Yalnızca sınırlayıcı arayanlar için sabit sınırlayıcı önerilir.
- Bellekte hiçbir durumda tahsis yok (0 B).
- Hızlı yolun doğruluğu `AdaptiveConcurrencyFastPathTests` ile doğrulandı (izin sızıntısı yok, iptal, kapasite sınırı, 64 işçilik
  fırtına). Bu testler hızlı yolun KAZANCINI göstermez: hiç beklemeyen bir async `ValueTask` metodu da senkron tamamlanır, yani
  kazanç yalnızca benchmark ile ölçülebilir.
- **Denetim refactor'ü (`UpdateConcurrencyLimit` üç metoda bölündü) performansı bozmadı:** aynı koşulda öncesi 363 ns
  (tek) / 899 ns (64 işçi), sonrası 360 ns / 848 ns.

## Sonuçlar (1.2.0) — önceki tam koşu

Bu turda iki şey yapıldı. Rakip yelpazesi genişletildi: Polly v7 eklentileri ve AspNetCoreRateLimit, yeni
`EcosystemBenchmarks` sınıfında. Ölçümle bulunan kayıplar da giderildi: senkron hızlı yollar, `System.Threading.Lock`,
kilitsiz eşzamanlılık sınırlayıcı. Dört grup ayrı süreçlerde, kind kümesi durdurulmuşken ölçüldü. İlk tam koşuda bazı
kategorilerde ortalama ile ortanca belirgin ayrıştı (gürültü); o kategoriler tekrar ölçüldü ve grup baştan koşuldu. Aşağıdaki
sayılar ikinci temiz koşudandır.

### Polly 8.8 — tek iş parçacığı

> Son ölçüm: 2026-10-04, .NET 10, son kod incelemesi düzeltmelerinden sonra (commit 8471113).

| Senaryo | Polly | Aegis | Oran (süre) | Polly bellek | Aegis bellek |
|  |---|---:|---:|---:|---||  |---|---:|---:|---:|---||---:|---:|---:|---:|
| Boş boru hattı | 88,9 ns | 49,9 ns | **0,56×** | 0 B | 0 B |
| Retry — başarı yolu | 354 ns | 169 ns | **0,48×** | 24 B | **0 B** |
| Retry — 1 hata + 1 başarı | 3.253 ns | 2.812 ns | **0,86×** | 296 B | **272 B** |
| Timeout — başarı yolu | 297 ns | 254 ns | **0,85×** | 0 B | 0 B |
| Circuit Breaker — kapalı | 399 ns | 164 ns | **0,41×** | 24 B | **0 B** |
| Açık devre — hızlı red | 7.622 ns | 7.434 ns | **0,98×** | 1.312 B | 1.312 B |
| Standart zincir | 1.567 ns | 1.011 ns | **0,64×** | 48 B | **0 B** |
| Eşzamanlılık sınırlayıcı | 304 ns | 91,3 ns | **0,30×** | 40 B | **0 B** |
| Hedging | 609 ns | 276 ns | **0,45×** | 0 B | 0 B |
| TState | 1.525 ns | 1.006 ns | **0,66×** | 48 B | **0 B** |
| Yalnızca token | 1.554 ns | 1.010 ns | **0,65×** | 48 B | **0 B** |
| Senkron `Execute` | 1.470 ns | 991 ns | **0,67×** | 48 B | **0 B** |
| Senkron + TState | 1.466 ns | 997 ns | **0,68×** | 48 B | **0 B** |
| `ExecuteOutcomeAsync` — hata yolu | 594 ns | 439 ns | **0,74×** | 56 B | **0 B** |

### Polly 8.8 — 64 iş parçacığı eşzamanlı yük

| Senaryo | Polly | Aegis | Oran (süre) | Polly bellek | Aegis bellek |
|  |---|---:|---:|---:|---||  |---|---:|---:|---:|---||---:|---:|---:|---:|
| C1 — Standart zincir, senkron | 1.020 ns | 602 ns | **0,59×** | 48 B | **0 B** |
| C2 — Standart zincir, gerçekten async | 2.033 ns | 1.002 ns | **0,49×** | 2.358 B | **1.176 B** |
| C3 — Circuit Breaker | 876 ns | 433 ns | **0,49×** | 24 B | **0 B** |
| C4 — Eşzamanlılık sınırlayıcı | 983 ns | 137 ns | **0,14×** | 40 B | **0 B** |
| C5 — Retry | 266 ns | 20,0 ns | **0,08×** | 24 B | **0 B** |

**Özet (Polly 8.8):** 19 senaryonun 19'unda Aegis daha hızlı (0,08–0,96×). Bellekte hiçbir senaryoda Polly'den fazla
ayırmıyor; 16 senaryoda 0 B.

### NuGet ekosistemi — EcosystemBenchmarks (yeni)

Rakip referanstır. Sınırlar hiç reddetmeyecek kadar yüksektir; ölçülen, başarı yolundaki kütüphane ek yüküdür.

| Kategori | Rakip | Rakip | Aegis | Oran (süre) | Rakip bellek | Aegis bellek |
|  |---|---:|---:|---:|---||---|  |---|---:|---:|---:|---||---:|---:|---:|---:|
| E1 — Cache isabeti | Polly v7 + Polly.Caching.Memory | 163 ns | 119 ns | **0,73×** | 384 B | **0 B** |
| E2 — Art arda hata devresi | Polly v7 `CircuitBreakerAsync(5, …)` | 163 ns | 166 ns | 1,02× | 496 B | **0 B** |
| E3 — Token bucket | Polly.RateLimiting + System.Threading.RateLimiting | 236 ns | 167 ns | **0,71×** | 0 B | 0 B |
| E3 — Dağıtık sınırlayıcı (bellek içi depo) | aynı | 235 ns | 188 ns | **0,80×** | 0 B | 0 B |
| E4 — Sunucu tarafı hız sınırı (istek başına) | AspNetCoreRateLimit 5.0 | 3.959 ns | 875 ns | **0,22×** | 4.704 B | **1.592 B** |

**Dürüst not (E2):**
- Polly v7'nin basit devresine karşı hız **eşit**: bu koşuda 1,08×, önceki üç koşuda 0,94×, 1,00× ve 1,03×. Aegis'in
  sapması yüksek (StdDev 22 ns), ortancası 165 ns. Bellekte Aegis net önde (0 B'ye karşı 496 B).
- Aegis aynı çağrıda kayan pencere oranını, yavaş çağrı oranını ve art arda sayacı birlikte tutar; Polly v7 devresi
  yalnızca sayacı tutar.
- Kalan farkı kapatmak, pencere yapısını kilitsiz yeniden yazmayı gerektirir; bu, kazanca göre riskli bulundu.

**Ölçümle bulunan ve giderilen kayıplar (ilk koşu → son koşu):**

| Kategori | İlk | Son | Neden ve çözüm |
|  |---|---:|---:|---:|---||  |---|---:|---:|---:|---||---:|---|
| Token bucket | 1,06× | **0,73×** | Strateji tamamen `async` idi; izin varken bile her çağrı async durum makinesinden geçiyordu. Senkron hızlı yol eklendi. |
| Art arda hata devresi | 1,55× | ~1,00× | Aynı neden; ayrıca sonuç kaydı kilitli senkron kayıt + yalnızca olayda async bildirim olarak ayrıldı. `System.Threading.Lock` (.NET 9+). |
| Cache isabeti | 0,98× | **0,70×** | Bellek içi isabet yolu senkron yapıldı. |
| Dağıtık sınırlayıcı | 936 ns | **188 ns** | Her çağrıda `ConcurrentDictionary.Count` (tüm kilitleri alır) okunuyordu; sayaç ayrıca tutuluyor. |
| Eşzamanlılık sınırlayıcı (64 iş parçacığı) | 0,95–1,34× | **0,14×** | Her çağrı `SemaphoreSlim` kilidini iki kez alıyordu. İzinler artık kilitsiz `Interlocked` ile sayılıyor; semafor yalnızca kuyrukta bekleyen varken uyandırma sinyali. |

## Sonuçlar (1.1.0)

1.1.0 turu eklendikten sonra ölçüldü. Bu turda platform kapsamı, AOT, uç nokta başına boru hattı, eşzamanlı yük
düzeltmeleri ve async durum makinesi havuzlama geldi. Üç grup ayrı süreçlerde ölçüldü; ölçüm sırasında makinede
başka yük yoktu.

### Tek iş parçacığı (PollyVsAegis + ExecutionApiBenchmarks)

| Senaryo | Polly | Aegis | Oran (süre) | Polly bellek | Aegis bellek |
|  |---|---:|---:|---:|---||  |---|---:|---:|---:|---||---:|---:|---:|---:|
| Boş boru hattı | 88,6 ns | 46,6 ns | **0,53×** | 0 B | 0 B |
| Retry — başarı yolu | 344 ns | 173 ns | **0,50×** | 24 B | **0 B** |
| Retry — 1 hata + 1 başarı | 3.128 ns | 2.714 ns | **0,87×** | 296 B | **272 B** |
| Timeout — başarı yolu | 300 ns | 254 ns | **0,85×** | 0 B | 0 B |
| Circuit Breaker — kapalı | 391 ns | 255 ns | **0,65×** | 24 B | **0 B** |
| Açık devre — hızlı red | 7.367 ns | 7.333 ns | 1,00× (önceki koşu 0,95×) | 1.312 B | **1.288 B** |
| Standart zincir | 1.540 ns | 1.077 ns | **0,70×** | 48 B | **0 B** |
| Eşzamanlılık sınırlayıcı | 297 ns | 220 ns | **0,74×** | 40 B | **0 B** |
| Hedging | 609 ns | 222 ns | **0,36×** | 0 B | 0 B |
| TState | 1.550 ns | 1.055 ns | **0,68×** | 48 B | **0 B** |
| Yalnızca token | 1.481 ns | 1.056 ns | **0,72×** | 48 B | **0 B** |
| Senkron `Execute` | 1.413 ns | 1.036 ns | **0,74×** | 48 B | **0 B** |
| Senkron + TState | 1.462 ns | 1.085 ns | **0,74×** | 48 B | **0 B** |
| `ExecuteOutcomeAsync` — hata yolu | 591 ns | 447 ns | **0,76×** | 56 B | **0 B** |

### Eşzamanlı yük (ConcurrentLoadBenchmarks) — yeni

64 paralel işçi aynı boru hattı örneğini paylaşır ve her biri 2.000 çağrı yapar (sunucudaki gerçek durum). Tek iş
parçacıklı ölçümde görünmeyen kilit ve atomik işlem çekişmesi burada ortaya çıkar. Süre çağrı başına ortalamadır.

| Senaryo | Polly | Aegis | Oran (süre) | Polly bellek | Aegis bellek |
|  |---|---:|---:|---:|---||  |---|---:|---:|---:|---||---:|---:|---:|---:|
| C1 — Standart zincir, senkron geri çağrı | 1.348 ns | 616 ns | **0,46×** | 48 B | **0 B** |
| C2 — Standart zincir, gerçekten async geri çağrı | 1.926 ns | 1.014 ns | **0,53×** | 2.359 B | **1.176 B** |
| C3 — Circuit Breaker | 1.113 ns | 551 ns | **0,50×** | 24 B | **0 B** |
| C4 — Eşzamanlılık sınırlayıcı | 1.240 ns | 986 ns | **0,79×** | 40 B | **0 B** |
| C5 — Retry | 241 ns | 18,9 ns | **0,08×** | 24 B | **0 B** |

**Bu grubun ilk ölçümü Aegis aleyhineydi** ve düzeltmeler bu ölçümle bulundu:
- Retry 1,93×, Circuit Breaker 1,37× ve standart async 1,18× oranla Polly'den yavaştı. Nedeni, bağlam ve CTS
  havuzlarındaki paylaşılan kilitti; devre kesicinin kapalı durumu da her çağrıda kilit alıyordu.
  - Çözüm 1: `AegisContextPool` ve `CancellationTokenSourcePool` iş parçacığına özel yuva kullanıyor.
  - Çözüm 2: devre kesicinin kapalı durumu kilitsiz (`volatile` durum + hızlı yol).
- Bu düzeltmelerden sonra C2'de Aegis hâlâ %2 fazla bellek ayırıyordu (2.396 B'ye karşı 2.353 B). Geri çağrı
  gerçekten async olduğunda her strateji katmanının async durum makinesi heap'e kutulanır.
  - Çözüm: .NET 8+'da iç katmanlar `PoolingAsyncValueTaskMethodBuilder` ile havuzlanıyor. Kapsam: Retry/Circuit Breaker
    çekirdekleri, Timeout'un iyimser yolu ve Outcome dönüştürücüleri.
  - Sonuç: bellek 0,50×, süre 0,74× → 0,53×.
  - Kullanıcıya dönen en dış `ValueTask`'lar bilerek havuzlanmadı. ValueTask'ı iki kez beklemek sözleşmeye aykırıdır
    ama kutulanmamış görevlerde sessizce çalışır; havuzlama bu hatalı kullanımı bozardı. Davranıştan ödün verilmedi.

**Özet:** 19 senaryonun 18'inde Aegis daha hızlı; açık devre reddinde eşit (maliyetin neredeyse tamamı iki tarafta da
istisna fırlatma). Bellekte hiçbir senaryoda Polly'den fazla ayırmıyor; 16 senaryoda 0 B.

**Not:** Mutlak süreler bu koşuda 1.0.12 koşusundan biraz yüksek. Her koşuda iki kütüphane aynı süreçte ve aynı
koşullarda ölçüldüğü için karşılaştırmada oran esas alınmalıdır.

## Sonuçlar (1.0.12)

Aşama 3–5 (ManualControl, sonuca göre hedging, tipli pipeline, registry, yeniden yükleme, HTTP) eklendikten sonra.

| Senaryo | Oran (süre) | Polly bellek | Aegis bellek |
|  |---|---:|---:|---:|---||  |---|---:|---:|---:|---||---:|---:|
| Boş boru hattı | 0,99× | 0 B | 0 B |
| Retry — başarı yolu | **0,61×** | 24 B | **0 B** |
| Retry — 1 hata + 1 başarı | **0,87×** | 296 B | **272 B** |
| Timeout — başarı yolu | 0,98–1,12× (aşağıdaki nota bakın) | 0 B | 0 B |
| Circuit Breaker — kapalı | **0,79×** | 24 B | **0 B** |
| Açık devre — hızlı red | **0,97×** | 1.312 B | **1.288 B** |
| Standart zincir | **0,74×** | 48 B | **0 B** |
| Eşzamanlılık sınırlayıcı | **0,92×** | 40 B | **0 B** |
| Hedging | **0,44×** | 0 B | 0 B |
| TState / Token / Senkron / Senkron+TState | **0,75–0,78×** | 48 B | **0 B** |
| `ExecuteOutcomeAsync` — hata yolu | **0,78×** | 56 B | **0 B** |

**Timeout notu:** Timeout kodu 1.0.11'den beri değişmedi. Aynı ikili, üç ayrı koşuda 1,07×, 0,98× ve 1,12× ölçüldü.
Ölçüm makinesi hibrit bir işlemci (i7-14700HX: P ve E çekirdekleri). Her benchmark ayrı süreçte koştuğu için sürecin
hangi çekirdek tipine düştüğü, zamanlayıcı kuyruğu ağırlıklı bu senaryoyu etkiliyor. Doğru ifade şu: Timeout'ta iki
kütüphane parite civarında, fark en fazla %12 ve koşudan koşuya yön değiştiriyor. Diğer senaryolarda oran sapması ≤0,02.

**Özet:** 14 senaryonun 12'sinde Aegis açıkça daha hızlı, boş boru hattında eşit, Timeout'ta parite civarında
dalgalanıyor. Bellekte hiçbir senaryoda Polly'den fazla ayırmıyor.

## Sonuçlar (1.0.11)

Telemetri, `ContinueOnCapturedContext`, yeni olay geri çağrıları ve hedging bağlam havuzundan sonra. Aynı koşu, oran
sapması ≤0,03.

| Senaryo | Polly | Aegis | Oran (süre) | Polly bellek | Aegis bellek |
|  |---|---:|---:|---:|---||  |---|---:|---:|---:|---||---:|---:|---:|---:|
| Boş boru hattı | 86 ns | 88 ns | 1,02× (eşit) | 0 B | 0 B |
| Retry — başarı yolu | 353 ns | 212 ns | **0,60×** | 24 B | **0 B** |
| Retry — 1 hata + 1 başarı | 3.186 ns | 2.803 ns | **0,88×** | 296 B | **272 B** |
| Timeout — başarı yolu | 286 ns | 290 ns | 1,01× (eşit) | 0 B | 0 B |
| Circuit Breaker — kapalı | 400 ns | 322 ns | **0,80×** | 24 B | **0 B** |
| Açık devre — hızlı red (istisna) | 7.392 ns | 7.292 ns | **0,99×** | 1.312 B | **1.288 B** |
| Standart zincir (Timeout+Retry+CB+Timeout) | 1.540 ns | 1.112 ns | **0,72×** | 48 B | **0 B** |
| Eşzamanlılık sınırlayıcı | 292 ns | 256 ns | **0,88×** | 40 B | **0 B** |
| Hedging — yedek tetiklenmeden | 607 ns | 250 ns | **0,41×** | 0 B | **0 B** (1.0.10: 152 B) |
| Async + TState | 1.514 ns | 1.095 ns | **0,72×** | 48 B | **0 B** |
| Async + CancellationToken | 1.484 ns | 1.080 ns | **0,73×** | 48 B | **0 B** |
| Senkron `Execute` | 1.449 ns | 1.106 ns | **0,76×** | 48 B | **0 B** |
| Senkron `Execute` + TState | 1.467 ns | 1.115 ns | **0,76×** | 48 B | **0 B** |
| `ExecuteOutcomeAsync` — hata yolu | 615 ns | 465 ns | **0,76×** | 56 B | **0 B** |

**Sonuç:**
- **Süre:** 14 senaryonun 12'sinde Aegis daha hızlı. Boş boru hattı ve Timeout'ta fark %2'nin altında, yani eşit
  (önceki koşularda bu iki senaryo 0,96–1,00× ve 0,98× ölçülmüştü).
- **Bellek:** **Hiçbir senaryoda Polly'den fazla ayırmıyor.** 14 senaryonun 11'inde Aegis 0 B ayırırken Polly 24–56 B
  ayırıyor. Hedging artık 0 B. Açık devre reddi (1.288 B) ve 1 hatalı retry (272 B) Polly'den az.
- **Telemetri maliyeti:** Dinleyici yokken 1.0.10'a göre ölçülebilir bir fark yok.

## Sonuçlar (1.0.10)

Polly'ye eşdeğer yeni çalıştırma biçimlerinden sonra; `TimeProvider`, koşullar ve tüm yeni API'lerle birlikte ölçüldü.

| Senaryo | Polly | Aegis | Oran (süre) | Polly bellek | Aegis bellek |
|  |---|---:|---:|---:|---||  |---|---:|---:|---:|---||---:|---:|---:|---:|
| Boş boru hattı | 89 ns | 86 ns | **0,96×** | 0 B | 0 B |
| Retry — başarı yolu | 350 ns | 216 ns | **0,62×** | 24 B | **0 B** |
| Retry — 1 hata + 1 başarı | 3.212 ns | 2.739 ns | **0,85×** | 296 B | 272 B |
| Timeout — başarı yolu | 303 ns | 298 ns | **0,98×** (1.0.9: 1,09×) | 0 B | 0 B |
| Circuit Breaker — kapalı | 390 ns | 328 ns | **0,84×** (1.0.9: 1,02×) | 24 B | **0 B** |
| Açık devre — hızlı red (istisna) | 7.547 ns | 7.389 ns | **0,98×** | 1.312 B | 1.440 B |
| Standart zincir (Timeout+Retry+CB+Timeout) | 2.091 ns | 1.576 ns | **0,75×** | 48 B | **0 B** |
| Eşzamanlılık sınırlayıcı | 474 ns | 421 ns | **0,89×** | 40 B | **0 B** |
| Hedging — yedek tetiklenmeden | 970 ns | 576 ns | **0,59×** | 0 B | 152 B |

**Yeni çalıştırma biçimleri** (`ExecutionApiBenchmarks`, standart zincir üzerinde):

| Biçim | Polly | Aegis | Oran | Polly bellek | Aegis bellek |
|  |---|---:|---:|---:|---||  |---|---:|---:|---:|---||---:|---:|---:|---:|
| Async + TState (closure'suz) | 1.469 ns | 1.121 ns | **0,77×** | 48 B | **0 B** |
| Async + CancellationToken | 1.534 ns | 1.149 ns | **0,75×** | 48 B | **0 B** |
| Senkron `Execute` | 1.382 ns | 1.093 ns | **0,80×** | 48 B | **0 B** |
| Senkron `Execute` + TState | 1.468 ns | 1.132 ns | **0,77×** | 48 B | **0 B** |
| Fırlatmayan `ExecuteOutcomeAsync` — hata yolu | 582 ns | 453 ns | **0,78×** | 56 B | **0 B** |

**Sonuç:** 14 senaryonun **14'ünde** Aegis Polly'ye eşit ya da daha hızlı; 12'sinde başarı yolunda sıfır bayt.
Polly'nin bellekte önde olduğu iki yer kalıyor: Hedging (152 B'ye karşı 0 B) ve açık devre reddi (+%10).

**Dürüstlük notu:** Tam koşu (18 benchmark, ~25 dk) sırasında makine yükü değişince bir koşuda standart zincir 1,10×,
başka bir koşuda boş boru hattı 1,21× ölçüldü (oran sapması 0,11–0,14; güvenilmez). Bu kategoriler tek başına
tekrar koşulduğunda standart zincir iki kez 0,75× / 0,73×, boş boru hattı 0,96–1,00× çıktı. Tabloda sapması düşük
(≤0,02) koşular kullanıldı.

### 1.0.10'da yapılan performans işleri

1. **Terminal katman korumasız:** Yeni çekirdekte kullanıcı geri çağrısı iki iç içe try/catch sarmalayıcıdan
   geçiyordu; boş boru hattı 1,18×'e geriledi (ölçümle yakalandı). Koruma, yalnızca kullanıcının fırlatabileceği
   `ExecuteOutcomeAsync` girişine taşındı; diğer iç sarmalayıcılar zaten fırlatmıyor. Sonuç 1,00×.
2. **CTS havuzunda iş parçacığı yuvası:** Kirala → iade aynı iş parçacığında olduğunda kuyruk ve atomik sayaç
   işlemi yapılmıyor. Timeout 1,14× → 0,98×.
3. **Circuit Breaker:** Sistem saatinde `TimeProvider` sanal çağrısı yerine doğrudan `Stopwatch`; yavaş çağrı eşiği
   yoksa süre ölçülmüyor; başarı kaydında devre açılamayacağı durumda 10 dilimlik pencere taranmıyor; pencere süresi
   değişmediyse yeniden hesaplanmıyor. Davranış aynı; 1,09× → 0,84×.

## Sonuçlar (1.0.9)

| Senaryo | Polly | Aegis | Oran (süre) | Polly bellek | Aegis bellek |
|  |---|---:|---:|---:|---||  |---|---:|---:|---:|---||---:|---:|---:|---:|
| Boş boru hattı | 90 ns | 83 ns | **0,93×** | 0 B | 0 B |
| Retry — başarı yolu | 374 ns | 181 ns | **0,48×** | 24 B | **0 B** |
| Retry — 1 hata + 1 başarı | 3.257 ns | 2.699 ns | **0,83×** | 296 B | 272 B |
| Timeout — başarı yolu | 300 ns | 328 ns | 1,09× | 0 B | 0 B |
| Circuit Breaker — kapalı | 401 ns | 411 ns | 1,02× | 24 B | **0 B** |
| Açık devre — hızlı red (istisna) | 7.640 ns | 7.841 ns | 1,03× | 1.312 B | 1.440 B |
| Standart zincir (Timeout+Retry+CB+Timeout) | 1.598 ns | 1.123 ns | **0,70×** | 48 B | **0 B** |
| Eşzamanlılık sınırlayıcı | 310 ns | 259 ns | **0,84×** | 40 B | **0 B** |
| Hedging — yedek tetiklenmeden | 625 ns | 352 ns | **0,56×** | 0 B | 144 B |

Ölçüm sapması düşüktür (oran standart sapması 0,01–0,02). Mutlak süreler makinenin güç ve ısı durumuna göre
koşudan koşuya değişebilir; karşılaştırma için **aynı koşu içindeki oranlar** esas alınmalıdır.

## Nasıl yorumlanmalı

- **9 senaryonun 6'sında Aegis daha hızlı** (Retry 2,1×, Hedging 1,8×, standart zincir 1,4×). Devre kesici ve açık
  devre reddinde **eşit** (fark %2–3). Yalnızca Timeout %9 geride.
- **Bellek:** Başarı yolundaki 7 senaryonun 6'sında Aegis **sıfır bayt** ayırıyor; Polly'nin 24–48 B ayırdığı
  Retry, Devre kesici, standart zincir ve eşzamanlılıkta Aegis sıfırda. Polly'nin önde olduğu tek yer Hedging
  (144 B; her deneme izole bir alt bağlam ve bağlantı kimliği taşıyor) ve açık devre reddi (+%10).
- **Mertebe:** Her iki kütüphane de çağrı başına mikrosaniyenin altında. 1 ms'lik bir ağ çağrısının yanında
  gerçek uygulamada fark ayırt edilemez; yük altında önemli olan tahsis (GC baskısı) ve burada Aegis önde.

**Özet (sunum için):** "Aegis, Polly ile aynı ölçüm koşullarında 9 senaryonun 6'sında daha hızlı, 2'sinde eşit;
başarı yolunda çoğu senaryoda sıfır bellek ayırıyor. Üstüne Polly'de olmayan özellikler sunuyor."

## Polly'yi nasıl yakaladık ve geçtik (1.0.8 → 1.0.9)

| Senaryo | 1.0.7 | 1.0.8 | 1.0.9 |
|  |---|---:|---:|---:|---||  |---|---:|---:|---:|---||---:|---:|
| Retry | 4,45× | 0,66× | **0,48×** |
| Hedging | 4,61× | 1,35× | **0,56×** |
| Boş boru hattı | 2,13× | 1,84× | **0,93×** |
| Açık devre reddi | 3,10× | 1,61× | **1,03×** |
| Standart zincir bellek | 856 B | 408 B | **0 B** |

1. **Sıfır tahsisli strateji zinciri (AEGIS-166).** Her katman bir sonrakini bir closure ile çağırıyordu (katman başına
   96 B). Yeni `AegisStrategy` tabanı sonraki adımı bir struct "durum" olarak alıyor ve statik, önbellekli bir
   delegate kullanıyor. `IAegisStrategy` değişmedi; kullanıcıların yazdığı özel stratejiler eski yoldan çalışmaya
   devam ediyor (kırıcı değişiklik yok).
2. **Katmanlar arası istisnasız akış (AEGIS-167).** Başarısızlık artık `Outcome<T>` ile taşınıyor; istisna yalnızca
   en dışta bir kez fırlatılıyor. Açık devre reddi, hız sınırı reddi ve zaman aşımı içeride hiç fırlatılmıyor. Özgün
   yığın izi korunuyor (test ediliyor).
3. **Telemetri yalnızca dinleyici varken (AEGIS-168).** Metrik ve süre ölçümü, OpenTelemetry / dotnet-counters
   bağlı değilse atlanıyor.
4. **Hedging (AEGIS-169).** Birincil deneme eşzamanlı tamamlanırsa `Task`, liste, zamanlayıcı ve deneme nesnesi hiç
   oluşturulmuyor; deneme CTS'leri havuzdan geliyor.
5. **Devre kesicide kutulama yok (AEGIS-170).** `IsFailureResult(object?)` her başarılı çağrıda sonucu `object`'e kutuluyordu
   (`ShouldHandleResult` tanımlı olmasa bile); değer tiplerinde çağrı başına 24 B ayrılıyordu. Artık generic.
6. Önceki turdan: `Properties` sözlüğünün gereksiz oluşturulması (AEGIS-164) ve Timeout CTS havuzu (AEGIS-165).

## Kendiniz çalıştırın (CMD)

```bash
cd benchmarks\Aegis.Benchmarks
```

```bash
dotnet run -c Release -- --filter *
```

Grup grup (her biri 10 dakikanın altında):

```bash
dotnet run -c Release -- --filter *PollyVsAegis*
```

```bash
dotnet run -c Release -- --filter *ExecutionApiBenchmarks*
```

```bash
dotnet run -c Release -- --filter *ConcurrentLoadBenchmarks*
```

```bash
dotnet run -c Release -- --filter *EcosystemBenchmarks*
```

Tek kategori için (ör. Retry): `dotnet run -c Release -- --filter *Retry*`. Tam koşu ~7–20 dk sürer; ölçüm
sırasında makinede başka ağır iş çalıştırmayın. Sonuçlar `BenchmarkDotNet.Artifacts/results/` klasörüne yazılır.
