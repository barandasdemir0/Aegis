namespace Aegis.Resilience.Core.Strategies.Cache;

/// <summary>Bir girdinin süre kuralı.</summary>
public readonly struct AegisCacheEntryOptions
{
    /// <param name="ttl">Yaşam süresi.</param>
    /// <param name="slidingExpiration">True ise her okumada süre yeniden başlar (Polly: <c>SlidingTtl</c>).</param>
    public AegisCacheEntryOptions(TimeSpan ttl, bool slidingExpiration)
    {
        Ttl = ttl;
        SlidingExpiration = slidingExpiration;
    }

    /// <summary>Yaşam süresi.</summary>
    public TimeSpan Ttl { get; }

    /// <summary>True ise her okumada süre yeniden başlar.</summary>
    public bool SlidingExpiration { get; }
}
