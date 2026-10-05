using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.ExceptionSummarization;
using Microsoft.Extensions.Http.Diagnostics;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Telemetry;
using Aegis.Resilience.Extensions.DependencyInjection;

namespace Aegis.Resilience.Extensions.Telemetry;

/// <summary>DI kaydı.</summary>
public static class AegisTelemetryServiceCollectionExtensions
{
    /// <summary>
    /// DI ile kurulan tüm Aegis boru hatlarının standart metriklerine <c>error.type</c>, <c>request.name</c> ve
    /// <c>request.dependency.name</c> etiketlerini ekler (Microsoft: <c>AddResilienceEnricher</c>). <c>error.type</c> için
    /// bir <see cref="IExceptionSummarizer"/> kayıtlı olmalıdır (ör. <c>services.AddExceptionSummarizer(b =&gt; b.AddHttpProvider())</c>).
    /// Birden çok çağrı güvenlidir.
    /// </summary>
    public static IServiceCollection AddAegisResilienceEnricher(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(static s => s.ServiceType == typeof(AegisResilienceMetricsEnricher)))
        {
            return services;
        }

        services.AddSingleton(sp => new AegisResilienceMetricsEnricher(
            sp.GetService<IExceptionSummarizer>(),
            sp.GetService<IOutgoingRequestContext>()));

        return services.ConfigureAegisTelemetry(t => t.Configure.Add(static (options, sp) =>
            options.MeteringEnrichers.Add(sp.GetRequiredService<AegisResilienceMetricsEnricher>().Enrich)));
    }
}
