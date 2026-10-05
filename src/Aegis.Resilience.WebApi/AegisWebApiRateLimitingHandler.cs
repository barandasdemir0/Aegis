using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Aegis.Resilience.Core.Telemetry;
using Aegis.Resilience.Distributed.Abstractions;

namespace Aegis.Resilience.WebApi;

/// <summary>
/// Web API 2 gelen istek hız sınırlama işleyicisi (WebApiThrottle <c>ThrottlingHandler</c> eşdeğeri). Kural değerlendirmesi
/// ortak motordadır (<see cref="InboundRateLimitEngine{TRule}"/>); bu sınıf yalnızca Web API isteğini motora uyarlar ve yanıtı
/// yazar. Biri reddederse denetleyici çalışmaz, <see cref="AegisWebApiRateLimitOptions.RejectionStatusCode"/> ve
/// <c>Retry-After</c> döner.
/// </summary>
public sealed class AegisWebApiRateLimitingHandler : DelegatingHandler
{
    private const string AnonymousPartition = "anonim";

    private readonly AegisWebApiRateLimitOptions _options;
    private readonly IDistributedRateLimitStore _store;
    private readonly InboundRateLimitEngine<AegisWebApiRateLimitRule> _engine;

    /// <summary>Seçenekleri doğrular ve derler (geçersizse açılışta istisna). <paramref name="store"/> null ise bellek içi depo.</summary>
    public AegisWebApiRateLimitingHandler(AegisWebApiRateLimitOptions options, IDistributedRateLimitStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _store = store ?? new InMemoryDistributedRateLimitStore();
        _engine = new InboundRateLimitEngine<AegisWebApiRateLimitRule>(options.Rules, options);
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var method = request.Method.Method;
        var path = request.RequestUri?.AbsolutePath ?? "/";
        var partition = _options.PartitionKeySelector(request) ?? AnonymousPartition;
        if (!_engine.HasRules || _engine.IsExempt(method, path, partition, ClientAddress(request)))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var decision = await _engine.AcquireAsync(_store, method, path, partition, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return Reject(request, decision.Rule!, partition, decision.RetryAfter);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (_options.EmitRateLimitHeaders && decision.Rule is { } tightest)
        {
            WriteLimitHeaders(response, tightest.Limit, decision.Remaining);
        }

        return response;
    }

    private static IPAddress? ClientAddress(HttpRequestMessage request) =>
        AegisWebApiRateLimitOptions.ClientIp(request) is { } ip && IPAddress.TryParse(ip, out var address) ? address : null;

    private HttpResponseMessage Reject(HttpRequestMessage request, AegisWebApiRateLimitRule rule, string partition, TimeSpan? retryAfter)
    {
        AegisTelemetry.RateLimitRejectionsTotal.Add(1, new KeyValuePair<string, object?>("pipeline", "inbound"));

        var response = request.CreateResponse(_options.RejectionStatusCode);
        if (_options.EmitRateLimitHeaders)
        {
            WriteLimitHeaders(response, rule.Limit, remaining: 0);
            if (retryAfter is { } wait)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling(wait.TotalSeconds))));
            }
        }

        _options.OnRejected?.Invoke(request, new AegisWebApiRateLimitRejection(rule, partition, retryAfter), response);
        return response;
    }

    // IETF "RateLimit header fields for HTTP" taslağındaki adlar (Aegis.Resilience.AspNetCore ile aynı).
    private static void WriteLimitHeaders(HttpResponseMessage response, int limit, int remaining)
    {
        response.Headers.Remove("RateLimit-Limit");
        response.Headers.Remove("RateLimit-Remaining");
        response.Headers.TryAddWithoutValidation("RateLimit-Limit", limit.ToString(CultureInfo.InvariantCulture));
        response.Headers.TryAddWithoutValidation("RateLimit-Remaining", Math.Max(0, remaining).ToString(CultureInfo.InvariantCulture));
    }
}
