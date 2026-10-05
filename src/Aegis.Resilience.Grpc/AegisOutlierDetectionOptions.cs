namespace Aegis.Resilience.Grpc;

/// <summary>
/// gRPC sunucu (uç nokta) ayıklama seçenekleri. Envoy <c>outlier_detection</c> ile aynı model ve varsayılanlar: art arda
/// <see cref="ConsecutiveFailures"/> sunucu hatası alan uç nokta <see cref="BaseEjectionTime"/> × ayıklanma sayısı kadar havuzdan
/// çıkarılır (en fazla <see cref="MaxEjectionTime"/>); havuzun en fazla <see cref="MaxEjectionPercent"/>'i ayıklanır.
/// </summary>
public sealed class AegisOutlierDetectionOptions
{
    /// <summary>Ayıklama için art arda sunucu hatası sayısı (varsayılan 5; Envoy <c>consecutive_5xx</c>).</summary>
    public int ConsecutiveFailures { get; set; } = 5;

    /// <summary>Taban ayıklama süresi; her yeni ayıklamada bununla çarpılarak büyür (varsayılan 30 sn; Envoy).</summary>
    public TimeSpan BaseEjectionTime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>En uzun ayıklama süresi (varsayılan 300 sn; Envoy).</summary>
    public TimeSpan MaxEjectionTime { get; set; } = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Havuzun en fazla yüzde kaçı ayıklanabilir (varsayılan 10; Envoy). En az bir uç nokta ayıklanabilir, ama hiçbir zaman hepsi:
    /// tüm uç noktalar ayıklanmışsa seçici yine tümünü kullanır (Envoy "panik modu").
    /// </summary>
    public int MaxEjectionPercent { get; set; } = 10;

    /// <summary>Seçenekleri doğrular (fail-fast).</summary>
    public void Validate()
    {
        if (ConsecutiveFailures < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ConsecutiveFailures), ConsecutiveFailures, "En az 1 olmalıdır.");
        }

        if (BaseEjectionTime <= TimeSpan.Zero || MaxEjectionTime < BaseEjectionTime)
        {
            throw new ArgumentOutOfRangeException(nameof(BaseEjectionTime), BaseEjectionTime, "Pozitif ve MaxEjectionTime'dan küçük ya da eşit olmalıdır.");
        }

        if (MaxEjectionPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEjectionPercent), MaxEjectionPercent, "0 ile 100 arasında olmalıdır.");
        }
    }
}
