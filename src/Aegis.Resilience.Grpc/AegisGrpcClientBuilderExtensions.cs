using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;

namespace Aegis.Resilience.Grpc;

/// <summary>
/// Grpc.Net.ClientFactory entegrasyonu: <c>services.AddGrpcClient&lt;T&gt;(...).AddStandardAegisGrpcResilience()</c>.
/// HTTP katmanındaki <c>AddStandardResilienceHandler</c> / <c>AddStandardAegisHandler</c> yerine bunu kullanın: dayanıklılık gRPC
/// katmanında, durum kodunu görerek çalışır.
/// </summary>
public static class AegisGrpcClientBuilderExtensions
{
    /// <summary>Standart gRPC dayanıklılık zincirini istemciye ekler (bkz. <see cref="AegisGrpcStandardResilienceOptions"/>).</summary>
    public static IHttpClientBuilder AddStandardAegisGrpcResilience(
        this IHttpClientBuilder builder, Action<AegisGrpcStandardResilienceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new AegisGrpcStandardResilienceOptions();
        configure?.Invoke(options);
        options.Validate(); // fail-fast: geçersiz ayar uygulama açılırken bildirilir
        return builder.AddAegisGrpcResilience(options.Configure);
    }

#if NET
    /// <summary>
    /// Sunucu (uç nokta) ayıklama: art arda sunucu hatası veren uç nokta havuzdan geçici olarak çıkarılır (Envoy outlier detection).
    /// Grpc.Net.Client yük dengelemesi gerektirir: birden çok adres döndüren bir çözümleyici (ör. <c>dns:///</c>, <c>static:///</c>).
    /// Yerleşik yük dengeleyici geri bildirimi yalnızca bağlantı hatasını görür; Aegis <c>grpc-status</c> trailer'ını da sayar.
    /// Dedektör uygulama genelinde tektir (aynı sunucuya giden tüm istemciler aynı kararı paylaşır).
    /// </summary>
    public static IHttpClientBuilder AddAegisGrpcOutlierDetection(
        this IHttpClientBuilder builder, Action<AegisOutlierDetectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new AegisOutlierDetectionOptions();
        configure?.Invoke(options);
        options.Validate();

        builder.Services.TryAddSingleton(_ => new AegisOutlierDetector(options));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<global::Grpc.Net.Client.Balancer.LoadBalancerFactory, AegisOutlierDetectionLoadBalancerFactory>());
        builder.ConfigureChannel(static channel =>
        {
            channel.ServiceConfig ??= new global::Grpc.Net.Client.Configuration.ServiceConfig();
            if (!channel.ServiceConfig.LoadBalancingConfigs.Any(static c => c.PolicyName == AegisOutlierDetectionLoadBalancerFactory.PolicyName))
            {
                channel.ServiceConfig.LoadBalancingConfigs.Add(new global::Grpc.Net.Client.Configuration.LoadBalancingConfig(AegisOutlierDetectionLoadBalancerFactory.PolicyName));
            }
        });
        return builder.AddHttpMessageHandler(sp => new AegisOutlierStatusHandler(sp.GetRequiredService<AegisOutlierDetector>()));
    }
#endif

    /// <summary>Özel boru hattını gRPC istemcisine ekler (unary ve sunucu akışı; bkz. <see cref="AegisGrpcClientInterceptor"/>).</summary>
    public static IHttpClientBuilder AddAegisGrpcResilience(
        this IHttpClientBuilder builder, Action<IAegisPipelineBuilder> configure, AegisGrpcClientOptions? clientOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        // Boru hattı (devre kesici durumu, eşzamanlılık sayacı) istemci adı başına tektir; interceptor her istemci örneğinde paylaşılır.
        var pipeline = new Lazy<IAegisPipeline>(() =>
        {
            var pipelineBuilder = new AegisPipelineBuilder($"{builder.Name}_AegisGrpc");
            configure(pipelineBuilder);
            return pipelineBuilder.Build();
        });
        return builder.AddInterceptor(_ => new AegisGrpcClientInterceptor(pipeline.Value, clientOptions));
    }
}
