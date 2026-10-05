namespace Aegis.Resilience.Core.Exceptions;

/// <summary>
/// Kaos Mühendisliği stratejisi tarafından yapay olarak enjekte edilen hata.
/// </summary>
public class ChaosInjectedException : AegisException
{
    public ChaosInjectedException(string message) : base(message) { }
    public ChaosInjectedException(string message, Exception innerException) : base(message, innerException) { }
}
