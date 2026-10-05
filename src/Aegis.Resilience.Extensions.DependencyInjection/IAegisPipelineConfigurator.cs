using Aegis.Resilience.Core.Abstractions;

namespace Aegis.Resilience.Extensions.DependencyInjection;

public interface IAegisPipelineConfigurator
{
    string Name { get; }
    IAegisPipeline Build(IServiceProvider serviceProvider);
}
