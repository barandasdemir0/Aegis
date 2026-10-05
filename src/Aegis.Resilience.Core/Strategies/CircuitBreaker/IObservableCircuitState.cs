namespace Aegis.Resilience.Core.Strategies.CircuitBreaker;

/// <summary>
/// Devre durumunu senkron ve yan etkisiz (uzak çağrı yapmayan) şekilde dışarı raporlayan stratejiler için ortak sözleşme.
/// Hem süreç-içi <see cref="CircuitBreakerStrategy"/> hem de dağıtık circuit breaker stratejileri bu arayüzü
/// uygular; böylece Dashboard/HealthCheck gibi tüketiciler somut strateji tipine (veya dağıtık pakete) bağımlı olmadan
/// devre durumunu okuyabilir (AEGIS-121).
/// </summary>
public interface IObservableCircuitState
{
    /// <summary>
    /// En son bilinen devre durumu. Dağıtık implementasyonlarda bu, yerel önbellekten (cache) okunur;
    /// çağrı anında ağ/uzak depo sorgusu tetiklemez.
    /// </summary>
    CircuitState LastKnownState { get; }
}
