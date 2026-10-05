> **Arşiv raporu.** Bu doğrulama 1.x sürümlerinde, eski vitrin uygulaması (`AegisShowcase`) ve onun Kubernetes manifestleriyle
> yapıldı. 2.0.0'da örnek projeler tek bir `samples/RealWorld` projesinde birleştirildi ve vitrin kaldırıldı; aşağıdaki komutlar
> o uygulamaya aittir. Dağıtık davranışlar (iki pod arası Redis devre/kota/önbellek, Redis'e geç bağlanan pod) artık
> `samples/RealWorld` testlerinde otomatik doğrulanır.

# ☸️ Aegis — Kubernetes Küme Testleri

Bu klasör, kütüphaneyi **gerçek bir Kubernetes kümesinde** denemek için gereken her şeyi içerir. Küme 3 düğümlüdür
(1 kontrol düzlemi + 2 çalışan). Üzerinde Redis ve 3 uygulama pod'u çalışır. Pod'lar dağıtık devre kesiciyi Redis
üzerinden paylaşır.

| Dosya | Ne işe yarar |
|---|---|
| `kind-config.yaml` | 3 düğümlü `aegis` kümesi, NodePort 30090 → `http://127.0.0.1:30090` |
| `redis.yaml` | Redis 7 (Deployment + Service `redis:6379`) |
| `aegis.yaml` | Showcase API, 3 kopya, düğümlere yayılmış; `/health` readiness ve liveness probe'ları |
| `Dockerfile` | Yayımlanmış uygulamayı taşıyan çalışma zamanı imajı |
| `test-cluster.sh` | 9 senaryoluk otomatik küme testi (PASS/FAIL raporu) |
| `load.js` | k6 yük testi (100 sanal kullanıcı, 60 sn) |

> Uygulama pod'larının kullandığı test uç noktaları `api/cluster/*`'tır (`ClusterController`). Her pod, `cluster-gateway`
> adlı **tek** dağıtık devreyi Redis'te paylaşır (açılma süresi 8 sn, en az 3 çağrı, %50 hata oranı).

---

## 1. Kurulum (bir kez)

Docker Desktop açık olmalı. Sonra CMD'de:

```bash
winget install --id Kubernetes.kind -e
```

```bash
winget install --id GrafanaLabs.k6 -e
```

`kubectl` Docker Desktop ile birlikte gelir. Kurulumdan sonra **yeni bir CMD penceresi** açın (PATH yenilensin).

## 2. Kümeyi kur ve uygulamayı dağıt

```bash
cd C:\Users\Baran\Desktop\Nuget\AegisShowcase\k8s
```

```bash
kind create cluster --config kind-config.yaml
```

Uygulamayı yerel NuGet paketleriyle yayımla ve imajı derle:

```bash
dotnet publish ..\AegisShowcase.Api\AegisShowcase.Api.csproj -c Release -o publish
```

```bash
docker build -t aegis-showcase:1.4.0 .
```

İmajları kümeye yükle. `kind load docker-image` Docker Desktop'ın çok platformlu imaj deposunda
`content digest ... not found` hatası verir; bu yüzden tek platformlu arşiv kullanılır:

```bash
docker save --platform linux/amd64 -o aegis.tar aegis-showcase:1.4.0
```

```bash
docker save --platform linux/amd64 -o redis.tar redis:7-alpine
```

```bash
kind load image-archive aegis.tar --name aegis
```

```bash
kind load image-archive redis.tar --name aegis
```

```bash
kubectl apply -f redis.yaml -f aegis.yaml
```

```bash
kubectl get pods -o wide
```

Tarayıcıdan deneyin: `http://127.0.0.1:30090/api/cluster/whoami` (her yenilemede farklı pod yanıt verebilir),
`http://127.0.0.1:30090/aegis` (pano), `http://127.0.0.1:30090/health`.

> `localhost` yerine `127.0.0.1` kullanın. Windows'ta `localhost` önce IPv6'yı denediği için her istek ~200 ms gecikir.

## 3. Otomatik küme testleri

Test betiği bash betiğidir; Git Bash ile çalıştırın (CMD'den):

```bash
"C:\Program Files\Git\bin\bash.exe" test-cluster.sh
```

| # | Senaryo | Neyi kanıtlar |
|---|---|---|
| S1 | Redis uygulamadan **sonra** kuruldu | Pod'lar yeniden başlatılmadan Redis'e bağlanır; bir pod'un yazdığını diğerleri okur |
| S2 | Yük dengeleme | NodePort istekleri birden çok pod'a, pod'lar birden çok düğüme dağılır |
| S3 | Bir pod devreyi açar | Diğer pod'lar **kendi hiç hata görmeden** reddeder, arka uç çağrılmaz |
| S4 | Açık devre + probe'lar | `/health` = Degraded (HTTP 200); Kubernetes pod'ları **yeniden başlatmaz** |
| S5 | HalfOpen fırtınası | 3 pod'dan 15 eşzamanlı istek → küme genelinde **tek** deneme isteği, `OnHalfOpened` 1 kez |
| S6 | Deneme yapan pod öldürülür | Kira `BreakDuration` sonunda düşer, başka pod devreyi kurtarır; devre kilitli kalmaz |
| S7 | Redis kesintisi (`replicas=0`) | Çağrılar hızlı yanıt verir (fail-open), devre yerel çalışır ve kalıcı açık kalmaz; Redis dönünce paylaşım kendiliğinden geri gelir |
| S8 | Rolling restart | Devre durumu Redis'te yaşar; tamamen yeni pod'lar izole devreyi görür |
| S9 | Redis anahtarları | Open değeri `1|600000`, ömrü `BreakDuration + 10 dk`; reset anahtarları siler |

## 4. Yük testi (k6)

```bash
k6 run load.js
```

Yük altında pod öldürme: ikinci bir CMD'de pod adını alın, test başladıktan ~15 sn sonra **bir** pod'u silin:

```bash
kubectl get pods -l app=aegis
```

```bash
kubectl delete pod <pod-adı> --wait=false
```

Yük altında Redis kesintisi (test sürerken, ~25 sn arayla):

```bash
kubectl scale deployment/redis --replicas=0
```

```bash
kubectl scale deployment/redis --replicas=1
```

## 5. Elle deneme komutları

```bash
curl "http://127.0.0.1:30090/api/cluster/call?fail=true"
```

```bash
curl http://127.0.0.1:30090/api/cluster/stats
```

```bash
kubectl exec deploy/redis -- redis-cli --scan --pattern "aegis:cb:*"
```

```bash
kubectl scale deployment/redis --replicas=0
```

```bash
kubectl scale deployment/redis --replicas=1
```

## 6. Temizlik

```bash
kind delete cluster --name aegis
```

## 7. Bu testlerin bulduğu kütüphane hataları

Gerçek küme, birim testlerinin göremediği iki hatayı ortaya çıkardı. İkisi de 1.0.6'da düzeltildi ve birim testi
eklendi (bkz. `CHANGELOG.md`, AEGIS-157):

1. **Redis henüz yokken açılan uygulama her isteğe HTTP 500 dönüyordu.** Kubernetes'te pod'un Redis'ten önce
   hazır olması olağan bir durumdur. `AddAegisRedisStateStore(string)` bağlantıyı `abortConnect=true` ile kuruyordu:
   depo oluşturulamıyor, istisna her isteğe sızıyor ve her istek bağlantıyı baştan deniyordu (istek başına 10–15 sn).
2. **Redis kesintisinde her çağrı ~5 sn gecikiyordu.** StackExchange.Redis bağlantı yokken komutları varsayılan olarak
   kuyrukta bekletir. Artık bağlantı arka planda kurulur ve komutlar anında başarısız olur (`BacklogPolicy.FailFast`).
   Depo o sırada yerel duruma düşer.

## 8. Son koşunun sonuçları (1.0.6)

Ortam: Windows 11, Docker Desktop 29.6, kind v0.33 (Kubernetes v1.37, 3 düğüm), Redis 7, 3 uygulama pod'u.

**Senaryo paketi (`test-cluster.sh`): 23 PASS / 0 FAIL**

| Senaryo | Ölçülen |
|---|---|
| S1 Redis sonradan geldi | Pod'lar yeniden başlatılmadan bağlandı; pod-1'in izolasyonunu pod-2 ve pod-3 okudu |
| S2 Dağılım | 30 istek 3 farklı pod'a, pod'lar 2 çalışan düğüme dağıldı |
| S3 Paylaşılan açılış | Pod-2 ve pod-3 reddetti, arka uçları hiç çağrılmadı |
| S4 Health | `Degraded` (HTTP 200), 12 sn gözlemde 0 yeniden başlatma |
| S5 Fırtına | 15 eşzamanlı istek → 1 başarı / 14 red; arka uç 1 kez, `OnHalfOpened` 1 kez |
| S6 Deneme yapan pod öldürüldü | Devre 7 sn'de (kira süresi 8 sn) başka pod üzerinden kurtuldu |
| S7 Redis kesintisi | Yanıt 14 ms; devre yerel açıldı, süre sonunda kapandı; 0 yeniden başlatma; Redis dönünce paylaşım geri geldi |
| S8 Rolling restart | 3 yeni pod izole devreyi Redis'ten okuyup reddetti |
| S9 Redis anahtarları | `1|600000`, PTTL 607.319 ms; reset tüm anahtarları sildi |

**k6 yük testleri (100 sanal kullanıcı, 60 sn, her istek Redis'e 2 kez gider):**

| Koşu | İstek | Hız | Hata | p95 |
|---|---|---|---|---|
| Normal | 281.634 | 4.694/sn | %0 | 31 ms |
| 20. sn'de bir pod silindi | 289.532 | 4.825/sn | %0 | 30 ms |
| 15.–40. sn arası Redis kapalı | 305.797 | 5.096/sn | %0 | 30 ms |

### 1.0.9 (sıfır tahsisli strateji zinciri) ile aynı testler

Senaryo paketi **25/25** (sertleştirilmiş manifestler). k6, aynı makine ve aynı ayarlar:

| Koşu | İstek | Hız | Hata | p95 |
|---|---|---|---|---|
| Normal | 492.623 | **8.210/sn** (1.0.6: 4.694/sn) | %0 | **18 ms** (1.0.6: 31 ms) |
| 15.–40. sn arası Redis kapalı | 550.326 | 9.172/sn | %0 | 18 ms |

### 1.0.12 (Polly/Microsoft eşitliği tamamlandı) ile aynı testler

Senaryo paketi **25/25**. Dağıtık devre kesicideki `ManualControl` değişikliği davranışı etkilemedi: HalfOpen'da küme
genelinde tek deneme, Redis kesintisinde fail-open ve rolling restart'ta durumun korunması aynen çalışıyor.
k6 sonuçları (aynı makine, aynı ayarlar):

| Koşu | İstek | Hız | Hata | p95 |
|---|---|---|---|---|
| Normal | 484.337 | 8.072/sn (1.0.9: 8.210/sn) | %0 | 18,5 ms (1.0.9: 18 ms) |

Fark ölçüm gürültüsü içinde. 1.0.10–1.0.12'de eklenenler (telemetri, DI'da otomatik `ILogger`, yeni çalıştırma
biçimleri, hedging bağlam havuzu) gerçek küme veriminde gerileme yaratmadı.

### 1.1.0 (platform kapsamı, AOT, eşzamanlı yük düzeltmeleri, async havuzlama) ile aynı testler

Senaryo paketi **25/25**. Bu turda üç çekirdek değişiklik var: iş parçacığına özel bağlam/CTS havuzu, kilitsiz kapalı
devre ve iç async katmanların havuzlanması. HalfOpen'da küme genelinde tek deneme, Redis kesintisinde fail-open ve
rolling restart'ta durumun korunması aynen çalışıyor. k6 sonuçları (aynı makine, aynı ayarlar):

| Koşu | İstek | Hız | Hata | p95 |
|---|---|---|---|---|
| Normal | 498.756 | **8.312/sn** (1.0.12: 8.072/sn) | %0 | **17,4 ms** (1.0.12: 18,5 ms) |

Küme verimi uygulama sunucusu, ağ ve Redis ile sınırlıdır. Bu yüzden benchmark'taki kütüphane içi kazanç buraya küçük
bir iyileşme olarak yansıyor; gerileme yok.

### 1.2.0 (sunucu tarafı koruma, dağıtık hız sınırlayıcı, kilitsiz eşzamanlılık sınırlayıcı) ile aynı testler

Senaryo paketi **25/25**. k6 sonuçları (aynı makine, aynı ayarlar):

| Koşu | İstek | Hız | Hata | p95 |
|---|---|---|---|---|
| Normal | 484.050 | 8.067/sn (1.1.0: 8.312/sn) | %0 | 18,2 ms (1.1.0: 17,4 ms) |

Fark ~%3; önceki koşuların aralığında (8.072–8.312/sn) ve ölçüm gürültüsü içinde. Küme verimi uygulama sunucusu, ağ ve
Redis ile sınırlıdır; kütüphane içi kazanç (benchmark) buraya doğrudan yansımaz, gerileme de yoktur.

### 1.3.0 (Polly 8.8 / Microsoft 10.10 eşitliği, batırma düzeltmeleri) ile aynı testler

Küme test paketi **25/25** (Redis kesintisi, pod yenileme, dağıtık devre senaryoları dahil). Aynı makine, aynı ayarlar.
Pod'lar `aegis-showcase:1.3.0` imajıyla çalıştı ve hiç yeniden başlamadı. k6 sonuçları:

| Koşu | İstek | Hız | Hata | p95 |
|---|---|---|---|---|
| Normal | 496.010 | 8.267/sn (1.2.0: 8.067/sn) | %0 | 17,7 ms (1.2.0: 18,2 ms) |

Fark %2,5 ve önceki koşuların aralığında (8.067–8.312/sn); ölçüm gürültüsü içinde sayılır, gerileme yok. Küme verimi
uygulama sunucusu, ağ ve Redis ile sınırlıdır. Kümeyi durmuş halden açmak için `docker start aegis-control-plane
aegis-worker aegis-worker2` yeterliydi; eski pod'lar `Unknown` durumdaydı ve yeni dağıtımla yenilendi.

### 1.4.0 (Aspire entegrasyonu, Web API 2 hız sınırı) ile aynı testler

Küme test paketi **25/25**. Showcase yerelde **29/29**. Pod'lar `aegis-showcase:1.4.0` ile çalıştı, hiç yeniden başlamadı.
k6 normal yük testi üç kez koşuldu (aynı makine, aynı ayarlar, arka arkaya):

| Koşu | İstek | Hız | Hata | p95 |
|---|---|---|---|---|
| 1 | 482.818 | 8.047/sn | %0 | 18,5 ms |
| 2 | 499.765 | 8.329/sn | %0 | 17,9 ms |
| 3 | 521.274 | 8.688/sn | %0 | 16,8 ms |

Koşular arası yayılım %8: tek koşu 1.3.0 ile (8.267/sn) 1.4.0 arasında fark olduğunu göstermeye yetmez. İlk koşu üçünün en
düşüğüydü ve önceki sürümlerin aralığının (8.067–8.312/sn) hemen altında kaldı; medyan 8.329/sn. Gerileme kanıtı yok.
Küme verimi uygulama sunucusu, ağ ve Redis ile sınırlıdır.
