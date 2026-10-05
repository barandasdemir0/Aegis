using System.Diagnostics;
using System.Collections.Concurrent;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Strategies.Fallback;

/// <summary>
/// Akıllı Bayat Önbellek (Stale-While-Revalidate) seçenekleri.
/// </summary>
public sealed class StaleFallbackOptions
{
    public const string IsStaleDataKey = "Aegis_IsStaleData";

    /// <summary>
    /// Önbellekte tutulan bayat verinin kabul edilebilir maksimum yaşı. Varsayılan 24 saat.
    /// </summary>
    public TimeSpan MaxStaleAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Verinin taze kabul edildiği süre (RFC 5861 Stale-While-Revalidate).
    /// Belirtildiğinde, veri bu süreyi aştığında kullanıcıya anında bayat veri sunulur ve
    /// arka planda asenkron olarak taze veri yenileme görevi (Background Refresh) başlatılır.
    /// </summary>
    public TimeSpan? FreshnessDuration { get; set; }

    /// <summary>
    /// Önbellekte saklanacak maksimum girdi sayısı (bellek sızıntısı ve cardinality patlaması koruması).
    /// </summary>
    public int MaxCacheEntries { get; set; } = 10_000;

    /// <summary>
    /// Önbellek anahtarı üretici. Belirtilmezse boru hattı adı kullanılır.
    /// </summary>
    public Func<AegisContext, string>? KeyGenerator { get; set; }

    public Predicate<Exception>? ShouldHandle { get; set; }
}
