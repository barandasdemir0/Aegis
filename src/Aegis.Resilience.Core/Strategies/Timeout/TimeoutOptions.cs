using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Strategies.Timeout;

/// <summary>
/// Zaman aşımı stratejisi seçenekleri.
/// </summary>
public sealed class TimeoutOptions
{
    /// <summary>Zaman aşımı süresi (> 0 ya da <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>). Varsayılan 30 saniye.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30); // Polly ile aynı

    /// <summary>
    /// Çağrı bağlamına göre dinamik zaman aşımı süresi üreten delege (Polly 8.7.0 paritesi).
    /// Dağıtık istek son kullanma tarihi (Request Deadline Budget) için idealdir.
    /// Belirtildiğinde ve non-null döndüğünde sabit Timeout değeri yerine kullanılır.
    /// </summary>
    public Func<AegisContext, TimeSpan?>? TimeoutGenerator { get; set; }

    /// <summary>İyimser (token ile iptal, önerilen) ya da kötümser (token'a saygısız kodu terk eder). Varsayılan iyimser.</summary>
    public TimeoutStrategyMode Mode { get; set; } = TimeoutStrategyMode.Optimistic;
    public Func<AegisContext, TimeSpan, ValueTask>? OnTimeout { get; set; }

    /// <summary>
    /// Canlı ayar güncellemesi için opsiyonel dinamik sağlayıcı.
    /// </summary>
    public Func<TimeoutOptions>? OptionsProvider { get; set; }

    /// <summary>Seçenekleri doğrular; geçersiz değer için <see cref="ArgumentOutOfRangeException"/> fırlatır (AEGIS-130).</summary>
    public void Validate()
    {
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.PositiveOrInfinite(Timeout, nameof(TimeoutOptions));
    }
}
