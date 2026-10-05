using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Strategies.Hedging;

/// <summary>
/// Yavaşlayan isteklere karşı paralel alternatif istek fırlatan Hedging seçenekleri.
/// </summary>
public sealed class HedgingOptions
{
    /// <summary>Birincil bu sürede bitmezse yedek deneme başlar. Varsayılan 2 saniye. <see cref="TimeSpan.Zero"/>: hepsi aynı anda; <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>: yalnızca öncekiler başarısız olunca.</summary>
    public TimeSpan HedgingDelay { get; set; } = TimeSpan.FromSeconds(2); // Polly ve Microsoft ile aynı
    /// <summary>
    /// Birincile EK olarak başlatılabilecek yedek deneme sayısı (Polly <c>MaxHedgedAttempts</c> ile aynı anlam; varsayılan 1:
    /// birincil + 1 yedek = toplam 2 deneme). 2.0.0 öncesinde bu değer birincil dahil toplamdı.
    /// </summary>
    public int MaxHedgedAttempts { get; set; } = 1;

    /// <summary>
    /// Yeniden deneme bütçesi (isteğe bağlı; gRPC A6). Verilirse ek hedging denemesi ancak bütçe izin verirse başlar ve her deneme
    /// sonucu bütçeye yazılır; bağımlılık bozulunca yedek denemeler trafiği katlamaz. Retry ile aynı örnek paylaşılabilir.
    /// </summary>
    public Retry.RetryBudget? Budget { get; set; }

    /// <summary>
    /// Deneme başına yedekleme gecikmesi (Polly: <c>DelayGenerator</c>); ör. ilk yedek 100 ms, sonrakiler 300 ms.
    /// Ayarlanırsa <see cref="HedgingDelay"/> yerine kullanılır. <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
    /// dönmesi o tur için "yalnızca önceki başarısız olunca" anlamına gelir.
    /// </summary>
    public Func<HedgingAttemptArguments, ValueTask<TimeSpan>>? DelayGenerator { get; set; }

    /// <summary>Yeni bir yedek deneme başlatılmadan hemen önce çağrılır (Polly: <c>OnHedging</c>). Hatası yutulur.</summary>
    public Func<HedgingAttemptArguments, ValueTask>? OnHedging { get; set; }

    /// <summary>
    /// Yedek deneme için farklı bir işlem üretir (Polly: <c>ActionGenerator</c>); ör. ikinci denemede farklı bölgeye/uç noktaya
    /// gitmek. Null dönerse o denemede asıl geri çağrı çalışır. Dönen değer boru hattının sonuç tipiyle uyumlu olmalıdır.
    /// Birincil deneme (0) her zaman asıl geri çağrıdır.
    /// </summary>
    public Func<HedgingAttemptArguments, Func<AegisContext, ValueTask<object?>>?>? ActionGenerator { get; set; }

    /// <summary>
    /// İstisna fırlatmayan ama başarısız sayılıp yedeklemeyi sürdürmesi gereken sonuçlar (ör. HTTP 503). Varsayılan: hiçbiri.
    /// </summary>
    public Func<object?, bool>? ShouldHandleResult { get; set; }

    /// <summary>
    /// Bağlam ve deneme numarası gören, gerekirse async koşul (Polly: <c>ShouldHandle</c>). Sonuçlar için kullanılır:
    /// true dönen sonuç başarısız sayılır ve sıradaki deneme kazanabilir. İstisnalar için bkz. <see cref="ShouldHandle"/>.
    /// Ayarlanırsa <see cref="ShouldHandleResult"/> yerine kullanılır.
    /// </summary>
    public Abstractions.AegisPredicate? ShouldHandleOutcome { get; set; }

    /// <summary>
    /// Hangi istisnaların yedeklemeyi sürdüreceği (Polly: <c>ShouldHandle</c>'ın istisna kısmı). False dönen istisna nihai
    /// sonuç kabul edilir: yedekleme durur ve istisna çağırana iletilir (ör. <c>ArgumentException</c> başka denemede de
    /// düzelmez). Null ise (varsayılan) tüm istisnalar sonraki denemeye geçer.
    /// </summary>
    public Predicate<Exception>? ShouldHandle { get; set; }

    /// <summary>Yedek denemelerin alt bağlamında deneme numarasını taşıyan anahtar (birincil denemede yoktur, 0 kabul edin).</summary>
    public static readonly AegisPropertyKey<int> AttemptNumberKey = new("Aegis.HedgingAttempt");

    /// <summary>
    /// Çağrı bazında toplam deneme üst sınırı (bağlama yazılır; <see cref="MaxHedgedAttempts"/>'tan küçükse o geçerli olur).
    /// Ör. yönlendirilecek uç nokta grubu sayısı kadar deneme.
    /// </summary>
    public static readonly AegisPropertyKey<int> MaxAttemptsKey = new("Aegis.HedgingMaxAttempts");

    public Func<HedgingOptions>? OptionsProvider { get; set; }

    /// <summary>Seçenekleri doğrular; geçersiz değer için <see cref="ArgumentOutOfRangeException"/> fırlatır (AEGIS-130).</summary>
    public void Validate()
    {
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.AtLeast(MaxHedgedAttempts, 1, nameof(HedgingOptions));
        // System.Threading.Timeout.InfiniteTimeSpan geçerlidir: "ardışık yedekleme" modu (yeni deneme yalnızca öncekiler başarısız olunca)
        if (HedgingDelay != System.Threading.Timeout.InfiniteTimeSpan)
        {
            Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.NonNegative(HedgingDelay, nameof(HedgingOptions));
        }
    }
}
