namespace Aegis.Resilience.Core.Strategies.RateLimiter;

/// <summary>
/// Segmentli Kayan Zaman Pencereli Hız Sınırlayıcı (Segmented Sliding Window Rate Limiter) seçenekleri.
/// Sabit pencerelerdeki sınır sıçraması (boundary bursting) problemini önler.
/// </summary>
public sealed class SlidingWindowRateLimiterOptions
{
    /// <summary>
    /// Kayan pencere süresi içinde izin verilen maksimum istek/izin sayısı. Varsayılan 100.
    /// </summary>
    public int PermitLimit { get; set; } = 100;

    /// <summary>
    /// Kotanın geçerli olduğu toplam kayan pencere süresi. Varsayılan 1 dakika.
    /// </summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Kayan pencerenin bölüneceği alt dilim (segment) sayısı. Varsayılan 6 (örneğin 60s / 6 = 10s dilimler).
    /// </summary>
    public int SegmentsPerWindow { get; set; } = 6;

    /// <summary>
    /// Kota dolduğunda yeni kota dilimi açılana kadar kuyrukta bekleme zaman aşımı süresi. Varsayılan TimeSpan.Zero (anında ret).
    /// </summary>
    public TimeSpan QueueTimeout { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Canlı ayar güncellemesi için opsiyonel dinamik sağlayıcı.
    /// </summary>
    /// <summary>İstek reddedildiğinde çağrılır (Polly: <c>OnRejected</c>). Hatası yutulur, red yine döner.</summary>
    public Func<RateLimiterRejectedArguments, ValueTask>? OnRejected { get; set; }

    public Func<SlidingWindowRateLimiterOptions>? OptionsProvider { get; set; }

    /// <summary>Seçenekleri doğrular; geçersiz değer için <see cref="ArgumentOutOfRangeException"/> fırlatır (AEGIS-130).</summary>
    public void Validate()
    {
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.AtLeast(PermitLimit, 1, nameof(SlidingWindowRateLimiterOptions));
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.Positive(Window, nameof(SlidingWindowRateLimiterOptions));
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.AtLeast(SegmentsPerWindow, 1, nameof(SlidingWindowRateLimiterOptions));
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.NonNegative(QueueTimeout, nameof(SlidingWindowRateLimiterOptions));
    }
}
