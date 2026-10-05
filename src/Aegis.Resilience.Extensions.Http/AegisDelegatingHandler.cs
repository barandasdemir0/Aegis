using System.Net.Http;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// Aegis HTTP işleyicilerinin tabanı. İşleyici mantığı tek bir çekirdekte (<see cref="SendCoreAsync"/>) yazılır; hem
/// <c>SendAsync</c> hem senkron <c>HttpClient.Send</c> (.NET 5+) aynı çekirdekten geçer. Ezilmeyen <c>Send</c>, iç işleyicinin
/// <c>Send</c>'ini doğrudan çağırıp dayanıklılığı sessizce atlardı (Microsoft <c>ResilienceHandler.Send</c>'i de bu yüzden ezer).
/// <c>RemoveAllAegisHandlers()</c> Aegis işleyicilerini bu tabanla tanır.
/// </summary>
public abstract class AegisDelegatingHandler : DelegatingHandler
{
    private readonly AegisHttpSender _sendAsync;

    protected AegisDelegatingHandler()
    {
        _sendAsync = (request, cancellationToken) => base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// True ise işleyici aynı istek için eşzamanlı denemeler başlatır (hedging). Senkron <c>Send</c>'de her deneme iş
    /// parçacığı havuzunda çalıştırılır; aksi halde engelleyen birincil deneme bitmeden yedek deneme başlayamazdı.
    /// </summary>
    protected virtual bool RunsAttemptsConcurrently => false;

    /// <summary>İşleyici mantığı: <paramref name="innerSend"/> zincirdeki bir sonraki işleyiciye gönderir.</summary>
    protected abstract Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, AegisHttpSender innerSend, CancellationToken cancellationToken);

    /// <inheritdoc />
    protected sealed override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        HttpResilienceExecutor.IsGrpc(request) ? base.SendAsync(request, cancellationToken) : SendCoreAsync(request, _sendAsync, cancellationToken);

#if !AEGIS_LEGACY
    /// <summary>
    /// Senkron gönderim aynı çekirdekten geçer. Bekleme güvenlidir: Aegis içindeki tüm beklemeler yakalanan bağlama dönmez
    /// (<c>ConfigureAwait(false)</c>; <c>AegisContext.ContinueOnCapturedContext</c> varsayılanı false).
    /// </summary>
    protected sealed override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // gRPC isteği dokunulmadan geçer (bkz. SendAsync).
        if (HttpResilienceExecutor.IsGrpc(request))
        {
            return base.Send(request, cancellationToken);
        }

        AegisHttpSender innerSend = RunsAttemptsConcurrently
            ? (r, ct) => Task.Run(() => base.Send(r, ct), ct)
            : (r, ct) => Task.FromResult(base.Send(r, ct));
        return SendCoreAsync(request, innerSend, cancellationToken).GetAwaiter().GetResult();
    }
#endif
}
