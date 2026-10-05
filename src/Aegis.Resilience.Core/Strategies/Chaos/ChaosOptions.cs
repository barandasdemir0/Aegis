using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;

namespace Aegis.Resilience.Core.Strategies.Chaos;

/// <summary>
/// Kaos Mühendisliği (Chaos Engineering) ve Arıza Enjeksiyonu seçenekleri.
/// Test ve Staging ortamlarında sistemin dayanıklılığını doğrulamak için yapay gecikme ve hata üretir.
/// </summary>
public sealed class ChaosOptions
{
    /// <summary>
    /// Kaos enjeksiyonunun etkin olup olmadığı. Üretim ortamında varsayılan olarak kapalıdır.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Hata/gecikme enjekte edilme olasılığı (0.0 ile 1.0 arasında). Örneğin 0.25 -> %25 ihtimal.
    /// </summary>
    public double InjectionRate { get; set; } = 0.2;

    /// <summary>[0, 1) aralığında rastgele sayı üreteci (Polly: <c>Randomizer</c>); testlerde deterministik enjeksiyon için. Null ise <c>Random.Shared</c>.</summary>
    public Func<double>? Randomizer { get; set; }

    /// <summary>
    /// Enjekte edilecek yapay gecikme süresi.
    /// </summary>
    public TimeSpan Latency { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Çağrı başına gecikme (Polly: <c>LatencyGenerator</c>); ör. bağlama göre değişen ya da rastgele gecikme. Ayarlanırsa
    /// <see cref="Latency"/> yerine kullanılır; sıfır veya negatif dönerse o çağrıda gecikme enjekte edilmez.
    /// </summary>
    public Func<AegisContext, ValueTask<TimeSpan>>? LatencyGenerator { get; set; }

    /// <summary>
    /// Enjekte edilecek istisna üretici. Varsayılan <see cref="ChaosInjectedException"/> üretir. Üretici null dönerse o
    /// çağrıda hata enjekte edilmez (Polly 8.8 ile aynı: koşullu hata için).
    /// </summary>
    public Func<Exception>? FaultGenerator { get; set; } = () => new ChaosInjectedException("Aegis Kaos Stratejisi tarafından yapay arıza enjekte edildi!");

    /// <summary>
    /// İstisna fırlatmadan doğrudan sahte veya bozulmuş (degraded) sonuç döndüren kaos enjeksiyon fonksiyonu (Simmy Chaos Result).
    /// Belirtildiğinde FaultGenerator yerine bu sonuç döndürülür.
    /// </summary>
    public Func<AegisContext, object?>? ResultGenerator { get; set; }

    /// <summary>
    /// Ağırlıklı istisna/sonuç üretici (Polly: <c>OutcomeGenerator</c>). Ayarlanırsa <see cref="ResultGenerator"/> ve
    /// <see cref="FaultGenerator"/> yerine kullanılır.
    /// </summary>
    public ChaosOutcomeGenerator? OutcomeGenerator { get; set; }

    /// <summary>
    /// Kaos anında çalıştırılacak özel davranış / yan etki fonksiyonu (Simmy Chaos Behavior).
    /// Disk doluluğu, bellek tüketimi simülasyonu veya özel callback'ler için kullanılır.
    /// </summary>
    public Func<AegisContext, ValueTask>? BehaviorGenerator { get; set; }

    /// <summary>
    /// Çağrı bazında etkinlik (Polly: <c>EnabledGenerator</c>); ör. yalnızca belirli kiracı veya başlıkta kaos.
    /// Ayarlanırsa <see cref="Enabled"/> yerine kullanılır.
    /// </summary>
    public Func<AegisContext, ValueTask<bool>>? EnabledGenerator { get; set; }

    /// <summary>Çağrı bazında enjeksiyon oranı (Polly: <c>InjectionRateGenerator</c>). Ayarlanırsa <see cref="InjectionRate"/> yerine kullanılır.</summary>
    public Func<AegisContext, ValueTask<double>>? InjectionRateGenerator { get; set; }

    /// <summary>Her enjeksiyonda çağrılır (Polly: <c>OnFaultInjected</c>, <c>OnLatencyInjected</c> ...). Hatası yutulur.</summary>
    public Func<ChaosInjectionArguments, ValueTask>? OnInjected { get; set; }

    public Func<ChaosOptions>? OptionsProvider { get; set; }

    /// <summary>Seçenekleri doğrular; geçersiz değer için <see cref="ArgumentOutOfRangeException"/> fırlatır (AEGIS-130).</summary>
    public void Validate()
    {
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.InRange(InjectionRate, 0.0, 1.0, nameof(ChaosOptions));
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.NonNegative(Latency, nameof(ChaosOptions));
    }
}
