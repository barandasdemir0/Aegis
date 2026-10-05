namespace Aegis.Resilience.Core.Exceptions;

/// <summary>
/// Circuit Breaker devresi açıkken (Open) veya izole edilmişken fırlatılan istisna.
/// </summary>
public class BrokenCircuitException : AegisException
{
    public BrokenCircuitException(string message) : base(message) { }
    public BrokenCircuitException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Devrenin yeniden deneme isteğine açılmasına kalan süreyle (Polly: <c>BrokenCircuitException.RetryAfter</c>).</summary>
    public BrokenCircuitException(string message, TimeSpan? retryAfter) : base(message)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// Devrenin HalfOpen olup deneme isteği kabul etmesine kalan tahmini süre. Bilinmiyorsa null: devre izole edilmiş,
    /// deneme isteği zaten yürütülüyor ya da kalan süre dağıtık depodan okunmuyor.
    /// </summary>
    public TimeSpan? RetryAfter { get; }
}
