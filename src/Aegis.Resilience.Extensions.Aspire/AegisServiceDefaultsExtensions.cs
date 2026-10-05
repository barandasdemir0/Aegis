using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Aegis.Resilience.Core.Telemetry;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.HealthChecks;
using Aegis.Resilience.Extensions.Http;

namespace Aegis.Resilience.Extensions.Aspire;

/// <summary>.NET Aspire ServiceDefaults projesi için tek çağrılık Aegis kurulumu.</summary>
public static class AegisServiceDefaultsExtensions
{
    /// <summary>
    /// Aegis'i Aspire ServiceDefaults'a ekler: DI altyapısı, tüm HttpClient'lara standart dayanıklılık işleyicisi
    /// (Microsoft <c>AddStandardResilienceHandler</c> eşdeğeri), <c>Aegis</c> metrikleri, <c>Aegis</c> iz kaynağı ve sağlık kontrolü.
    /// ServiceDefaults şablonundaki <c>http.AddStandardResilienceHandler()</c> satırını kaldırın; ikisi birlikte
    /// kullanılırsa her istek iki dayanıklılık zincirinden geçer.
    /// </summary>
    public static TBuilder AddAegisServiceDefaults<TBuilder>(this TBuilder builder, Action<AegisServiceDefaultsOptions>? configure = null)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new AegisServiceDefaultsOptions();
        configure?.Invoke(options);

        var services = builder.Services;
        services.AddAegis();

        if (options.AddHttpResilience)
        {
            var section = builder.Configuration.GetSection(options.ConfigurationSectionName);
            services.ConfigureHttpClientDefaults(http => http.AddStandardAegisHandler(section, options.ConfigureHttp));
        }

        if (options.AddMetrics)
        {
            services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddMeter(AegisTelemetry.MeterName));
        }

        if (options.AddTracing)
        {
            services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddSource(AegisTelemetry.ActivitySourceName));
        }

        if (options.AddHealthCheck)
        {
            services.AddHealthChecks().AddAegisCheck();
        }

        return builder;
    }
}
