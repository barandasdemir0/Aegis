using Aegis.Resilience.Core.Abstractions;

namespace Aegis.Resilience.Extensions.DependencyInjection;

public sealed class DelegateAegisPipelineConfigurator : IAegisPipelineConfigurator
{
    private readonly Action<IAegisPipelineBuilder, IServiceProvider> _configure;

    public string Name { get; }

    public DelegateAegisPipelineConfigurator(string name, Action<IAegisPipelineBuilder, IServiceProvider> configure)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _configure = configure ?? throw new ArgumentNullException(nameof(configure));
    }

    public IAegisPipeline Build(IServiceProvider serviceProvider)
    {
        var builder = new Aegis.Resilience.Core.Pipeline.AegisPipelineBuilder(Name);
        _configure(builder, serviceProvider);
        Telemetry.AegisTelemetryConfiguration.Apply(builder, serviceProvider); // DI varsayılanları: ILogger dinleyicisi + ConfigureAegisTelemetry
        return builder.Build();
    }
}
