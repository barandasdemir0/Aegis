using System.Diagnostics;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Distributed.Abstractions;

/// <summary>
/// Dağıtık Circuit Breaker seçenekleri. Eşikler, olaylar ve hata sınıflandırması süreç-içi devre kesiciyle
/// ortaktır (<see cref="CircuitBreakerOptionsBase"/>).
/// <para>
/// Fark: pencere sayaçları depoda tutulur ve <see cref="CircuitBreakerOptionsBase.SamplingDuration"/> boyunca sabit
/// (tumbling) penceredir; yavaş çağrı oranı (Slow Call Rate) yalnızca süreç-içi devre kesicide vardır.
/// </para>
/// </summary>
public sealed class DistributedCircuitBreakerOptions : CircuitBreakerOptionsBase
{
    /// <summary>
    /// Tüm pod'ların aynı devreyi paylaşabilmesi için kullanılan mantıksal devre anahtarı.
    /// Belirtilmezse boru hattı adı kullanılır.
    /// </summary>
    public string? CircuitKey { get; set; }

    /// <summary>
    /// Her çağrıda duruma ait uzak sorguyu tekrarlamamak için yerel önbellek süresi.
    /// Düşük değerler tutarlılığı, yüksek değerler gecikmeyi iyileştirir. Varsayılan 250 ms.
    /// </summary>
    public TimeSpan StateCacheDuration { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Canlı ayar güncellemesi için opsiyonel dinamik sağlayıcı.
    /// </summary>
    public Func<DistributedCircuitBreakerOptions>? OptionsProvider { get; set; }

    /// <summary>Seçenekleri doğrular; geçersiz değer için <see cref="ArgumentOutOfRangeException"/> fırlatır (AEGIS-130).</summary>
    public void Validate()
    {
        ValidateCommon(nameof(DistributedCircuitBreakerOptions));
        AegisOptionsValidator.NonNegative(StateCacheDuration, nameof(DistributedCircuitBreakerOptions));
    }
}
