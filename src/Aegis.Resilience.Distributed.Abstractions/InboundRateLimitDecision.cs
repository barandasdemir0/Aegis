namespace Aegis.Resilience.Distributed.Abstractions;

/// <summary>Gelen istek hız sınırı kararı (<see cref="InboundRateLimitEngine{TRule}.AcquireAsync"/>).</summary>
/// <typeparam name="TRule">Barındırıcının kural türü.</typeparam>
public readonly struct InboundRateLimitDecision<TRule>
    where TRule : InboundRateLimitRule
{
    /// <summary>Kararı oluşturur.</summary>
    public InboundRateLimitDecision(bool isAllowed, TRule? rule, int remaining, TimeSpan? retryAfter)
    {
        IsAllowed = isAllowed;
        Rule = rule;
        Remaining = remaining;
        RetryAfter = retryAfter;
    }

    /// <summary>İstek geçebilir mi.</summary>
    public bool IsAllowed { get; }

    /// <summary>Reddedildiyse aşılan kural; geçtiyse en az kotası kalan kural (başlıklar için); hiçbir kural eşleşmediyse null.</summary>
    public TRule? Rule { get; }

    /// <summary><see cref="Rule"/>'da kalan istek sayısı.</summary>
    public int Remaining { get; }

    /// <summary>Reddedildiyse yeniden denemek için önerilen bekleme.</summary>
    public TimeSpan? RetryAfter { get; }
}
