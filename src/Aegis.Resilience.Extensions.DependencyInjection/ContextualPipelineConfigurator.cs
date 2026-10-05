using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Extensions.DependencyInjection.Telemetry;

namespace Aegis.Resilience.Extensions.DependencyInjection;

/// <summary>
/// <see cref="AegisPipelineContext"/> ile kurulan adlandırılmış boru hattı. Bağlam hiçbir yeniden kurma kaynağı ya da dispose
/// bildirimi toplamazsa düz boru hattı döner (sarmalayıcı maliyeti yok); toplarsa nesiller <see cref="ReloadableAegisPipeline"/>
/// ile yönetilir ve her neslin kaynakları kendi boru hattıyla birlikte bırakılır.
/// </summary>
internal sealed class ContextualPipelineConfigurator(string name, Action<IAegisPipelineBuilder, AegisPipelineContext> configure)
    : IAegisPipelineConfigurator
{
    public string Name { get; } = name;

    public IAegisPipeline Build(IServiceProvider serviceProvider)
    {
        ReloadableAegisPipeline? reloadable = null;
        void Reload() => reloadable?.Reload();

        var first = Create(serviceProvider, Reload, out var hasResources);
        if (!hasResources)
        {
            return first;
        }

        IAegisPipeline? pending = first; // ilk nesil yeniden kurulmasın
        reloadable = new ReloadableAegisPipeline(Name, () => Interlocked.Exchange(ref pending, null) ?? Create(serviceProvider, Reload, out _));
        return reloadable;
    }

    private IAegisPipeline Create(IServiceProvider serviceProvider, Action reload, out bool hasResources)
    {
        var context = new NamedPipelineContext(serviceProvider, Name, reload);
        var builder = new AegisPipelineBuilder(Name);
        configure(builder, context);
        AegisTelemetryConfiguration.Apply(builder, serviceProvider);
        var pipeline = builder.Build();

        hasResources = context.HasResources;
        return hasResources ? new OwnedResourcesPipeline(pipeline, context.Take()) : pipeline;
    }

    private sealed class NamedPipelineContext(IServiceProvider serviceProvider, string pipelineName, Action reload)
        : AegisPipelineContext(serviceProvider, pipelineName, reload)
    {
        public IDisposable Take() => TakeResources();
    }

    /// <summary>Boru hattı nesli + o nesle ait abonelikler ve dispose bildirimleri (birlikte bırakılır).</summary>
    private sealed class OwnedResourcesPipeline(IAegisPipeline inner, IDisposable resources) : IAegisPipeline
    {
        public string Name => inner.Name;

        public IReadOnlyList<IAegisStrategy> Strategies => inner.Strategies;

        public ValueTask<TResult> ExecuteAsync<TResult>(Func<AegisContext, ValueTask<TResult>> callback, AegisContext? context = null) =>
            inner.ExecuteAsync(callback, context);

        public ValueTask ExecuteAsync(Func<AegisContext, ValueTask> callback, AegisContext? context = null) =>
            inner.ExecuteAsync(callback, context);

        public void Dispose()
        {
            inner.Dispose();
            resources.Dispose();
        }
    }
}
