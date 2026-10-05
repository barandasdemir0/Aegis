using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Distributed.Abstractions;

namespace Aegis.Resilience.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Aegis resilience altyapısını ve kayıt defterini Dependency Injection konteynerine ekler.
    /// </summary>
    public static IServiceCollection AddAegis(this IServiceCollection services)
    {
        services.TryAddSingleton<IAegisPipelineRegistry, AegisPipelineRegistry>();
        services.TryAddSingleton<ICircuitBreakerStateStore, InMemoryCircuitBreakerStateStore>();
        return services;
    }

    /// <summary>
    /// Adlandırılmış bir Aegis boru hattı (Pipeline) tanımlar ve DI konteynerine kaydeder.
    /// </summary>
    public static IServiceCollection AddAegisPipeline(
        this IServiceCollection services,
        string name,
        Action<IAegisPipelineBuilder, IServiceProvider> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddAegis();
        services.AddSingleton<IAegisPipelineConfigurator>(new DelegateAegisPipelineConfigurator(name, configure));

        return services;
    }

    /// <summary>
    /// Kurulum bağlamıyla adlandırılmış boru hattı tanımlar (Polly: <c>AddResiliencePipeline(key, (builder, context) =&gt; ...)</c>).
    /// Bağlam servis sağlayıcıyı, seçenekleri (<c>GetOptions</c>), yeniden kurmayı (<c>EnableReloads</c>, <c>AddReloadToken</c>) ve
    /// dispose bildirimini (<c>OnPipelineDisposed</c>) sunar. Yeniden kurmada uçuştaki çağrılar eski nesille tamamlanır.
    /// Örnek: <c>services.AddAegisPipelineWithContext("odeme", (b, ctx) =&gt; { var o = ctx.GetOptions&lt;OdemeAyarlari&gt;(); ctx.EnableReloads&lt;OdemeAyarlari&gt;(); b.AddRetry(r =&gt; r.MaxRetryAttempts = o.Deneme); })</c>.
    /// </summary>
    public static IServiceCollection AddAegisPipelineWithContext(
        this IServiceCollection services,
        string name,
        Action<IAegisPipelineBuilder, AegisPipelineContext> configure)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddAegis();
        services.AddSingleton<IAegisPipelineConfigurator>(new ContextualPipelineConfigurator(name, configure));
        return services;
    }

    /// <summary>
    /// DI ile kurulan tüm boru hatlarının telemetrisini yapılandırır (otomatik <c>ILogger</c> günlüğü, sonuç biçimleyici,
    /// ek dinleyici/zenginleştirici). Örnek: <c>services.ConfigureAegisTelemetry(t =&gt; t.EnableLogging = false)</c>.
    /// </summary>
    public static IServiceCollection ConfigureAegisTelemetry(this IServiceCollection services, Action<Telemetry.AegisTelemetryConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        services.AddAegis();
        services.Configure(configure);
        return services;
    }

    /// <summary>
    /// Basit parametrelerle adlandırılmış bir Aegis boru hattı tanımlar.
    /// </summary>
    public static IServiceCollection AddAegisPipeline(
        this IServiceCollection services,
        string name,
        Action<IAegisPipelineBuilder> configure)
    {
        return services.AddAegisPipeline(name, (builder, _) => configure(builder));
    }
}
