using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Aegis.Resilience.Core.Telemetry;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.HealthChecks;
using Aegis.Resilience.Extensions.Http;

namespace Aegis.Resilience.Extensions.Aspire;

/// <summary><see cref="AegisServiceDefaultsExtensions.AddAegisServiceDefaults{TBuilder}"/> seçenekleri.</summary>
public sealed class AegisServiceDefaultsOptions
{
    /// <summary>Standart işleyicinin bağlandığı yapılandırma bölümü (değişince yeniden kurulur).</summary>
    public string ConfigurationSectionName { get; set; } = "Aegis:Http";

    /// <summary>Tüm HttpClient'lara <c>AddStandardAegisHandler</c> eklenir mi (varsayılan: evet).</summary>
    public bool AddHttpResilience { get; set; } = true;

    /// <summary>Aegis metrikleri OpenTelemetry MeterProvider'a eklenir mi (varsayılan: evet).</summary>
    public bool AddMetrics { get; set; } = true;

    /// <summary>
    /// Aegis iz kaynağı OpenTelemetry TracerProvider'a eklenir mi (varsayılan: evet). Boru hattı yürütmesi başına bir span üretilir;
    /// uygulamada iz dışa aktarımı yoksa hiçbir maliyeti yoktur.
    /// </summary>
    public bool AddTracing { get; set; } = true;

    /// <summary>Aegis sağlık kontrolü eklenir mi (varsayılan: evet; etiketler: resilience, aegis, ready).</summary>
    public bool AddHealthCheck { get; set; } = true;

    /// <summary>Standart işleyicinin kodla ayarı; yapılandırma bölümünden sonra uygulanır.</summary>
    public Action<AegisHttpStandardResilienceOptions>? ConfigureHttp { get; set; }
}
