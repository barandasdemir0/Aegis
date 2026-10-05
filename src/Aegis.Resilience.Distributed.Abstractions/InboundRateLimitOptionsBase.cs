using Aegis.Resilience.Core.Abstractions;

namespace Aegis.Resilience.Distributed.Abstractions;

/// <summary>
/// Gelen istek hız sınırlama seçeneklerinin barındırıcıdan (ASP.NET Core, Web API 2) bağımsız ortak kısmı: beyaz listeler,
/// başlıklar, depo anahtarı öneki ve kural doğrulaması.
/// </summary>
public abstract class InboundRateLimitOptionsBase
{
    /// <summary>Hiç sınırlanmayan istemci IP'leri ve ağları (ör. <c>"10.0.0.5"</c>, <c>"10.0.0.0/8"</c>, <c>"::1"</c>).</summary>
    public List<string> IpWhitelist { get; set; } = [];

    /// <summary>Hiç sınırlanmayan bölüm anahtarları (ör. iç servislerin istemci kimlikleri).</summary>
    public List<string> ClientWhitelist { get; set; } = [];

    /// <summary>Hiç sınırlanmayan uç nokta desenleri (ör. <c>"GET:/health"</c>).</summary>
    public List<string> EndpointWhitelist { get; set; } = [];

    /// <summary>Yanıtlara <c>RateLimit-Limit</c> / <c>RateLimit-Remaining</c> ve redde <c>Retry-After</c> ekle (varsayılan: evet).</summary>
    public bool EmitRateLimitHeaders { get; set; } = true;

    /// <summary>Depo anahtarlarının öneki (aynı Redis'i paylaşan uygulamaları ayırır).</summary>
    public string KeyPrefix { get; set; } = "inbound";

    /// <summary>Kuralları, desenleri ve ağ tanımlarını doğrular (fail-fast).</summary>
    protected void ValidateRules(IEnumerable<InboundRateLimitRule> rules, string optionsName)
    {
        ArgumentNullException.ThrowIfNull(rules);
        foreach (var rule in rules)
        {
            InboundEndpointPattern.Parse(rule.Endpoint);
            AegisOptionsValidator.AtLeast(rule.Limit, 1, optionsName, $"Rules[{rule.Endpoint}].{nameof(rule.Limit)}");
            AegisOptionsValidator.Positive(rule.Period, optionsName, $"Rules[{rule.Endpoint}].{nameof(rule.Period)}");
        }

        foreach (var pattern in EndpointWhitelist)
        {
            InboundEndpointPattern.Parse(pattern);
        }

        foreach (var network in IpWhitelist)
        {
            InboundIpRule.Parse(network);
        }
    }
}
