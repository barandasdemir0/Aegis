using System.Net.Http;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// Gelen istekleri yapılandırılan ağırlıklara göre (örneğin %90 Üretim v1, %10 Kanarya v2)
/// farklı uç noktalara dağıtan DelegatingHandler.
/// </summary>
public sealed class WeightedCanaryHandler : AegisDelegatingHandler
{
    private readonly WeightedCanaryOptions _options;

    public WeightedCanaryHandler(WeightedCanaryOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    protected override Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, AegisHttpSender innerSend, CancellationToken cancellationToken)
    {
        var endpoints = _options.EndpointsProvider?.Invoke(request) ?? _options.Endpoints;
        if (endpoints == null || endpoints.Count <= 1)
        {
            return innerSend(request, cancellationToken);
        }

        var selectedEndpoint = SelectEndpoint(request, endpoints);
        if (selectedEndpoint != null)
        {
            request.RequestUri = RewriteUri(request.RequestUri, selectedEndpoint.Uri);
        }

        return innerSend(request, cancellationToken);
    }

    private WeightedEndpoint SelectEndpoint(HttpRequestMessage request, IReadOnlyList<WeightedEndpoint> endpoints)
    {
        var totalWeight = 0.0;
        foreach (var ep in endpoints)
        {
            if (ep.Weight > 0)
            {
                totalWeight += ep.Weight;
            }
        }

        if (totalWeight <= 0)
        {
            return endpoints[0];
        }

        // 1. Sticky Session kontrolü (aynı oturum / kullanıcı aynı uç noktaya yönlendirilir)
        var stickyKey = _options.StickySessionKeySelector?.Invoke(request);
        if (stickyKey is not null and not "")
        {
            var hash = ComputeDeterministicHash(stickyKey);
            var targetFraction = (hash % 10000) / 10000.0 * totalWeight;

            var running = 0.0;
            foreach (var ep in endpoints)
            {
                if (ep.Weight <= 0) continue;
                running += ep.Weight;
                if (targetFraction <= running)
                {
                    return ep;
                }
            }

            return endpoints[0];
        }

        // 2. Rastgele ağırlıklı seçim
        var randomVal = Random.Shared.NextDouble() * totalWeight;
        var cumulative = 0.0;

        foreach (var ep in endpoints)
        {
            if (ep.Weight <= 0) continue;
            cumulative += ep.Weight;
            if (randomVal <= cumulative)
            {
                return ep;
            }
        }

        return endpoints[0];
    }

    private static Uri RewriteUri(Uri? originalUri, Uri targetBaseUri)
    {
        if (originalUri == null)
        {
            return targetBaseUri;
        }

        if (!originalUri.IsAbsoluteUri)
        {
            return new Uri(targetBaseUri, originalUri);
        }

        var builder = new UriBuilder(originalUri)
        {
            Scheme = targetBaseUri.Scheme,
            Host = targetBaseUri.Host,
            Port = targetBaseUri.Port
        };

        return builder.Uri;
    }

    /// <summary>
    /// Multi-pod ve dağıtık süreçler arasında deterministik yönlendirme sağlayan 32-bit FNV-1a hash algoritması (AEGIS-102).
    /// .NET string.GetHashCode() süreç bazlı rastgele seed kullandığı için yerine bu yöntem tercih edilir.
    /// </summary>
    public static uint ComputeDeterministicHash(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in text)
            {
                hash = (hash ^ c) * 16777619;
            }
            return hash;
        }
    }
}
