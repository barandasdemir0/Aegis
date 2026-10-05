namespace Aegis.Resilience.AspNetCore;

/// <summary>Bir reddin ayrıntısı (özel yanıt yazmak için).</summary>
public sealed class AegisInboundRateLimitRejection
{
    internal AegisInboundRateLimitRejection(AegisInboundRateLimitRule rule, string partitionKey, TimeSpan? retryAfter)
    {
        Rule = rule;
        PartitionKey = partitionKey;
        RetryAfter = retryAfter;
    }

    /// <summary>Aşılan kural.</summary>
    public AegisInboundRateLimitRule Rule { get; }

    /// <summary>İsteğin bölümü (ör. istemci IP'si).</summary>
    public string PartitionKey { get; }

    /// <summary>Yeniden denemek için önerilen bekleme.</summary>
    public TimeSpan? RetryAfter { get; }
}
