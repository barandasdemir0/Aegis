using Microsoft.Extensions.DependencyInjection;

namespace Aegis.Resilience.Extensions.DependencyInjection.Aop;

public static class AopServiceCollectionExtensions
{
    /// <summary>
    /// Servisi AegisDispatchProxy ile sarmalayarak Scoped yaşam döngüsünde kaydeder.
    /// Metotlar veya arayüz üzerindeki [AegisPolicy] özniteliğine göre otomatik resilience uygulanır.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AegisAotMessages.DispatchProxy)]
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AegisAotMessages.DispatchProxy)]
    public static IServiceCollection AddAegisProxiedScoped<TInterface, [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(
        this IServiceCollection services,
        string? defaultPipelineName = null)
        where TInterface : class
        where TImplementation : class, TInterface
    {
        return AddProxied<TInterface, TImplementation>(services, ServiceLifetime.Scoped, defaultPipelineName);
    }

    /// <summary>
    /// Servisi AegisDispatchProxy ile sarmalayarak Transient yaşam döngüsünde kaydeder.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AegisAotMessages.DispatchProxy)]
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AegisAotMessages.DispatchProxy)]
    public static IServiceCollection AddAegisProxiedTransient<TInterface, [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(
        this IServiceCollection services,
        string? defaultPipelineName = null)
        where TInterface : class
        where TImplementation : class, TInterface
    {
        return AddProxied<TInterface, TImplementation>(services, ServiceLifetime.Transient, defaultPipelineName);
    }

    /// <summary>
    /// Servisi AegisDispatchProxy ile sarmalayarak Singleton yaşam döngüsünde kaydeder.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AegisAotMessages.DispatchProxy)]
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AegisAotMessages.DispatchProxy)]
    public static IServiceCollection AddAegisProxiedSingleton<TInterface, [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(
        this IServiceCollection services,
        string? defaultPipelineName = null)
        where TInterface : class
        where TImplementation : class, TInterface
    {
        return AddProxied<TInterface, TImplementation>(services, ServiceLifetime.Singleton, defaultPipelineName);
    }

    // Üç yaşam döngüsünün ortak gövdesi: gerçek servis + onu Aegis proxy'siyle sarmalayan arayüz kaydı (aynı yaşam döngüsü).
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode(AegisAotMessages.DispatchProxy)]
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(AegisAotMessages.DispatchProxy)]
    private static IServiceCollection AddProxied<TInterface, [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)] TImplementation>(
        IServiceCollection services,
        ServiceLifetime lifetime,
        string? defaultPipelineName)
        where TInterface : class
        where TImplementation : class, TInterface
    {
        services.Add(new ServiceDescriptor(typeof(TImplementation), typeof(TImplementation), lifetime));
        services.Add(new ServiceDescriptor(typeof(TInterface), sp =>
        {
            var target = sp.GetRequiredService<TImplementation>();
            var registry = sp.GetRequiredService<IAegisPipelineRegistry>();
            return AegisDispatchProxy<TInterface>.Create(target, registry, defaultPipelineName);
        }, lifetime));

        return services;
    }
}
