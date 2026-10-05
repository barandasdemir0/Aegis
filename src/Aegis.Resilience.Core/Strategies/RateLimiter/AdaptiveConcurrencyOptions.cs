using System.Diagnostics;
using System.Runtime.CompilerServices;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Strategies.RateLimiter;

/// <summary>
/// Kendi kendini eğiten (Self-Tuning) Adaptif Eşzamanlılık seçenekleri (Netflix Gradient Algoritması).
/// </summary>
public sealed class AdaptiveConcurrencyOptions
{
    public int MinConcurrency { get; set; } = 5;
    public int MaxConcurrency { get; set; } = 100;
    public int InitialConcurrency { get; set; } = 20;
    /// <summary>
    /// Kapasite doluyken en fazla bekleme (varsayılan 0: bekletmeden reddet; Polly, Microsoft ve Netflix concurrency-limits ile
    /// aynı yük atma kuralı). Sıfırdan büyükse çağrı bu süre kadar boşalan kapasiteyi bekler.
    /// </summary>
    public TimeSpan QueueTimeout { get; set; } = TimeSpan.Zero;

    /// <summary>İstek reddedildiğinde çağrılır (Polly: <c>OnRejected</c>). Hatası yutulur, red yine döner.</summary>
    public Func<RateLimiterRejectedArguments, ValueTask>? OnRejected { get; set; }
    public double SmoothingFactor { get; set; } = 0.2; // Hareketli ortalama alfa katsayısı

    /// <summary>
    /// Gecikme dalgalanmasının (jitter) "yavaşlama" sayılmaması için tanınan mutlak taban tolerans (ms).
    /// Gerçek tolerans, bu değer ile <see cref="RttJitterToleranceRatio"/> ile hesaplanan orantılı
    /// payın BÜYÜK olanıdır (AEGIS-122).
    /// <para>
    /// Varsayılan 20ms (AEGIS-143): Windows zamanlayıcı çözünürlüğü ~15.6ms'dir; bunun altındaki mutlak farklar
    /// gerçek yavaşlama değil işletim sistemi gürültüsüdür. Eski varsayılan (5ms) ile, 1ms'lik iki ardışık
    /// "şanslı" örnek minRTT'yi 1ms'ye sabitliyor, ardından kararlı ~14ms'lik çağrılar "14 kat yavaşlama"
    /// sayılıp limit MinConcurrency'ye çöküyor ve orada kalıyordu (paketlenmiş showcase'te gözlemlendi).
    /// Gerçek uzak çağrılarda (RTT 50-500ms) orantılı pay zaten baskındır; bu taban yalnızca 20ms altı
    /// ortamlarda devreye girer — ki orada RTT tabanlı adaptif sınırlama zaten anlamlı sinyal üretmez.
    /// </para>
    /// </summary>
    public double MinRttJitterToleranceMs { get; set; } = 20.0;

    /// <summary>
    /// En düşük gözlemlenen gecikmeye (MinRtt) orantılı jitter tolerans payı (0.0 - 1.0+).
    /// Varsayılan 1.0: ölçülen ortalama gecikme, MinRtt'nin 2 KATINA kadar hâlâ "sağlıklı" kabul edilir
    /// (Netflix Gradient2 varsayılanı olan tolerance=2.0 ile birebir aynı davranış).
    /// Düşük mutlak RTT'li ortamlarda (yerel ağ, birim testleri) sabit milisaniyelik bir eşik,
    /// işletim sistemi zamanlayıcı hassasiyetinden (ör. Windows'ta ~15ms) kaynaklanan normal
    /// dalgalanmayı yanlışlıkla "sistem yavaşladı" olarak yorumlayıp eşzamanlılık limitini
    /// gereksiz yere daraltabiliyordu; orantılı pay bu riski azaltır.
    /// </summary>
    public double RttJitterToleranceRatio { get; set; } = 1.0;

    /// <summary>
    /// Limit ayarlamasına başlanmadan önce yalnızca ölçüm toplanan ısınma örneği sayısı (AEGIS-127).
    /// İlk çağrılar JIT derlemesi, bağlantı kurulumu ve önbellek ısınması yüzünden temsili değildir;
    /// bu örnekler doğrudan karar mekanizmasına girerse hareketli ortalama şişer ve limit haksız yere daralır.
    /// Isınma süresince EMA basit ortalama ile tohumlanır. Varsayılan: 5.
    /// </summary>
    public int WarmupSamples { get; set; } = 5;

    /// <summary>Seçenekleri doğrular; geçersiz değer için <see cref="ArgumentOutOfRangeException"/> fırlatır (AEGIS-130).</summary>
    public void Validate()
    {
        AegisOptionsValidator.AtLeast(MinConcurrency, 1, nameof(AdaptiveConcurrencyOptions));
        AegisOptionsValidator.AtLeast(MaxConcurrency, MinConcurrency, nameof(AdaptiveConcurrencyOptions));
        if (InitialConcurrency < MinConcurrency || InitialConcurrency > MaxConcurrency)
        {
            throw new ArgumentOutOfRangeException(nameof(InitialConcurrency), InitialConcurrency,
                $"AdaptiveConcurrencyOptions.InitialConcurrency, [{MinConcurrency}, {MaxConcurrency}] aralığında olmalıdır.");
        }
        AegisOptionsValidator.InRange(SmoothingFactor, double.Epsilon, 1.0, nameof(AdaptiveConcurrencyOptions));
        AegisOptionsValidator.NonNegative(QueueTimeout, nameof(AdaptiveConcurrencyOptions));
        AegisOptionsValidator.NonNegative(WarmupSamples, nameof(AdaptiveConcurrencyOptions));
        AegisOptionsValidator.InRange(MinRttJitterToleranceMs, 0, double.MaxValue, nameof(AdaptiveConcurrencyOptions));
        AegisOptionsValidator.InRange(RttJitterToleranceRatio, 0, double.MaxValue, nameof(AdaptiveConcurrencyOptions));
    }
}
