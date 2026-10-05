using System.Net.Http;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Hedging;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Extensions.DependencyInjection;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// Standart hedging işleyicisi (Microsoft: <c>AddStandardHedgingHandler</c>). Toplam zaman aşımı + Aegis hedging (sonuca göre:
/// 5xx/408/429 yanıtlar sonraki denemeyi tetikler, atılan yanıtlar dispose edilir) + uç nokta (authority) başına ayrı
/// eşzamanlılık sınırı, devre kesici ve deneme zaman aşımı. Yönlendirme grupları (sıralı / ağırlıklı) her denemeyi farklı
/// bölgeye/uç noktaya gönderir.
/// </summary>
internal sealed class AegisStandardHedgingHandler(
    Reloadable<HedgingHandlerRuntime> runtime,
    long maxRequestBodySize = HttpRequestReplayHandler.DefaultMaxRequestBodySize) : AegisDelegatingHandler
{
    protected override bool RunsAttemptsConcurrently => true;

    protected override async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, AegisHttpSender innerSend, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.RequestUri);

        using var lease = runtime.Acquire();
        var current = lease.Value;
        var options = current.Options;

        var context = request.GetOrCreateAegisContext(current.Outer.Name);
        context.CancellationToken = cancellationToken;

        var isBodyReplayable = true;
        if (request.Content is not null)
        {
            if (request.Content.Headers.ContentLength > maxRequestBodySize)
            {
                isBodyReplayable = false;
            }
            else
            {
                await HttpResilienceExecutor.BufferContentAsync(request.Content, maxRequestBodySize, cancellationToken).ConfigureAwait(false);
            }
        }

        var plan = RoutingPlan.Create(options);
        var resendSafe = isBodyReplayable && (options.AllowNonIdempotentHedging || HttpResilienceExecutor.IsIdempotent(request));
        if (!resendSafe)
        {
            context.Properties[AegisContextKeys.SuppressAdditionalAttempts] = true;
        }

        if (plan.Count > 0)
        {
            context.SetProperty(HedgingOptions.MaxAttemptsKey, plan.Count); // grup sayısından fazla deneme yapılmaz (Microsoft ile aynı)
        }

        try
        {
            return await current.Outer.ExecuteAsync(
                async attemptContext =>
                {
                    var attempt = attemptContext.GetPropertyOrDefault(HedgingOptions.AttemptNumberKey, 0);
                    var target = plan.EndpointFor(attempt) is { } endpoint ? Rebase(request.RequestUri, endpoint) : request.RequestUri;

                    using var clone = await HttpRequestReplayHandler.CloneRequestAsync(request, maxRequestBodySize, attemptContext.CancellationToken)
                        .ConfigureAwait(false);
                    clone.RequestUri = target;

                    var endpointPipeline = current.Endpoints.GetPipeline(target.GetLeftPart(UriPartial.Authority));
                    return await endpointPipeline.ExecuteAsync(
                        ctx => new ValueTask<HttpResponseMessage>(innerSend(clone, ctx.CancellationToken)),
                        attemptContext).ConfigureAwait(false);
                },
                context).ConfigureAwait(false);
        }
        finally
        {
            if (!resendSafe)
            {
                context.Properties.Remove(AegisContextKeys.SuppressAdditionalAttempts);
            }

            if (plan.Count > 0)
            {
                context.Properties.Remove(HedgingOptions.MaxAttemptsKey.Key);
            }
        }
    }

    /// <summary>İsteğin yol ve sorgusunu koruyarak şema/ana bilgisayar/portu hedef uç noktayla değiştirir.</summary>
    internal static Uri Rebase(Uri original, Uri endpoint) =>
        new UriBuilder(original) { Scheme = endpoint.Scheme, Host = endpoint.Host, Port = endpoint.Port }.Uri;

    /// <summary>İstek başına deneme → uç nokta planı.</summary>
    internal sealed class RoutingPlan
    {
        private static readonly RoutingPlan Empty = new([]);
        private readonly Uri[] _endpoints;

        private RoutingPlan(Uri[] endpoints) => _endpoints = endpoints;

        public int Count => _endpoints.Length;

        public Uri? EndpointFor(int attempt) => attempt < _endpoints.Length ? _endpoints[attempt] : null;

        public static RoutingPlan Create(AegisHttpStandardHedgingOptions options)
        {
            if (options.OrderedGroups.Count > 0)
            {
                var endpoints = new Uri[options.OrderedGroups.Count];
                for (var i = 0; i < endpoints.Length; i++)
                {
                    endpoints[i] = PickEndpoint(options.OrderedGroups[i]);
                }

                return new RoutingPlan(endpoints);
            }

            if (options.WeightedGroups.Count > 0)
            {
                var remaining = new List<WeightedUriEndpointGroup>(options.WeightedGroups);
                var endpoints = new Uri[remaining.Count];
                for (var i = 0; i < endpoints.Length; i++)
                {
                    // İlk deneme her zaman ağırlıklı; InitialAttempt modunda sonrakiler tanım sırasıyla.
                    var index = i == 0 || options.SelectionMode == WeightedGroupSelectionMode.EveryAttempt
                        ? PickWeightedIndex(remaining, static g => g.Weight)
                        : 0;
                    endpoints[i] = PickEndpoint(remaining[index]);
                    remaining.RemoveAt(index);
                }

                return new RoutingPlan(endpoints);
            }

            return Empty;
        }

        private static Uri PickEndpoint(UriEndpointGroup group) =>
            group.Endpoints[PickWeightedIndex(group.Endpoints, static e => e.Weight)].Uri;

        private static int PickWeightedIndex<T>(IList<T> items, Func<T, int> weight)
        {
            if (items.Count == 1)
            {
                return 0;
            }

            var total = 0;
            foreach (var item in items)
            {
                total += Math.Max(1, weight(item));
            }

            var roll = Random.Shared.Next(total);
            for (var i = 0; i < items.Count; i++)
            {
                roll -= Math.Max(1, weight(items[i]));
                if (roll < 0)
                {
                    return i;
                }
            }

            return items.Count - 1;
        }
    }
}
