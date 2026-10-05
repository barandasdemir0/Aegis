namespace Aegis.Resilience.Core.Strategies.CircuitBreaker;

/// <summary>
/// Devre kesicileri elle izole etmek veya kapatmak için kontrol nesnesi (Polly: <c>CircuitBreakerManualControl</c>).
/// Boru hattı kurulmadan ÖNCE oluşturulup seçeneklere verilir; aynı nesne birden çok devreye bağlanabilir ve hepsini
/// birlikte yönetir (ör. bakım modu anahtarı). Kontrol izole durumdayken bağlanan yeni devre de izole başlar.
/// </summary>
public sealed class CircuitBreakerManualControl
{
    private readonly object _lock = new();
    private readonly List<IManuallyControllableCircuit> _circuits = [];
    private bool _isolated;

    /// <param name="isIsolated">true ise bağlanan devreler izole başlar.</param>
    public CircuitBreakerManualControl(bool isIsolated = false) => _isolated = isIsolated;

    /// <summary>Kontrolün son komutu izolasyon muydu.</summary>
    public bool IsIsolated
    {
        get
        {
            lock (_lock)
            {
                return _isolated;
            }
        }
    }

    /// <summary>Bağlı devre sayısı.</summary>
    public int CircuitCount
    {
        get
        {
            lock (_lock)
            {
                return _circuits.Count;
            }
        }
    }

    /// <summary>Bağlı tüm devreleri izole eder.</summary>
    public async Task IsolateAsync(CancellationToken cancellationToken = default)
    {
        IManuallyControllableCircuit[] circuits;
        lock (_lock)
        {
            _isolated = true;
            circuits = [.. _circuits];
        }

        foreach (var circuit in circuits)
        {
            await circuit.IsolateCircuitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Bağlı tüm devreleri kapatır.</summary>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        IManuallyControllableCircuit[] circuits;
        lock (_lock)
        {
            _isolated = false;
            circuits = [.. _circuits];
        }

        foreach (var circuit in circuits)
        {
            await circuit.CloseCircuitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Devreyi bağlar; kontrol izole durumdaysa devre hemen izole edilir. Devre kesici kurucusu çağırır.</summary>
    public void Register(IManuallyControllableCircuit circuit)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        bool isolate;
        lock (_lock)
        {
            _circuits.Add(circuit);
            isolate = _isolated;
        }

        if (isolate)
        {
            circuit.IsolateOnRegistration();
        }
    }
}
