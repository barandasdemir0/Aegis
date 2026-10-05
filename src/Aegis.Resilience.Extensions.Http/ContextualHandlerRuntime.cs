using System.Collections.Concurrent;
using System.Net.Http;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.DependencyInjection.Telemetry;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// DI bağlamlı işleyicinin bir nesli: boru hattı, HTTP kuralları ve bağlamın kaynakları (yeniden yükleme abonelikleri,
/// dispose bildirimleri). Nesil emekliye ayrılıp boşalınca (ya da uygulama kapanınca) hepsi birlikte bırakılır.
/// </summary>
internal sealed class ContextualHandlerRuntime(IAegisPipeline pipeline, HttpHandlerRules rules, IDisposable resources) : IHttpHandlerRuntime
{
    public HttpHandlerRoute Route(HttpRequestMessage request) => new(pipeline, rules);

    /// <summary>Bir nesil kurar; <paramref name="reload"/> sonraki nesilleri kurdurmak için bağlama verilir.</summary>
    public static ContextualHandlerRuntime Build(
        IServiceProvider serviceProvider,
        string clientName,
        string? instanceName,
        Action<IAegisPipelineBuilder, AegisHttpHandlerContext> configure,
        Action reload)
    {
        var context = new AegisHttpHandlerContext(serviceProvider, clientName, instanceName, reload);
        var builder = new AegisPipelineBuilder(context.PipelineName) { InstanceName = instanceName };
        configure(builder, context);
        AegisTelemetryConfiguration.Apply(builder, serviceProvider);
        return context.ToRuntime(builder.Build());
    }

    public void Dispose()
    {
        pipeline.Dispose();
        resources.Dispose();
    }
}
