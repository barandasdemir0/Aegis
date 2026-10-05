namespace Aegis.Resilience.Core.Exceptions;

/// <summary>
/// Devre elle izole edilmişken fırlatılır (Polly: <c>IsolatedCircuitException</c>). <see cref="BrokenCircuitException"/>'dan
/// türer: mevcut <c>catch (BrokenCircuitException)</c> blokları aynen çalışır; ayrıca izolasyon ayırt edilebilir.
/// </summary>
public class IsolatedCircuitException : BrokenCircuitException
{
    public IsolatedCircuitException(string message) : base(message) { }
}
