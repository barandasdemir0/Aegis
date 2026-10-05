using Grpc.AspNetCore.Server;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;

namespace Aegis.Resilience.Grpc.AspNetCore;

/// <summary>gRPC sunucu kaydı: <c>services.AddGrpc(o =&gt; o.AddAegisResilience(p =&gt; p.AddConcurrencyLimiter(100)))</c>.</summary>
public static class AegisGrpcServiceOptionsExtensions
{
    /// <summary>
    /// Tüm gRPC servislerine Aegis sunucu korumasını ekler (bkz. <see cref="AegisGrpcServerInterceptor"/>). Boru hattı tektir
    /// (eşzamanlılık sayacı ve devre durumu tüm çağrılar arasında paylaşılır); Retry ve Hedging içeremez.
    /// </summary>
    public static GrpcServiceOptions AddAegisResilience(this GrpcServiceOptions options, Action<IAegisPipelineBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new AegisPipelineBuilder("grpc-sunucu");
        configure(builder);
        options.Interceptors.Add<AegisGrpcServerInterceptor>(builder.Build());
        return options;
    }
}
