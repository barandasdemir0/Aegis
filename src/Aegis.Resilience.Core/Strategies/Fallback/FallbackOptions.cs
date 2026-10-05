using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Strategies.Fallback;

/// <summary>
/// Hata anında zarif gerileme (Graceful degradation) sağlayan Fallback seçenekleri.
/// </summary>
public sealed class FallbackOptions
{
    /// <summary>Hangi istisnaların yedeğe düşeceği. Varsayılan: tümü (iptal her zaman hariç).</summary>
    public Predicate<Exception>? ShouldHandle { get; set; }

    /// <summary>
    /// İstisna fırlatmayan ama yedeğe düşmesi gereken sonuçlar (ör. HTTP 503 yanıtı). Varsayılan: hiçbiri.
    /// Sonuç tabanlı yedek için <see cref="FallbackAction"/> gerekir.
    /// </summary>
    public Func<object?, bool>? ShouldHandleResult { get; set; }

    /// <summary>
    /// Bağlamı ve sonucu/istisnayı birlikte gören, gerekirse asenkron koşul (Polly: <c>ShouldHandle</c> +
    /// <c>FallbackPredicateArguments</c>). Ayarlanırsa <see cref="ShouldHandle"/> ve <see cref="ShouldHandleResult"/>
    /// yerine kullanılır. İptal her durumda yedeğe düşmez.
    /// </summary>
    public AegisPredicate? ShouldHandleOutcome { get; set; }

    /// <summary>İstisna tabanlı yedek üretici (eski imza; aynen desteklenir).</summary>
    public Func<AegisContext, Exception, ValueTask<object?>>? FallbackHandler { get; set; }

    /// <summary>
    /// Hem istisna hem sonuç tabanlı yedek üretici (Polly: <c>FallbackAction</c>). Ayarlanırsa <see cref="FallbackHandler"/>
    /// yerine kullanılır.
    /// </summary>
    public Func<FallbackArguments, ValueTask<object?>>? FallbackAction { get; set; }

    /// <summary>Yedeğe düşmeden hemen önce çağrılır (Polly: <c>OnFallback</c>). Hatası yutulur, yedek yine döner.</summary>
    public Func<FallbackArguments, ValueTask>? OnFallback { get; set; }
}
