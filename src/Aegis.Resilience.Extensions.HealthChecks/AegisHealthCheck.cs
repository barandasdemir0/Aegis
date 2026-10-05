using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Extensions.DependencyInjection;

namespace Aegis.Resilience.Extensions.HealthChecks;

/// <summary>
/// Sistemdeki tüm Aegis boru hatlarının Circuit Breaker durumlarını denetleyip
/// Kubernetes Liveness/Readiness problarına güvenle raporlayan kurumsal HealthCheck sınıfı.
/// </summary>
public sealed class AegisHealthCheck : IHealthCheck
{
    private readonly IAegisPipelineRegistry _registry;
    private readonly AegisHealthCheckOptions _options;

    public AegisHealthCheck(IAegisPipelineRegistry registry, IOptions<AegisHealthCheckOptions>? options = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options?.Value ?? new AegisHealthCheckOptions();
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var pipelines = _registry.GetAllPipelines();
        var openCircuits = new Dictionary<string, object>();
        var sharedStateIssues = new Dictionary<string, object>();

        foreach (var (pipelineName, pipeline) in pipelines)
        {
            if (_options.PipelineFilter != null && !_options.PipelineFilter(pipelineName))
            {
                continue;
            }

            foreach (var strategy in pipeline.Strategies)
            {
                if (strategy is IObservableCircuitState cb &&
                    (cb.LastKnownState == CircuitState.Open || cb.LastKnownState == CircuitState.Isolated))
                {
                    openCircuits[pipelineName] = $"Devre kesici '{cb.LastKnownState}' durumunda!";
                }

                if (strategy is IObservableSharedState { IsSharedStateAvailable: false })
                {
                    sharedStateIssues[$"{pipelineName}:sharedState"] =
                        "Dağıtık durum deposuna ulaşılamıyor; devre kesici pod-yerel modda (pod'lar devre durumunu paylaşmıyor).";
                }
            }
        }

        if (openCircuits.Count > 0 || sharedStateIssues.Count > 0)
        {
            var messages = new List<string>(2);
            var status = HealthStatus.Healthy;
            if (openCircuits.Count > 0)
            {
                messages.Add($"Dikkat: {openCircuits.Count} adet kritik dış servisin devre kesicisi AÇIK durumda!");
                status = _options.OpenCircuitStatus;
            }

            if (sharedStateIssues.Count > 0)
            {
                messages.Add($"Dikkat: {sharedStateIssues.Count} adet dağıtık devre kesici paylaşılan depoya ulaşamıyor (pod-yerel mod)!");
                status = (HealthStatus)Math.Min((int)status, (int)_options.SharedStateUnavailableStatus); // en kötü durum
            }

            foreach (var issue in sharedStateIssues)
            {
                openCircuits[issue.Key] = issue.Value;
            }

            return Task.FromResult(new HealthCheckResult(status, string.Join(" ", messages), data: openCircuits));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Tüm Aegis devre kesicileri (Circuit Breakers) KAPALI ve sağlıklı."));
    }
}
