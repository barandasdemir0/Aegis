# Batırma (torture) test sonuçları

Tarih: 2026-10-03 21:41 · Tur: 5 · Taban tohum: 777 · .NET 10.0.12 · 28 çekirdek

Rakipler: Polly.Core 8.8.0, Polly.RateLimiting 8.8.0, Microsoft.Extensions.Http.Resilience 10.10.0 (NuGet'te en son sürüm).

| Senaryo | Aegis | Polly 8.8.0 | MS Http.Resilience 10.10.0 |
|---|---|---|---|
| Thread fırtınası + rastgele iptal | ✅ 5/5 | ✅ 5/5 | — |
| Eşzamanlılık sınırı: izin sızıntısı | ✅ 5/5 | ✅ 5/5 | — |
| Tek iş parçacıklı bağlamda sync-over-async | ✅ 5/5 | ✅ 5/5 | — |
| Çağıran iptali: token yayılımı | ✅ 5/5 | ✅ 5/5 | — |
| İz açıkken fırtına (span sızıntısı, ebeveyn karışması) | ✅ 5/5 | — | — |
| Uç seçenek değerleri (taşma) | ✅ 5/5 | ✅ 5/5 | — |
| Fırlatan geri çağrılar (zehirlenme) | ✅ 5/5 | ❌ 0/5 | — |
| Çağrı sürerken dispose yarışı | ✅ 5/5 | ✅ 5/5 | — |
| Yeniden yükleme fırtınası (geçersiz değer dahil) | ✅ 5/5 | ✅ 5/5 | — |
| Saat sıçraması (100 / 1000 yıl) | ✅ 5/5 | ✅ 5/5 | — |
| Hız sınırı: fazla kabul yok | ✅ 5/5 | ✅ 5/5 | — |
| Bellek dayanıklılığı (1M çağrı) | ✅ 5/5 | ✅ 5/5 | — |
| Hedging: kaybeden yanıt sızıntısı | ✅ 5/5 | ✅ 5/5 | ✅ 5/5 |
| HTTP senkron Send yeniden denenir | ✅ 5/5 | — | ✅ 5/5 |
| Bozuk Retry-After başlıkları | ✅ 5/5 | — | ❌ 0/5 |
| Geri sarılamayan gövdeli POST | ✅ 5/5 | — | ✅ 5/5 |

✅ tüm turlar geçti · ❌ en az bir tur kaldı · — kütüphane bu yeteneği sunmuyor / senaryo uygulanmıyor

## Kalan turların ilk nedeni

- **Fırlatan geri çağrılar (zehirlenme) / Polly** (0/5): tur 0 (tohum 467857093): fırlatan Listener, OnHalfOpened yüzünden hata kesildikten 5 açık kalma süresi sonra bile sağlam çağrı geçemedi (boru hattı zehirlendi)
- **Bozuk Retry-After başlıkları / Microsoft** (0/5): tur 0 (tohum 852442475): Retry-After 'Fri, 31 Dec 9999 23:59:59 GMT': ArgumentOutOfRangeException: The value needs to translate in milliseconds to -1 (signifying an infinite timeout), 0, or a positive integer less than or equal to the maximum allowed timer duration. (Parameter 'delay')

## Senaryolar

- **Thread fırtınası + rastgele iptal**: 32×400 eşzamanlı çağrı (retry → devre kesici → 50 ms zaman aşımı); %20 fırlatır, %10 yavaş, %10 çağıran rastgele anda iptal eder. Her çağrı kendi sonucunu almalı, çağıran iptal etmeden OperationCanceledException sızmamalı, yalnızca beklenen tipler yükselmeli.
- **Eşzamanlılık sınırı: izin sızıntısı**: 8 izinli sınırlayıcı, 64×200 çağrı, bekleme ve yürütme sırasında rastgele iptal. İçerideki çağrı sayısı hiç 8'i aşmamalı; fırtına sonrası tam 8 çağrı aynı anda girebilmeli, 9. giremez (sızan ya da fazladan izin yok).
- **Tek iş parçacıklı bağlamda sync-over-async**: UI benzeri tek iş parçacıklı SynchronizationContext içinde ExecuteAsync(...).GetAwaiter().GetResult() ve senkron Execute; retry (1 ms bekleme) + zaman aşımı. Kütüphane yakalanan bağlama dönerse kilitlenir.
- **Çağıran iptali: token yayılımı**: Zaman aşımı (10 sn) ve hedging (3 deneme, 5 ms) içinde bekleyen çağrı rastgele anda iptal edilir. Yükselen OperationCanceledException.CancellationToken çağıranın token'ı olmalı; hedging'de başlatılan her deneme iptali görmeli.
- **İz açıkken fırtına (span sızıntısı, ebeveyn karışması)**: Tüm örnekleme açık; 32×300 çağrı (hata, zaman aşımı, rastgele iptal, eşzamansız alt span). Her Aegis span'ı kapanmalı, her alt span kendi çağrısının span'ının çocuğu olmalı, çağıranın Activity.Current'ı çağrıdan sonra yerinde olmalı.
- **Uç seçenek değerleri (taşma)**: int.MaxValue deneme + 1 gün üstel gecikme + TimeSpan.MaxValue üst sınır; TimeSpan.MaxValue ve sonsuz zaman aşımı; TimeSpan.MaxValue açık kalma; TimeSpan.MaxValue pencere. Ya kurulumda reddedilmeli ya doğru çalışmalı; çalışma anında taşma olmamalı.
- **Fırlatan geri çağrılar (zehirlenme)**: OnRetry, ShouldHandle, DelayGenerator, OnOpened/OnHalfOpened/OnClosed ve telemetri dinleyicisi fırlatır. Kilitlenme olmamalı; devre takılı kalmamalı: hata kesilince sağlam çağrı birkaç açık kalma süresi içinde yine başarılı olmalı.
- **Çağrı sürerken dispose yarışı**: 32 iş parçacığı sürekli çağırırken boru hattı (Polly: kayıt defteri) rastgele anda dispose edilir. Yalnızca başarı, ObjectDisposedException ya da iptal yükselmeli; NullReference/InvalidOperation gibi iç hata ve kilitlenme olmamalı.
- **Yeniden yükleme fırtınası (geçersiz değer dahil)**: 16 iş parçacığı çağırırken seçenekler her milisaniye değişir (Aegis: OptionsProvider, Polly: AddReloadToken); değerlerin %20'si geçersizdir. Çağrılar yalnızca başarı / işlenen hata / devre reddi görmeli; kilitlenme olmamalı.
- **Saat sıçraması (100 / 1000 yıl)**: Sahte saat 100 ve 1000 yıl ileri alınır. Açık devre sıçramadan sonra kapanabilmeli; bekleyen çağrının zaman aşımı tetiklenmeli; süre hesapları taşmamalı.
- **Hız sınırı: fazla kabul yok**: 1 saatlik pencerede 100 izin; 64×200 eşzamanlı çağrı. Tam olarak 100 çağrı kabul edilmeli.
- **Bellek dayanıklılığı (1M çağrı)**: Retry → devre kesici → zaman aşımı zincirinden 1.000.000 çağrı (%1 hata). Canlı bellek 4 MB'tan fazla büyümemeli.
- **Hedging: kaybeden yanıt sızıntısı**: Yanıtlar 0-30 ms rastgele gecikir, %25'i 503; 120 istek, 4'er eşzamanlı, 5 ms'de yedek deneme. Çağıran yanıtını bıraktıktan sonra sunucunun ürettiği tüm yanıt gövdeleri dispose edilmiş olmalı.
- **HTTP senkron Send yeniden denenir**: Sunucu 503, 503, 200 döner; senkron HttpClient.Send çağrısı yeniden denenip 200 almalı.
- **Bozuk Retry-After başlıkları**: 429 yanıtında Retry-After: -1, abc, boş, 1.5, 20 haneli sayı, int/long üst sınırı, 9999 yılı, 1970, bozuk tarih. Üst sınır 1 sn iken her istek 5 sn içinde 200 ile bitmeli; istisna olmamalı.
- **Geri sarılamayan gövdeli POST**: 64 KB ileri-okunur akış gövdeli POST (Idempotency-Key ile), ilk yanıt 503. Sunucuya giden her gövde özgün veriyle aynı olmalı (boş/kesik yeniden gönderim = veri bozulması); sonuç 200, 503 ya da HttpRequestException olmalı.
