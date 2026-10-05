using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Strategies.Retry;

/// <summary>
/// Yeniden deneme stratejisi yapılandırma seçenekleri.
/// </summary>
public sealed class RetryOptions
{
    /// <summary>İlk denemeden sonraki en fazla yeniden deneme sayısı (≥ 0). Varsayılan 3: toplam 4 deneme.</summary>
    public int MaxRetryAttempts { get; set; } = 3;
    /// <summary>Gecikmenin denemeler arasında nasıl arttığı. Varsayılan üstel (<see cref="DelayBackoffType.Exponential"/>).</summary>
    public DelayBackoffType BackoffType { get; set; } = DelayBackoffType.Exponential;
    /// <summary>Taban gecikme. Varsayılan 2 saniye (Microsoft standart işleyicisiyle aynı).</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(2); // Microsoft standart işleyicisiyle aynı (üstel + jitter ile)
    /// <summary>Hesaplanan ve <c>Retry-After</c> ile istenen gecikmenin üst sınırı. Varsayılan 30 saniye.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Gecikmeye rastgelelik ekler; çok sayıda istemcinin aynı anda yeniden denemesini önler. Varsayılan açık.</summary>
    public bool UseJitter { get; set; } = true;
    public Predicate<Exception>? ShouldHandle { get; set; }

    /// <summary>
    /// Metodun fırlattığı istisna yerine döndürdüğü sonuca göre (örneğin result == null veya IsError == true)
    /// sıfır exception maliyetiyle yeniden deneme tetikleyen opsiyonel koşul.
    /// </summary>
    public Func<object?, bool>? ShouldHandleResult { get; set; }

    /// <summary>
    /// Bağlam, deneme numarası ve sonucu/istisnayı birlikte gören, gerekirse asenkron koşul (Polly: <c>ShouldHandle</c>
    /// + <c>RetryPredicateArguments</c>). Ayarlanırsa <see cref="ShouldHandle"/> ve <see cref="ShouldHandleResult"/> yerine
    /// kullanılır. <see cref="Aegis.Resilience.Core.Abstractions.AegisPredicateBuilder"/> ile akıcı biçimde kurulabilir.
    /// İptal ve açık devre istisnaları her durumda yeniden denenmez.
    /// </summary>
    public Aegis.Resilience.Core.Abstractions.AegisPredicate? ShouldHandleOutcome { get; set; }

    /// <summary>
    /// Jitter ve DecorrelatedJitter için [0, 1) aralığında rastgele sayı üreteci (Polly: <c>Randomizer</c>).
    /// Testlerde sabit değer verilerek gecikmeler deterministik yapılır. Null ise <c>Random.Shared</c>.
    /// </summary>
    public Func<double>? Randomizer { get; set; }

    /// <summary>
    /// Her deneme için dinamik bekleme süresi üreten asenkron delege (Polly 8.7.0 paritesi).
    /// Null döndürürse veya belirtilmemişse standart BackoffType ve Delay hesaplaması kullanılır.
    /// HTTP Retry-After başlığı veya sunucu odaklı gecikmeler için idealdir.
    /// </summary>
    public Func<RetryAttemptContext, ValueTask<TimeSpan?>>? DelayGenerator { get; set; }

    /// <summary>
    /// Sunucunun bildirdiği bekleme (HTTP <c>Retry-After</c>) gecikme olarak kullanılsın mı (Microsoft:
    /// <c>HttpRetryStrategyOptions.ShouldRetryAfterHeader</c>; varsayılan true). Bildirilen süre <see cref="MaxDelay"/> ile
    /// sınırlanır: sunucu saatlerce beklemeyi isterse çağrı kilitlenmez. False ise her zaman <see cref="BackoffType"/> uygulanır.
    /// </summary>
    public bool ShouldRetryAfterHeader { get; set; } = true;

    public Func<RetryAttemptContext, ValueTask>? OnRetry { get; set; }

    /// <summary>
    /// Yeniden deneme bütçesi (isteğe bağlı; gRPC retry throttling). Verilirse hata oranı yükselince yeniden denemeler kendiliğinden
    /// durur ve retry fırtınası trafiği katlayamaz. Aynı <see cref="RetryBudget"/> birden çok boru hattında paylaşılabilir
    /// (ör. aynı bağımlılığa giden tüm istemciler). Null (varsayılan): bütçe yok, Polly ve Microsoft ile aynı.
    /// </summary>
    public RetryBudget? Budget { get; set; }

    /// <summary>
    /// Canlı ve dinamik ayar güncellemesi için opsiyonel sağlayıcı delegasyon.
    /// Belirtildiğinde strateji parametreleri her yürütmede anlık olarak buradan okunur (Zero-Downtime Hot Reload).
    /// </summary>
    public Func<RetryOptions>? OptionsProvider { get; set; }

    /// <summary>Seçenekleri doğrular; geçersiz değer için <see cref="ArgumentOutOfRangeException"/> fırlatır (AEGIS-130).</summary>
    public void Validate()
    {
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.NonNegative(MaxRetryAttempts, nameof(RetryOptions));
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.NonNegative(Delay, nameof(RetryOptions));
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.NonNegative(MaxDelay, nameof(RetryOptions));
    }
}
