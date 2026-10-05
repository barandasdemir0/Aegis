namespace Aegis.Resilience.WebApi;

/// <summary>Bir reddin ayrıntısı (özel yanıt yazmak için).</summary>
public sealed class AegisWebApiRateLimitRejection
{
    internal AegisWebApiRateLimitRejection(AegisWebApiRateLimitRule rule, string partitionKey, TimeSpan? retryAfter)
    {
        Rule = rule;
        PartitionKey = partitionKey;
        RetryAfter = retryAfter;
    }

    /// <summary>Aşılan kural.</summary>
    public AegisWebApiRateLimitRule Rule { get; }

    /// <summary>İsteğin bölümü (ör. istemci IP'si).</summary>
    public string PartitionKey { get; }

    /// <summary>Yeniden denemek için önerilen bekleme.</summary>
    public TimeSpan? RetryAfter { get; }
}
