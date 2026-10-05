using System.Collections.Concurrent;
using System.Net.Http;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.DependencyInjection.Telemetry;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// İstek anahtarı başına (ör. hedef authority) ayrı DI bağlamlı boru hattı (Microsoft: <c>SelectPipelineBy</c>). Her anahtar
/// kendi bağlamıyla (<see cref="AegisHttpHandlerContext.InstanceName"/> = anahtar) ilk isteğinde kurulur; bir hostun devresi
/// açılınca diğer hostlar etkilenmez. Kurulum hatası önbelleğe alınmaz: sonraki istek yeniden dener.
/// </summary>
internal sealed class KeyedContextualHandlerRuntime(
    Func<string, ContextualHandlerRuntime> build,
    Func<HttpRequestMessage, string> selector,
    int maxPipelines) : IHttpHandlerRuntime
{
    private readonly ConcurrentDictionary<string, Lazy<ContextualHandlerRuntime>> _runtimes = new(StringComparer.OrdinalIgnoreCase);

    public HttpHandlerRoute Route(HttpRequestMessage request)
    {
        var key = selector(request) ?? string.Empty;
        if (!_runtimes.TryGetValue(key, out var runtime))
        {
            // Kardinalite koruması (saldırgan rastgele host/anahtarla bellek tüketemesin); eşzamanlı eklemelerde sınır en çok
            // eşzamanlı istek sayısı kadar aşılabilir.
            if (_runtimes.Count >= maxPipelines)
            {
                throw new InvalidOperationException(
                    $"Anahtar başına boru hattı sınırı ({maxPipelines}) aşıldı. Seçicinin kardinalitesini kontrol edin veya sınırı artırın.");
            }

            runtime = _runtimes.GetOrAdd(key, k => new Lazy<ContextualHandlerRuntime>(() => build(k)));
        }

        try
        {
            return runtime.Value.Route(request);
        }
        catch
        {
            ((ICollection<KeyValuePair<string, Lazy<ContextualHandlerRuntime>>>)_runtimes).Remove(new(key, runtime));
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var runtime in _runtimes.Values)
        {
            if (runtime.IsValueCreated)
            {
                runtime.Value.Dispose();
            }
        }
    }
}
