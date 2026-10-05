using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Extensions.DependencyInjection.Telemetry;

namespace Aegis.Resilience.Extensions.DependencyInjection;

/// <summary>
/// Gelişmiş kayıtlar (Polly: <c>AddResiliencePipeline&lt;TKey&gt;</c>, <c>AddResiliencePipelines&lt;TKey&gt;</c>,
/// <c>EnableReloads&lt;TOptions&gt;</c>): generic anahtarlı / dinamik boru hatları ve yapılandırma değişince yeniden yükleme.
/// </summary>
public static class AdvancedRegistrationExtensions
{
    /// <summary>
    /// Generic anahtarlı bir boru hattı tanımlar. <see cref="IAegisPipelineProvider{TKey}"/> ile çözülür.
    /// Örnek: <c>services.AddAegisPipeline(new EndpointKey("odeme", "v2"), (b, key, sp) =&gt; b.AddRetry(...))</c>.
    /// </summary>
    public static IServiceCollection AddAegisPipeline<TKey>(
        this IServiceCollection services,
        TKey key,
        Action<IAegisPipelineBuilder, TKey, IServiceProvider> configure)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(configure);
        EnsureKeyedRegistry<TKey>(services);
        services.AddSingleton(new KeyedPipelineDefinition<TKey>(key, configure));
        return services;
    }

    /// <summary>
    /// Önceden bilinmeyen anahtarlar için dinamik boru hatları (Polly: <c>AddResiliencePipelines&lt;TKey&gt;</c> +
    /// <c>GetOrAddPipeline</c>). Ör. kiracı başına ayrı devre kesici ve kota. Her anahtar ilk erişimde bir kez kurulur.
    /// </summary>
    /// <param name="services">Servis koleksiyonu.</param>
    /// <param name="configure">Anahtar başına boru hattı yapılandırması.</param>
    /// <param name="maxDynamicPipelines">Kardinalite koruması (rastgele anahtarla bellek şişirmeye karşı).</param>
    public static IServiceCollection AddAegisPipelines<TKey>(
        this IServiceCollection services,
        Action<IAegisPipelineBuilder, TKey, IServiceProvider> configure,
        int maxDynamicPipelines = 10_000)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(configure);
        EnsureKeyedRegistry<TKey>(services);
        services.AddSingleton(new DynamicPipelineDefinition<TKey>(configure, maxDynamicPipelines));
        return services;
    }

    private static void EnsureKeyedRegistry<TKey>(IServiceCollection services)
        where TKey : notnull
    {
        services.AddAegis();
        services.TryAddSingleton(sp =>
        {
            var registry = new AegisPipelineRegistry<TKey>
            {
                ConfigureBuilder = (builder, _) => AegisTelemetryConfiguration.Apply(builder, sp)
            };

            foreach (var definition in sp.GetServices<KeyedPipelineDefinition<TKey>>())
            {
                registry.TryAddBuilder(definition.Key, (b, k) => definition.Configure(b, k, sp));
            }

            if (sp.GetServices<DynamicPipelineDefinition<TKey>>().LastOrDefault() is { } dynamicDefinition)
            {
                registry.DynamicBuilder = (b, k) => dynamicDefinition.Configure(b, k, sp);
                registry.MaxDynamicPipelines = dynamicDefinition.MaxDynamicPipelines;
            }

            return registry;
        });
        services.TryAddSingleton<IAegisPipelineProvider<TKey>>(sp => sp.GetRequiredService<AegisPipelineRegistry<TKey>>());
    }

    /// <summary>
    /// <typeparamref name="TOptions"/> değişince (ör. appsettings.json düzenlendi) TÜM boru hattını yeniden kuran adlandırılmış
    /// boru hattı (Polly: <c>EnableReloads&lt;TOptions&gt;</c>). Uçuştaki çağrılar eski nesille tamamlanır; eski nesil sonra
    /// dispose edilir. Kurulum hatasında eski nesil çalışmaya devam eder.
    /// </summary>
    /// <param name="services">Servis koleksiyonu.</param>
    /// <param name="name">Boru hattı adı.</param>
    /// <param name="configure">Güncel seçeneklerle boru hattı yapılandırması (her yeniden yüklemede çağrılır).</param>
    /// <param name="optionsName">İzlenecek adlandırılmış seçenek (varsayılan: adsız seçenek).</param>
    public static IServiceCollection AddAegisPipeline<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(
        this IServiceCollection services,
        string name,
        Action<IAegisPipelineBuilder, TOptions, IServiceProvider> configure,
        string? optionsName = null)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddAegis();
        services.AddOptions<TOptions>();
        services.AddSingleton<IAegisPipelineConfigurator>(new ReloadableConfigurator<TOptions>(name, configure, optionsName ?? Options.DefaultName));
        return services;
    }

    private sealed record KeyedPipelineDefinition<TKey>(TKey Key, Action<IAegisPipelineBuilder, TKey, IServiceProvider> Configure);

    private sealed record DynamicPipelineDefinition<TKey>(Action<IAegisPipelineBuilder, TKey, IServiceProvider> Configure, int MaxDynamicPipelines);

    private sealed class ReloadableConfigurator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(
        string name,
        Action<IAegisPipelineBuilder, TOptions, IServiceProvider> configure,
        string optionsName) : IAegisPipelineConfigurator
        where TOptions : class
    {
        public string Name { get; } = name;

        public IAegisPipeline Build(IServiceProvider serviceProvider)
        {
            var monitor = serviceProvider.GetRequiredService<IOptionsMonitor<TOptions>>();

            IAegisPipeline Create()
            {
                var builder = new AegisPipelineBuilder(Name);
                configure(builder, monitor.Get(optionsName), serviceProvider);
                AegisTelemetryConfiguration.Apply(builder, serviceProvider);
                return builder.Build();
            }

            var pipeline = new ReloadableAegisPipeline(Name, Create);
            var subscription = monitor.OnChange((_, changedName) =>
            {
                if (string.Equals(changedName ?? Options.DefaultName, optionsName, StringComparison.Ordinal))
                {
                    pipeline.Reload();
                }
            });

            if (subscription is not null)
            {
                pipeline.AddOwnedDisposable(subscription);
            }

            return pipeline;
        }
    }
}
