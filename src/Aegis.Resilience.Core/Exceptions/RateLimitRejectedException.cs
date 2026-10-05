namespace Aegis.Resilience.Core.Exceptions;

/// <summary>
/// Eşzamanlılık veya hız kotası aşıldığında fırlatılan istisna.
/// </summary>
public class RateLimitRejectedException : AegisException
{
    public RateLimitRejectedException(string message) : base(message) { }

    /// <summary>Yeniden deneme önerisiyle (Polly: <c>RateLimiterRejectedException.RetryAfter</c>).</summary>
    public RateLimitRejectedException(string message, TimeSpan? retryAfter) : base(message)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>Yeniden denemek için önerilen bekleme; sınırlayıcı hesaplayamıyorsa null (ör. eşzamanlılık sınırı).</summary>
    public TimeSpan? RetryAfter { get; }
}
