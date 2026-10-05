namespace Aegis.Resilience.Core.Abstractions;

/// <summary>
/// Dayanıklılık stratejilerini sıralı bir şekilde boru hattına ekleyen kurucu (builder) arayüzü.
/// </summary>
public interface IAegisPipelineBuilder
{
    string Name { get; }
    IReadOnlyList<IAegisStrategy> Strategies { get; }

    IAegisPipelineBuilder AddStrategy(IAegisStrategy strategy);
    IAegisPipeline Build();
}
