namespace Aegis.Resilience.Core.Strategies.CircuitBreaker;

/// <summary>
/// Devre durumunu dışarıdan okumak için sağlayıcı (Polly: <c>CircuitBreakerStateProvider</c>). Kurulumdan önce oluşturulup
/// seçeneklere verilir; yalnızca TEK bir devreye bağlanabilir. Okuma yan etkisizdir (geçiş yapmaz).
/// </summary>
public sealed class CircuitBreakerStateProvider
{
    private IObservableCircuitState? _circuit;

    /// <summary>Bağlı devrenin durumu; henüz bağlanmadıysa <see cref="CircuitState.Closed"/>.</summary>
    public CircuitState CircuitState => Volatile.Read(ref _circuit)?.LastKnownState ?? CircuitState.Closed;

    /// <summary>Bir devreye bağlandı mı.</summary>
    public bool IsInitialized => Volatile.Read(ref _circuit) is not null;

    /// <summary>Devreyi bağlar. İkinci bağlama <see cref="InvalidOperationException"/> fırlatır (Polly ile aynı).</summary>
    public void Initialize(IObservableCircuitState circuit)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        if (Interlocked.CompareExchange(ref _circuit, circuit, null) is not null)
        {
            throw new InvalidOperationException(
                "Bu CircuitBreakerStateProvider zaten bir devreye bağlı. Her devre kesici için ayrı bir sağlayıcı oluşturun.");
        }
    }
}
