using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Extensions.DependencyInjection;

namespace Aegis.Resilience.Extensions.HealthChecks;

/// <summary>
/// Aegis sağlık denetim seçenekleri.
/// </summary>
public sealed class AegisHealthCheckOptions
{
    /// <summary>
    /// Circuit Breaker açık veya izole olduğunda bildirilecek sağlık durumu.
    /// Kubernetes ortamında Liveness probunun kaskad çöküşe (restart döngüsüne) girmemesi için
    /// varsayılan değer kesinlikle 'HealthStatus.Degraded' veya 'HealthStatus.Healthy' olmalıdır (Asla Unhealthy olmamalıdır).
    /// </summary>
    public HealthStatus OpenCircuitStatus { get; set; } = HealthStatus.Degraded;

    /// <summary>
    /// Sadece belirli kritik boru hatlarının Circuit Breaker durumunu denetlemek için opsiyonel filtre.
    /// Belirtilmezse tüm boru hatları denetlenir.
    /// </summary>
    public Func<string, bool>? PipelineFilter { get; set; }

    /// <summary>
    /// Dağıtık devre kesicinin paylaşılan deposuna (ör. Redis) ulaşılamadığında bildirilecek durum (AEGIS-160).
    /// Bu durumda uygulama çalışmaya devam eder ama pod'lar devre durumunu paylaşmaz (pod-yerel mod); yanlış
    /// yapılandırma veya kesinti sessiz kalmasın diye varsayılan <see cref="HealthStatus.Degraded"/>'dır (pod yeniden başlatılmaz).
    /// </summary>
    public HealthStatus SharedStateUnavailableStatus { get; set; } = HealthStatus.Degraded;
}
