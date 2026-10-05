using Aegis.Resilience.Core.Abstractions;

namespace Aegis.Resilience.Core.Strategies.CircuitBreaker;

/// <summary>
/// Süreç-içi (<see cref="CircuitBreakerStrategy"/>) ve dağıtık devre kesicilerin ortak seçenekleri.
/// Eşikler, olaylar ve hata sınıflandırması tek yerde tanımlanır; iki uygulama birbirinden ayrışamaz.
/// </summary>
public abstract class CircuitBreakerOptionsBase
{
    /// <summary>Devrenin açılması için gereken hata oranı eşiği (0, 1]. Varsayılan 0,1 (Polly ve Microsoft ile aynı).</summary>
    public double FailureRatio { get; set; } = 0.1; // Polly ve Microsoft ile aynı varsayılan

    /// <summary>Hata oranının hesaplandığı örnekleme penceresi. Varsayılan 30 saniye.</summary>
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30); // Polly ve Microsoft ile aynı

    /// <summary>Karar verebilmek için pencere içinde gereken minimum toplam çağrı sayısı. Varsayılan 100 (az trafikte tek tük hata devreyi açmaz; gerekirse <c>ConsecutiveFailureThreshold</c>).</summary>
    public int MinimumThroughput { get; set; } = 100; // Polly ve Microsoft ile aynı: az trafikte tek tük hata devreyi açmaz

    /// <summary>Devre açıldıktan sonra HalfOpen (tek deneme) durumuna geçmeden önce beklenecek süre. Varsayılan 5 saniye.</summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(5); // Polly ve Microsoft ile aynı

    /// <summary>
    /// Açılma süresini dinamik hesaplayan üretici (Polly 8.7.0 paritesi). <c>null</c> veya pozitif olmayan dönüş statik
    /// <see cref="BreakDuration"/> kullanımına düşer.
    /// </summary>
    public Func<CircuitBreakerEventContext, TimeSpan?>? BreakDurationGenerator { get; set; }

    /// <summary>Hangi istisnaların hata sayılacağı. Varsayılan: tümü (iptal ve <c>BrokenCircuitException</c> her zaman hariç).</summary>
    public Predicate<Exception>? ShouldHandle { get; set; }

    /// <summary>İstisna fırlatmayan ancak başarısız sayılması gereken sonuçlar (Zero-Exception Result-Based Handling).</summary>
    public Func<object?, bool>? ShouldHandleResult { get; set; }

    /// <summary>
    /// Bağlamı ve sonucu/istisnayı birlikte gören, gerekirse asenkron koşul (Polly: <c>ShouldHandle</c> +
    /// <c>CircuitBreakerPredicateArguments</c>). Ayarlanırsa <see cref="ShouldHandle"/> ve <see cref="ShouldHandleResult"/>
    /// yerine kullanılır. İptal ve açık-devre reddi her durumda hata sayılmaz.
    /// </summary>
    public AegisPredicate? ShouldHandleOutcome { get; set; }

    /// <summary>Elle izole etme / kapatma kontrolü (Polly: <c>ManualControl</c>). Birden çok devre aynı kontrolü paylaşabilir.</summary>
    public CircuitBreakerManualControl? ManualControl { get; set; }

    /// <summary>Devre durumunu dışarıdan okuma sağlayıcısı (Polly: <c>StateProvider</c>). Tek devreye bağlanır.</summary>
    public CircuitBreakerStateProvider? StateProvider { get; set; }

    /// <summary>Devre açıldığında çağrılır. Hatası yutulur, çağıranın sonucunu etkilemez.</summary>
    public Func<CircuitBreakerEventContext, ValueTask>? OnOpened { get; set; }

    /// <summary>Devre kapandığında çağrılır. Hatası yutulur, çağıranın sonucunu etkilemez.</summary>
    public Func<CircuitBreakerEventContext, ValueTask>? OnClosed { get; set; }

    /// <summary>Devre HalfOpen'a geçip deneme isteği başlamadan hemen önce çağrılır. Hatası yutulur, deneme yine yapılır.</summary>
    public Func<CircuitBreakerEventContext, ValueTask>? OnHalfOpened { get; set; }

    /// <summary>Ortak alanları doğrular (AEGIS-130).</summary>
    protected void ValidateCommon(string optionsName)
    {
        AegisOptionsValidator.InRange(FailureRatio, double.Epsilon, 1.0, optionsName, nameof(FailureRatio));
        AegisOptionsValidator.AtLeast(MinimumThroughput, 1, optionsName, nameof(MinimumThroughput));
        AegisOptionsValidator.Positive(SamplingDuration, optionsName, nameof(SamplingDuration));
        AegisOptionsValidator.Positive(BreakDuration, optionsName, nameof(BreakDuration));
    }

    /// <summary>İstisnanın devre için hata sayılıp sayılmayacağı. İptal ve açık-devre reddi asla hata sayılmaz.</summary>
    public bool IsFailure(Exception exception) =>
        !CircuitBreakerRules.IsControlFlow(exception) && (ShouldHandle?.Invoke(exception) ?? true);

    /// <summary>
    /// İstisnanın devre için hata sayılıp sayılmayacağı: retler ve çağıranın kendi iptali sayılmaz; çağıran iptal etmediği
    /// halde gelen iptal (bağımlılık zaman aşımı) sayılır (<see cref="CircuitBreakerRules.IsCircuitNeutral"/>).
    /// </summary>
    public bool IsFailure(Exception exception, CancellationToken callerToken) =>
        !CircuitBreakerRules.IsCircuitNeutral(exception, callerToken) && (ShouldHandle?.Invoke(exception) ?? true);

    /// <summary>Sonucun devre için hata sayılıp sayılmayacağı.</summary>
    /// <remarks>Generic: koşul tanımlı değilse sonuç hiç kutulanmaz (değer tiplerinde çağrı başına 24 B tahsis önlenir).</remarks>
    public bool IsFailureResult<TResult>(TResult result) => ShouldHandleResult != null && ShouldHandleResult(result);

    /// <summary>Pencere sayaçlarına göre hata oranı eşiğinin aşılıp aşılmadığı.</summary>
    public bool IsFailureThresholdExceeded(int successCount, int failureCount)
    {
        var total = successCount + failureCount;
        return total >= MinimumThroughput && (double)failureCount / total >= FailureRatio;
    }

    /// <summary>Bu açılış için uygulanacak süreyi belirler (generator varsa ve pozitif dönerse o, yoksa statik değer).</summary>
    public TimeSpan ResolveBreakDuration(CircuitBreakerEventContext openingEvent)
    {
        if (BreakDurationGenerator == null)
        {
            return BreakDuration;
        }

        try
        {
            var generated = BreakDurationGenerator(openingEvent);
            return generated is { } value && value > TimeSpan.Zero ? value : BreakDuration;
        }
#pragma warning disable CA1031 // Üretici hatası devrenin açılmasını engellememeli; statik süreye düşülür.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Telemetry.AegisTelemetry.CountCallbackError(openingEvent.Context?.PipelineName, nameof(BreakDurationGenerator), ex);
            return BreakDuration;
        }
    }
}
