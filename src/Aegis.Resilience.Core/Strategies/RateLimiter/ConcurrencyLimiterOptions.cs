namespace Aegis.Resilience.Core.Strategies.RateLimiter;

/// <summary>
/// Eşzamanlılık (Bulkhead) sınırlayıcı seçenekleri. Varsayılan davranış Polly, Microsoft standart işleyicisi ve
/// <c>System.Threading.RateLimiting.ConcurrencyLimiter</c> ile aynıdır: sınır doluysa çağrı BEKLETİLMEDEN reddedilir
/// (yük atma / fail-fast). Bekleme isteniyorsa <see cref="QueueLimit"/> ile sınırlı bir kuyruk açılır.
/// </summary>
public sealed class ConcurrencyLimiterOptions
{
    /// <summary>Aynı anda çalışabilecek en fazla işlem (varsayılan 50).</summary>
    public int MaxConcurrentExecutions { get; set; } = 50;

    /// <summary>
    /// Sınır doluyken bekleyebilecek en fazla çağrı (Polly / .NET <c>QueueLimit</c>; varsayılan 0: bekletmeden reddet).
    /// Kuyruk her zaman sınırlıdır: sınırsız kuyruk aşırı yükte gecikmeyi ve belleği büyütür, bölmeyi (bulkhead) anlamsızlaştırır.
    /// </summary>
    public int QueueLimit { get; set; }

    /// <summary>Kuyruktaki çağrının en fazla bekleme süresi (varsayılan 2 sn; yalnızca <see cref="QueueLimit"/> &gt; 0 iken).</summary>
    public TimeSpan QueueTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>İstek reddedildiğinde çağrılır (Polly: <c>OnRejected</c>). Hatası yutulur, red yine döner.</summary>
    public Func<RateLimiterRejectedArguments, ValueTask>? OnRejected { get; set; }

    public Func<ConcurrencyLimiterOptions>? OptionsProvider { get; set; }

    /// <summary>Seçenekleri doğrular; geçersiz değer için <see cref="ArgumentOutOfRangeException"/> fırlatır (AEGIS-130).</summary>
    public void Validate()
    {
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.AtLeast(MaxConcurrentExecutions, 1, nameof(ConcurrencyLimiterOptions));
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.NonNegative(QueueLimit, nameof(ConcurrencyLimiterOptions));
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.NonNegative(QueueTimeout, nameof(ConcurrencyLimiterOptions));
    }
}
