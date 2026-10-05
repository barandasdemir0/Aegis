using System.Collections.Concurrent;
using System.Net.Http;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.DependencyInjection.Telemetry;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// DI bağlamlı HTTP işleyicisinin kurulum bağlamı (Microsoft: <c>ResilienceHandlerContext</c>). Servis sağlayıcı, seçenekler,
/// yeniden kurma (<c>EnableReloads</c>, <c>AddReloadToken</c>) ve dispose bildirimi <see cref="AegisPipelineContext"/>'tendir;
/// bu sınıf HTTP kurallarını ve istemci/anahtar bilgisini ekler. Ayarlar o kurulumun (nesil) boru hattına aittir.
/// </summary>
public sealed class AegisHttpHandlerContext : AegisPipelineContext
{
    internal AegisHttpHandlerContext(IServiceProvider serviceProvider, string clientName, string? instanceName, Action reload)
        : base(serviceProvider, clientName + "_AegisPipeline", reload)
    {
        ClientName = clientName;
        InstanceName = instanceName;
    }

    /// <summary>İşleyicinin eklendiği HttpClient'ın adı (Microsoft: <c>BuilderName</c>).</summary>
    public string ClientName { get; }

    /// <summary>
    /// İstek anahtarı başına boru hattında anahtar (Microsoft: <c>InstanceName</c>; ör. <c>SelectPipelineByAuthority</c> ile
    /// hedef authority). Boru hattının <c>InstanceName</c>'i olarak telemetriye de yazılır. Tek boru hattında null.
    /// </summary>
    public string? InstanceName { get; }

    /// <summary>5xx, 408, 429 yanıtlarını geçici hata say (varsayılan: evet).</summary>
    public bool HandleHttpFailureStatuses { get; set; } = true;

    /// <summary>Idempotent olmayan (Idempotency-Key'siz POST/PATCH) istekleri de yeniden dene (varsayılan: hayır).</summary>
    public bool AllowNonIdempotentRetry { get; set; }

    /// <inheritdoc cref="AegisHttpStandardResilienceOptions.ReturnFinalResponse"/>
    public bool ReturnFinalResponse { get; set; }

    internal HashSet<HttpMethod> RetryDisabledMethods { get; } = [];

    /// <summary>
    /// Verilen yöntemlerde yeniden denemeyi tamamen kapatır (Microsoft: <c>DisableFor(...)</c>). Açık kapatma her kuraldan
    /// önce gelir: <c>Idempotency-Key</c> başlığı ve <see cref="AllowNonIdempotentRetry"/> bu yöntemleri açmaz.
    /// </summary>
    public AegisHttpHandlerContext DisableRetryFor(params HttpMethod[] methods)
    {
        HttpHandlerRules.AddDisabled(RetryDisabledMethods, methods);
        return this;
    }

    /// <summary>Güvensiz yöntemlerde (POST, PATCH, PUT, DELETE, CONNECT) yeniden denemeyi kapatır (Microsoft: <c>DisableForUnsafeHttpMethods()</c>).</summary>
    public AegisHttpHandlerContext DisableRetryForUnsafeHttpMethods() => DisableRetryFor(HttpHandlerRules.UnsafeMethods);

    /// <summary>Bağlamın topladığı ayarlarla bir işleyici nesli oluşturur (bağlam bundan sonra kullanılmaz).</summary>
    internal ContextualHandlerRuntime ToRuntime(IAegisPipeline pipeline) =>
        new(pipeline,
            HttpHandlerRules.Create(HandleHttpFailureStatuses, AllowNonIdempotentRetry, RetryDisabledMethods, ReturnFinalResponse),
            TakeResources());
}
