using System.Web.Http;
using Aegis.Resilience.Distributed.Abstractions;

namespace Aegis.Resilience.WebApi;

/// <summary>Web API 2 kurulum uzantıları.</summary>
public static class AegisWebApiConfigurationExtensions
{
    /// <summary>
    /// Gelen istek hız sınırlamasını Web API hattına ekler (<c>WebApiConfig.Register</c> içinde çağırın).
    /// Küme genelinde ortak kota için <paramref name="store"/> olarak Redis deposunu verin.
    /// </summary>
    public static HttpConfiguration UseAegisRateLimiting(
        this HttpConfiguration configuration, Action<AegisWebApiRateLimitOptions> configure, IDistributedRateLimitStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new AegisWebApiRateLimitOptions();
        configure(options);
        configuration.MessageHandlers.Add(new AegisWebApiRateLimitingHandler(options, store));
        return configuration;
    }
}
