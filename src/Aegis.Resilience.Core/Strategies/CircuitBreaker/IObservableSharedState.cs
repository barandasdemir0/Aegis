namespace Aegis.Resilience.Core.Strategies.CircuitBreaker;

/// <summary>
/// Durumunu pod'lar arasında paylaşılan bir depoda (ör. Redis) tutan stratejiler için bağlantı gözlemi (AEGIS-160).
/// Depoya ulaşılamadığında strateji çökmez, pod-yerel moda düşer (fail-open); ancak bu durumda pod'lar artık durum
/// paylaşmaz. Health check bu sinyali raporlar ki yanlış yapılandırma veya kesinti sessiz kalmasın.
/// </summary>
public interface IObservableSharedState
{
    /// <summary>Paylaşılan depoya şu an ulaşılabiliyor mu. Yan etkisizdir; uzak çağrı yapmaz.</summary>
    bool IsSharedStateAvailable { get; }
}
