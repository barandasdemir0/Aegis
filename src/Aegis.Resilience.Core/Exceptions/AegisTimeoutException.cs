namespace Aegis.Resilience.Core.Exceptions;

/// <summary>
/// İşlem belirlenen zaman aşımı süresini aştığında fırlatılan istisna.
/// </summary>
public class AegisTimeoutException : AegisException
{
    public TimeSpan Timeout { get; }

    public AegisTimeoutException(string message, TimeSpan timeout) : base(message)
    {
        Timeout = timeout;
    }
}
