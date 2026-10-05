using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Strategies.RateLimiter;

namespace Aegis.Resilience.Distributed.Abstractions;

/// <summary>
/// Dağıtık hız sınırlayıcı seçenekleri (AspNetCoreRateLimit.Redis / RedisRateLimiting eşdeğeri, Aegis boru hattında).
/// Örnek: ücretli bir dış API'ye tüm pod'lardan toplam saniyede 50 çağrı.
/// </summary>
public sealed class DistributedRateLimiterOptions
{
    /// <summary>Tüm pod'ların paylaştığı mantıksal sınırlayıcı adı. Belirtilmezse boru hattı adı.</summary>
    public string? LimiterKey { get; set; }

    /// <summary>
    /// İsteğe bağlı bölümleme (ör. kiracı, kullanıcı, IP): her bölüm kendi kotasını alır. Null dönerse ortak kota.
    /// </summary>
    public Func<AegisContext, string?>? PartitionKeySelector { get; set; }

    /// <summary>Algoritma (varsayılan: <see cref="DistributedRateLimitAlgorithm.TokenBucket"/>).</summary>
    public DistributedRateLimitAlgorithm Algorithm { get; set; } = DistributedRateLimitAlgorithm.TokenBucket;

    /// <summary>Pencere başına izin ya da kova kapasitesi (varsayılan 100).</summary>
    public int PermitLimit { get; set; } = 100;

    /// <summary>Pencere uzunluğu ya da kova dolum periyodu (varsayılan 1 sn).</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Kovada periyot başına dolan jeton. Null ise <see cref="PermitLimit"/> (periyotta tam dolum).</summary>
    public int? TokensPerPeriod { get; set; }

    /// <summary>Reddedildiğinde çağrılır.</summary>
    public Func<RateLimiterRejectedArguments, ValueTask>? OnRejected { get; set; }

    /// <summary>Canlı ayar güncellemesi için opsiyonel dinamik sağlayıcı.</summary>
    public Func<DistributedRateLimiterOptions>? OptionsProvider { get; set; }

    /// <summary>Seçenekleri doğrular; geçersiz değer için <see cref="ArgumentOutOfRangeException"/> fırlatır.</summary>
    public void Validate()
    {
        AegisOptionsValidator.AtLeast(PermitLimit, 1, nameof(DistributedRateLimiterOptions));
        AegisOptionsValidator.Positive(Window, nameof(DistributedRateLimiterOptions));
        if (TokensPerPeriod is { } tokens)
        {
            AegisOptionsValidator.AtLeast(tokens, 1, nameof(DistributedRateLimiterOptions), nameof(TokensPerPeriod));
        }
    }

    internal DistributedRateLimitRule ToRule() => new(Algorithm, PermitLimit, Window, TokensPerPeriod ?? PermitLimit);
}
