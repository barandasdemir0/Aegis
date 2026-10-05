using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Extensions.DependencyInjection.Telemetry;

/// <summary>
/// DI ile kurulan tüm boru hatlarının telemetri varsayılanları (Polly: <c>AddResiliencePipeline</c> içindeki otomatik
/// telemetri). <c>services.ConfigureAegisTelemetry(...)</c> ile değiştirilir.
/// </summary>
public sealed class AegisTelemetryConfiguration
{
    /// <summary>DI'da <see cref="ILoggerFactory"/> varsa olayları otomatik loglar. Varsayılan: açık.</summary>
    public bool EnableLogging { get; set; } = true;

    /// <summary>Loglanan sonucun biçimi (ör. hassas veriyi maskelemek). Null ise <see cref="AegisLoggingTelemetryListener.DefaultResultFormatter"/>.</summary>
    public Func<AegisContext, object?, object?>? ResultFormatter { get; set; }

    /// <summary>Her boru hattının telemetri seçeneklerine uygulanır (dinleyici, zenginleştirici, önem sağlayıcı eklemek için).</summary>
    public IList<Action<AegisTelemetryOptions, IServiceProvider>> Configure { get; } = new List<Action<AegisTelemetryOptions, IServiceProvider>>();

    /// <summary>
    /// DI'daki telemetri varsayılanlarını (otomatik <c>ILogger</c> günlüğü, <c>ConfigureAegisTelemetry</c> ayarları) bir
    /// builder'a uygular. Kullanıcının builder'da verdiği seçenekler korunur. <c>AddAegisPipeline</c> ve DI bağlamlı HTTP
    /// işleyicileri bunu kendiliğinden çağırır; DI dışında kurulan bir boru hattına aynı telemetriyi vermek için kullanılır.
    /// </summary>
    public static void Apply(AegisPipelineBuilder builder, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var configuration = serviceProvider.GetService<IOptions<AegisTelemetryConfiguration>>()?.Value ?? new AegisTelemetryConfiguration();
        var loggerFactory = configuration.EnableLogging ? serviceProvider.GetService<ILoggerFactory>() : null;

        if (loggerFactory is null && configuration.Configure.Count == 0)
        {
            return; // eklenecek bir şey yok: standart metrikler zaten seçeneksiz de yayınlanır
        }

        var options = builder.TelemetryOptions ??= new AegisTelemetryOptions();
        foreach (var configure in configuration.Configure)
        {
            configure(options, serviceProvider);
        }

        if (loggerFactory is not null && !options.Listeners.Any(static l => l is AegisLoggingTelemetryListener))
        {
            options.Listeners.Add(new AegisLoggingTelemetryListener(loggerFactory, configuration.ResultFormatter));
        }
    }
}
