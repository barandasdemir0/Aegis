using System.Net;

namespace Aegis.Resilience.Distributed.Abstractions;

/// <summary>
/// Gelen istek hız sınırlamanın barındırıcıdan bağımsız çekirdeği (ASP.NET Core ara katmanı ve Web API 2 işleyicisi
/// paylaşır). Kurallar ve beyaz listeler açılışta bir kez derlenir; istek yolunda yalnızca eşleştirme ve depo çağrısı yapılır.
/// </summary>
/// <typeparam name="TRule">Barındırıcının kural türü.</typeparam>
public sealed class InboundRateLimitEngine<TRule>
    where TRule : InboundRateLimitRule
{
    private readonly CompiledRule[] _rules;
    private readonly InboundEndpointPattern[] _endpointWhitelist;
    private readonly InboundIpRule[] _ipWhitelist;
    private readonly HashSet<string> _clientWhitelist;
    private readonly string _keyPrefix;

    /// <summary>Kuralları ve beyaz listeleri derler (seçenekler önceden doğrulanmış olmalıdır).</summary>
    public InboundRateLimitEngine(IEnumerable<TRule> rules, InboundRateLimitOptionsBase options)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(options);
        _rules = [.. rules.Select(static rule => new CompiledRule(rule))];
        _endpointWhitelist = [.. options.EndpointWhitelist.Select(InboundEndpointPattern.Parse)];
        _ipWhitelist = [.. options.IpWhitelist.Select(InboundIpRule.Parse)];
        _clientWhitelist = new HashSet<string>(options.ClientWhitelist, StringComparer.Ordinal);
        _keyPrefix = options.KeyPrefix;
    }

    /// <summary>En az bir kural var mı (yoksa istek hiç sınırlanmaz).</summary>
    public bool HasRules => _rules.Length > 0;

    /// <summary>İstek beyaz listede mi (bölüm anahtarı, uç nokta ya da istemci IP'si).</summary>
    public bool IsExempt(string method, string path, string partitionKey, IPAddress? clientAddress)
    {
        if (_clientWhitelist.Contains(partitionKey) || _endpointWhitelist.Any(pattern => pattern.Matches(method, path)))
        {
            return true;
        }

        return clientAddress is not null && _ipWhitelist.Any(rule => rule.Contains(clientAddress));
    }

    /// <summary>
    /// Eşleşen her kural için bölüm anahtarıyla depodan izin ister. Biri reddederse o kuralla red döner; hepsi geçerse en az
    /// kotası kalan kuralla izin döner (yanıt başlıkları için).
    /// </summary>
    public async ValueTask<InboundRateLimitDecision<TRule>> AcquireAsync(
        IDistributedRateLimitStore store, string method, string path, string partitionKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);

        TRule? tightest = null;
        var tightestRemaining = int.MaxValue;
        foreach (var rule in _rules)
        {
            if (!rule.Pattern.Matches(method, path))
            {
                continue;
            }

            var decision = await store.TryAcquireAsync($"{_keyPrefix}:{rule.Id}:{partitionKey}", rule.Limit, 1, cancellationToken)
                .ConfigureAwait(false);
            if (!decision.IsAcquired)
            {
                return new InboundRateLimitDecision<TRule>(false, rule.Rule, 0, decision.RetryAfter);
            }

            if (decision.Remaining < tightestRemaining)
            {
                tightest = rule.Rule;
                tightestRemaining = decision.Remaining;
            }
        }

        return new InboundRateLimitDecision<TRule>(true, tightest, tightest is null ? 0 : tightestRemaining, null);
    }

    private sealed class CompiledRule(TRule rule)
    {
        public TRule Rule { get; } = rule;

        public InboundEndpointPattern Pattern { get; } = InboundEndpointPattern.Parse(rule.Endpoint);

        public DistributedRateLimitRule Limit { get; } = new(rule.Algorithm, rule.Limit, rule.Period, rule.Limit);

        /// <summary>Sayaç kimliği: kural değişince (limit, periyot, algoritma) yeni sayaç başlar.</summary>
        public string Id { get; } = FormattableString.Invariant(
            $"{rule.Endpoint}|{rule.Limit}|{(long)rule.Period.TotalMilliseconds}|{(int)rule.Algorithm}");
    }
}
