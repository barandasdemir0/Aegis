namespace Aegis.Resilience.Core.Strategies.CircuitBreaker;

/// <summary>Elle kontrol edilebilen devre (yerel veya dağıtık devre kesici uygular).</summary>
public interface IManuallyControllableCircuit
{
    /// <summary>Devreyi izole eder: çağrılar hedefe gitmeden reddedilir.</summary>
    ValueTask IsolateCircuitAsync(CancellationToken cancellationToken);

    /// <summary>Devreyi kapatır (sağlıklı duruma döndürür).</summary>
    ValueTask CloseCircuitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Kontrol izole durumdayken devre bağlandığında (kurucuda) çağrılır. Yerel devre hemen izole olur; uzak depolu devreler
    /// kurucuda ağ çağrısı yapmamak için izolasyonu ilk çağrıya erteleyebilir.
    /// </summary>
    void IsolateOnRegistration();
}
