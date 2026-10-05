using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aegis.Resilience.Extensions.HealthChecks;

public static class AegisHealthChecksBuilderExtensions
{
    private static readonly string[] DefaultTags = ["resilience", "aegis", "ready"];

    /// <summary>
    /// ASP.NET Core Health Checks altyapısına Aegis dayanıklılık kontrolünü ekler.
    /// </summary>
    public static IHealthChecksBuilder AddAegisCheck(
        this IHealthChecksBuilder builder,
        string name = "aegis_resilience",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        Action<AegisHealthCheckOptions>? configureOptions = null)
    {
        // Ayar bu kontrolün adına bağlanır: canlılık (Degraded) ve hazır olma (Unhealthy) gibi farklı ayarlı iki kontrol birbirini
        // ezmez (önceden ikinci çağrının ayarı birinciyi de değiştiriyordu). Ayar verilmezse uygulama geneli ayar kullanılır.
        if (configureOptions != null)
        {
            builder.Services.Configure(name, configureOptions);
        }

        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new AegisHealthCheck(
                sp.GetRequiredService<Aegis.Resilience.Extensions.DependencyInjection.IAegisPipelineRegistry>(),
                configureOptions != null
                    ? Microsoft.Extensions.Options.Options.Create(sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AegisHealthCheckOptions>>().Get(name))
                    : sp.GetService<Microsoft.Extensions.Options.IOptions<AegisHealthCheckOptions>>()),
            failureStatus ?? HealthStatus.Degraded,
            tags ?? DefaultTags));
    }
}
