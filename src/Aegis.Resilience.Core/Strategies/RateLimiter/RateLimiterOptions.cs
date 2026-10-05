namespace Aegis.Resilience.Core.Strategies.RateLimiter;

/// <summary>
/// Belirli bir zaman penceresinde izin verilen maksimum istek sayısını denetleyen zaman tabanlı Hız Sınırlayıcı (Rate Limiter) seçenekleri.
/// Token Bucket algoritmasını temel alır.
/// </summary>
public sealed class RateLimiterOptions
{
    /// <summary>
    /// Belirlenen zaman penceresinde kabul edilecek maksimum izin/istek sayısı. Varsayılan 100.
    /// </summary>
    public int PermitLimit { get; set; } = 100;

    /// <summary>
    /// Kotanın geçerli olduğu zaman penceresi. Varsayılan 1 dakika.
    /// </summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Kota dolduğunda yeni token için kuyrukta bekleme zaman aşımı süresi. Varsayılan TimeSpan.Zero (kuyruğa alma, anında reddet).
    /// </summary>
    public TimeSpan QueueTimeout { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Canlı ayar güncellemesi için opsiyonel dinamik sağlayıcı (Zero-Downtime Hot Reload).
    /// </summary>
    /// <summary>İstek reddedildiğinde çağrılır (Polly: <c>OnRejected</c>). Hatası yutulur, red yine döner.</summary>
    public Func<RateLimiterRejectedArguments, ValueTask>? OnRejected { get; set; }

    public Func<RateLimiterOptions>? OptionsProvider { get; set; }

    /// <summary>Seçenekleri doğrular; geçersiz değer için <see cref="ArgumentOutOfRangeException"/> fırlatır (AEGIS-130).</summary>
    public void Validate()
    {
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.AtLeast(PermitLimit, 1, nameof(RateLimiterOptions));
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.Positive(Window, nameof(RateLimiterOptions));
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.NonNegative(QueueTimeout, nameof(RateLimiterOptions));
    }
}
