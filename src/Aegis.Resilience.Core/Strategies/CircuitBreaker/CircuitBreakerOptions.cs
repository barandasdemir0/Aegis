using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Strategies.CircuitBreaker;

/// <summary>
/// Süreç-içi Circuit Breaker stratejisi seçenekleri. Ortak eşik ve olaylar <see cref="CircuitBreakerOptionsBase"/>'dedir.
/// </summary>
public sealed class CircuitBreakerOptions : CircuitBreakerOptionsBase
{
    /// <summary>
    /// Bir çağrının 'yavaş çağrı' (Slow Call) sayılması için gereken süre eşiği (Resilience4j paritesi).
    /// Null ise yavaş çağrı denetimi devre dışıdır.
    /// </summary>
    public TimeSpan? SlowCallDurationThreshold { get; set; }

    /// <summary>
    /// Örnekleme penceresi içinde yavaş çağrıların toplam çağrılara oranı bu eşiği aştığında
    /// hata olmasa dahi devre açılır (Resilience4j Slow Call Rate Threshold). Varsayılan 1.0 (devre dışı).
    /// </summary>
    public double SlowCallRateThreshold { get; set; } = 1.0;

    /// <summary>
    /// Art arda bu kadar işlenen hatada devreyi açar (Polly v7: <c>CircuitBreaker(exceptionsAllowedBeforeBreaking, ...)</c>).
    /// Oran kuralına EK bir koşuldur: <see cref="CircuitBreakerOptionsBase.MinimumThroughput"/> ve örnekleme penceresi
    /// beklenmez, araya giren tek başarı sayacı sıfırlar. Düşük trafikli bağımlılıklarda oranın hiç hesaplanamadığı
    /// durumlar için uygundur. Null ise (varsayılan) kapalıdır.
    /// </summary>
    public int? ConsecutiveFailureThreshold { get; set; }

    /// <summary>
    /// Devrenin HalfOpen'dan kapanması için gereken art arda başarılı deneme isteği sayısı (varsayılan 1: Polly ile aynı). Denemeler
    /// yine birer birer geçer; herhangi biri başarısız olursa devre yeniden açılır. Büyük değer, hâlâ dengesiz bir servise tek şanslı
    /// denemeyle tüm trafiğin açılmasını önler (resilience4j: <c>permittedNumberOfCallsInHalfOpenState</c>).
    /// </summary>
    public int HalfOpenSuccessThreshold { get; set; } = 1;

    /// <summary>
    /// Sayı tabanlı pencere: hata oranı son N çağrıdan hesaplanır (resilience4j <c>COUNT_BASED</c>). Null (varsayılan): zaman
    /// tabanlı pencere (<c>SamplingDuration</c>; Polly ve Microsoft ile aynı). Düzensiz ya da düşük trafikte karar zamana bağlı
    /// kalmaz. <c>MinimumThroughput</c>'tan küçük olamaz (aksi halde devre hiç açılamazdı).
    /// </summary>
    public int? SamplingCount { get; set; }

    /// <summary>
    /// Çalışma kipi (varsayılan <see cref="CircuitBreakerMode.Enforce"/>). <see cref="CircuitBreakerMode.Shadow"/> ile devre üretimde
    /// risksiz izlenir; <see cref="OptionsProvider"/> üzerinden canlı değiştirilebilir.
    /// </summary>
    public CircuitBreakerMode Mode { get; set; } = CircuitBreakerMode.Enforce;

    /// <summary>
    /// Canlı ayar güncellemesi için opsiyonel dinamik sağlayıcı.
    /// </summary>
    public Func<CircuitBreakerOptions>? OptionsProvider { get; set; }

    /// <summary>Seçenekleri doğrular; geçersiz değer için <see cref="ArgumentOutOfRangeException"/> fırlatır (AEGIS-130).</summary>
    public void Validate()
    {
        ValidateCommon(nameof(CircuitBreakerOptions));
        AegisOptionsValidator.AtLeast(HalfOpenSuccessThreshold, 1, nameof(CircuitBreakerOptions), nameof(HalfOpenSuccessThreshold));
        if (SamplingCount is { } samplingCount)
        {
            AegisOptionsValidator.AtLeast(samplingCount, MinimumThroughput, nameof(CircuitBreakerOptions), nameof(SamplingCount));
        }
        AegisOptionsValidator.InRange(SlowCallRateThreshold, double.Epsilon, 1.0, nameof(CircuitBreakerOptions));
        if (SlowCallDurationThreshold is { } slow)
        {
            AegisOptionsValidator.Positive(slow, nameof(CircuitBreakerOptions), nameof(SlowCallDurationThreshold));
        }

        if (ConsecutiveFailureThreshold is { } consecutive)
        {
            AegisOptionsValidator.AtLeast(consecutive, 1, nameof(CircuitBreakerOptions), nameof(ConsecutiveFailureThreshold));
        }
    }

    internal bool IsConsecutiveFailureThresholdReached(int consecutiveFailures) =>
        ConsecutiveFailureThreshold is { } threshold && consecutiveFailures >= threshold;

    /// <summary>Yavaş çağrı oranı eşiğinin aşılıp aşılmadığı (eşik 1.0 ise özellik kapalıdır).</summary>
    internal bool IsSlowCallThresholdExceeded(int total, int slowCount) =>
        SlowCallRateThreshold < 1.0 && total >= MinimumThroughput && (double)slowCount / total >= SlowCallRateThreshold;

    internal bool IsSlow(TimeSpan elapsed) => SlowCallDurationThreshold is { } threshold && elapsed >= threshold;
}
