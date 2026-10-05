using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Strategies.CircuitBreaker;

/// <summary>
/// Devre durum değişikliği olayı (OnOpened / OnClosed / OnHalfOpened, <c>BreakDurationGenerator</c>). Yerel ve dağıtık devre
/// kesici olayları aynı fabrika metotlarıyla kurar: her geçişin içeriği tek yerde tanımlıdır.
/// </summary>
public sealed class CircuitBreakerEventContext
{
    public CircuitState OldState { get; init; }
    public CircuitState NewState { get; init; }
    public TimeSpan BreakDuration { get; init; }
    public AegisContext Context { get; init; } = default!;

    /// <summary>
    /// Devre açılırken örnekleme penceresinin sağlık istatistiği (Polly: <c>BreakDurationGeneratorArguments.HealthInfo</c>).
    /// <see cref="CircuitBreakerOptionsBase.BreakDurationGenerator"/> hata oranına göre süre seçebilir. Diğer olaylarda boştur.
    /// </summary>
    public CircuitHealth Health { get; init; }

    /// <summary>
    /// Art arda başarısız olan HalfOpen deneme sayısı (Polly: <c>BreakDurationGeneratorArguments.HalfOpenAttempts</c>).
    /// Devre kapanınca sıfırlanır. <see cref="CircuitBreakerOptionsBase.BreakDurationGenerator"/> ile açılma süresi
    /// üstel büyütülebilir: <c>e =&gt; TimeSpan.FromSeconds(5 * Math.Pow(2, e.HalfOpenAttempts))</c>.
    /// </summary>
    public int HalfOpenAttempts { get; init; }

    /// <summary>Durum değişikliği elle mi yapıldı (ManualControl / pano) (Polly: <c>IsManual</c>).</summary>
    public bool IsManual { get; init; }

    /// <summary>Geçişi tetikleyen istisna (otomatik geçişlerde; Polly: <c>Outcome.Exception</c>).</summary>
    public Exception? Exception { get; init; }

    /// <summary>Geçişi tetikleyen sonuç (otomatik geçişlerde; Polly: <c>Outcome.Result</c>).</summary>
    public object? Result { get; init; }

    /// <summary>Devre açıldı: sağlık bilgisi, deneme sayısı ve tetikleyen sonuçla.</summary>
    public static CircuitBreakerEventContext Opened<TResult>(
        CircuitState oldState, TimeSpan breakDuration, AegisContext context, CircuitHealth health, int halfOpenAttempts, in Outcome<TResult> outcome) =>
        new()
        {
            OldState = oldState,
            NewState = CircuitState.Open,
            BreakDuration = breakDuration,
            Context = context,
            Health = health,
            HalfOpenAttempts = halfOpenAttempts,
            Exception = outcome.Exception,
            Result = outcome.Exception is null ? outcome.Result : null
        };

    /// <summary>Deneme isteği başarılı oldu, devre kapandı (HalfOpen → Closed).</summary>
    public static CircuitBreakerEventContext Closed<TResult>(TimeSpan breakDuration, AegisContext context, in Outcome<TResult> outcome) =>
        new()
        {
            OldState = CircuitState.HalfOpen,
            NewState = CircuitState.Closed,
            BreakDuration = breakDuration,
            Context = context,
            Exception = outcome.Exception,
            Result = outcome.Exception is null ? outcome.Result : null
        };

    /// <summary>Açık kalma süresi doldu, devre deneme isteğine açıldı (Open → HalfOpen).</summary>
    public static CircuitBreakerEventContext HalfOpened(TimeSpan breakDuration, AegisContext context) =>
        new() { OldState = CircuitState.Open, NewState = CircuitState.HalfOpen, BreakDuration = breakDuration, Context = context };

    /// <summary>Elle yapılan geçiş (ManualControl / pano).</summary>
    public static CircuitBreakerEventContext Manual(CircuitState oldState, CircuitState newState, TimeSpan breakDuration, AegisContext context) =>
        new() { OldState = oldState, NewState = newState, BreakDuration = breakDuration, Context = context, IsManual = true };
}
