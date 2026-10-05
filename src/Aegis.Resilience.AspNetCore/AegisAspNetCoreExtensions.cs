using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Aegis.Resilience.Distributed.Abstractions;

namespace Aegis.Resilience.AspNetCore;

/// <summary>ASP.NET Core sunucu tarafı koruma kayıtları.</summary>
public static class AegisAspNetCoreExtensions
{
    /// <summary>
    /// Gelen istek hız sınırlamayı koddan yapılandırır. Sayaç deposu: DI'da <see cref="IDistributedRateLimitStore"/> varsa
    /// o (ör. <c>AddAegisRedisRateLimitStore</c> ile küme genelinde), yoksa bellek içi. Ardından <see cref="UseAegisInboundRateLimiting"/>.
    /// </summary>
    public static IServiceCollection AddAegisInboundRateLimiting(this IServiceCollection services, Action<AegisInboundRateLimitOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.Configure(configure);
        return AddInboundRateLimitingCore(services);
    }

    /// <summary>
    /// Gelen istek hız sınırlamayı yapılandırma bölümünden kurar; bölüm değişince kurallar yeniden yüklenir (geçersiz yeni
    /// yapılandırma yüklenmez, eski kurallar sürer). Kod ile ek ayar (bölümleme seçicisi, özel red yanıtı) <paramref name="configure"/> ile.
    /// </summary>
    public static IServiceCollection AddAegisInboundRateLimiting(
        this IServiceCollection services, IConfigurationSection section, Action<AegisInboundRateLimitOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);

        // Somut tiple doğrudan Bind: yapılandırma bağlama kaynak üreticisi yakalar (Native AOT'de yansıma yok).
        services.Configure<AegisInboundRateLimitOptions>(options =>
        {
            section.Bind(options);
            configure?.Invoke(options);
        });
        services.AddSingleton<IOptionsChangeTokenSource<AegisInboundRateLimitOptions>>(
            new ConfigurationChangeTokenSource<AegisInboundRateLimitOptions>(Options.DefaultName, section));
        return AddInboundRateLimitingCore(services);
    }

    /// <summary>Gelen istek hız sınırlama ara katmanını ekler (kimlik doğrulamadan sonra; kullanıcıya göre bölümleme için).</summary>
    public static IApplicationBuilder UseAegisInboundRateLimiting(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<AegisInboundRateLimitingMiddleware>();
    }

    /// <summary>
    /// Uç nokta başına Aegis boru hattı ara katmanını ekler (<c>UseRouting</c>'den sonra). Boru hatları
    /// <c>AddAegisPipeline</c> ile kaydedilir, uç noktaya <see cref="RequireAegisPipeline{TBuilder}"/> ya da
    /// <see cref="AegisInboundPipelineAttribute"/> ile bağlanır.
    /// </summary>
    public static IApplicationBuilder UseAegisInboundPipelines(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<AegisInboundPipelineMiddleware>();
    }

    /// <summary>Uç noktayı adlandırılmış Aegis boru hattından geçirir (ör. <c>app.MapGet(...).RequireAegisPipeline("rapor")</c>).</summary>
    public static TBuilder RequireAegisPipeline<TBuilder>(this TBuilder builder, string pipelineName)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(pipelineName);
        return builder.WithMetadata(new AegisInboundPipelineAttribute(pipelineName));
    }

    private static IServiceCollection AddInboundRateLimitingCore(IServiceCollection services)
    {
        services.TryAddSingleton<IDistributedRateLimitStore>(_ => new InMemoryDistributedRateLimitStore());
        return services;
    }
}
