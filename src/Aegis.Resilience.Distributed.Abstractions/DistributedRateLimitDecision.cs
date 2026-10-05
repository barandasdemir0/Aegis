using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Strategies.RateLimiter;

namespace Aegis.Resilience.Distributed.Abstractions;

/// <summary>Bir izin isteğinin sonucu.</summary>
public readonly struct DistributedRateLimitDecision
{
    /// <param name="isAcquired">İzin verildi mi.</param>
    /// <param name="remaining">Kalan izin (tahmini; kayan pencerede ağırlıklı hesap).</param>
    /// <param name="retryAfter">Reddedildiyse yeniden denemek için önerilen bekleme.</param>
    public DistributedRateLimitDecision(bool isAcquired, int remaining, TimeSpan? retryAfter)
    {
        IsAcquired = isAcquired;
        Remaining = remaining;
        RetryAfter = retryAfter;
    }

    public bool IsAcquired { get; }

    public int Remaining { get; }

    public TimeSpan? RetryAfter { get; }
}
