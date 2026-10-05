using System.Net.Http;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// Yeniden yüklenebilen işleyici çekirdeği: her istek güncel nesli kiralar (nesil istek bitene kadar dispose edilmez),
/// boru hattını seçer ve Aegis HTTP yürütücüsüyle çalıştırır (geçici durum kodları, idempotency koruması, gövde tekrar
/// oynatma, Retry-After). Standart işleyici ve DI bağlamlı özel işleyici bunu paylaşır.
/// </summary>
internal sealed class ReloadableResilienceHandler<TRuntime>(Reloadable<TRuntime> runtime) : AegisDelegatingHandler
    where TRuntime : class, IHttpHandlerRuntime
{
    protected override Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, AegisHttpSender innerSend, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Nesil kirası yürütücüye devredilir: istek bitince orada bırakılır (ayrı async katman ve görev tahsisi yok).
        var lease = runtime.Acquire();
        HttpHandlerRoute route;
        try
        {
            route = lease.Value.Route(request);
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        return HttpResilienceExecutor.ExecuteAsync(route.Pipeline, request, cancellationToken, innerSend, route.Rules, lease);
    }
}
