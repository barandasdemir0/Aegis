using System.Collections.Concurrent;
using Aegis.Resilience.Core.Abstractions;

namespace Aegis.Resilience.Extensions.DependencyInjection;

/// <summary>
/// Sistem genelinde yapılandırılan adlandırılmış boru hatlarını (Pipelines) yöneten ve çözümleyen kayıt defteri arayüzü.
/// </summary>
public interface IAegisPipelineRegistry
{
    IAegisPipeline GetPipeline(string name);
    bool TryGetPipeline(string name, out IAegisPipeline? pipeline);
    void RegisterPipeline(string name, IAegisPipeline pipeline);
    IReadOnlyDictionary<string, IAegisPipeline> GetAllPipelines();
}
