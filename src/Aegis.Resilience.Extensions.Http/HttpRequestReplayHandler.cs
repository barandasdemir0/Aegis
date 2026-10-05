using System.Net.Http;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// HTTP gövdesi (HttpContent) içeren POST/PUT/PATCH isteklerini arabelleğe alarak
/// Retry veya Hedging stratejilerinde 'Request already sent' veya akış tükenmesi hatası
/// olmaksızın güvenle yeniden gönderilmesini sağlayan DelegatingHandler (Microsoft.Extensions.Http.Resilience paritesi).
/// </summary>
public sealed class HttpRequestReplayHandler : AegisDelegatingHandler
{
    /// <summary>
    /// Belleğe tamponlanabilecek varsayılan maksimum istek gövdesi boyutu (10 MB).
    /// </summary>
    public const long DefaultMaxRequestBodySize = 10 * 1024 * 1024;

    private readonly long _maxRequestBodySize;

    public HttpRequestReplayHandler(long maxRequestBodySize = DefaultMaxRequestBodySize)
    {
        _maxRequestBodySize = maxRequestBodySize > 0 ? maxRequestBodySize : DefaultMaxRequestBodySize;
    }

    protected override async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, AegisHttpSender innerSend, CancellationToken cancellationToken)
    {
        if (request.Content != null)
        {
            // İçeriği belirlenen üst sınıra kadar belleğe tamponla (AEGIS-068: Bellek patlaması / OOM koruması)
            if (request.Content.Headers.ContentLength.HasValue && request.Content.Headers.ContentLength.Value > _maxRequestBodySize)
            {
                // Boyut sınırını aşan dev akışlar belleğe tamponlanmaz; istek olduğu gibi iletilir
            }
            else
            {
                await HttpResilienceExecutor.BufferContentAsync(request.Content, _maxRequestBodySize, cancellationToken).ConfigureAwait(false);
            }
        }

        return await innerSend(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Yeniden deneme (Retry) veya alternatif uç noktaya iletim anında yeni ve temiz bir HttpRequestMessage kopyası üretir.
    /// </summary>
    public static Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage req, CancellationToken ct = default)
        => CloneRequestAsync(req, DefaultMaxRequestBodySize, ct);

    /// <summary>
    /// Yeniden deneme (Retry) veya alternatif uç noktaya iletim anında yeni ve temiz bir HttpRequestMessage kopyası üretir.
    /// </summary>
    public static Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage req,
        long maxRequestBodySize,
        CancellationToken ct = default) =>
        CloneRequestAsync(req, maxRequestBodySize, int.MaxValue, ct);

    /// <summary>Klon; başlıklardan yalnızca ilk <paramref name="originalHeaderCount"/> tanesi alınır (bkz. <see cref="HttpAttempts"/>).</summary>
    internal static async Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage req,
        long maxRequestBodySize,
        int originalHeaderCount,
        CancellationToken ct)
    {
        var clone = HttpRequestMessageCompat.CopyWithoutContent(req, req.RequestUri, originalHeaderCount);

        if (req.Content != null)
        {
            clone.Content = await CopyContentAsync(req.Content, maxRequestBodySize, ct).ConfigureAwait(false);
        }

        return clone;
    }

    /// <summary>
    /// Gövdeyi belleğe kopyalayıp başlıklarıyla birlikte yeni bir içerik üretir; böylece her deneme kendi akışını okur.
    /// Sınırı aşan gövde (bildirilen ya da okunan boyut) <see cref="InvalidOperationException"/> fırlatır.
    /// </summary>
    internal static async Task<StreamContent> CopyContentAsync(HttpContent source, long maxRequestBodySize, CancellationToken ct)
    {
        if (source.Headers.ContentLength.HasValue && source.Headers.ContentLength.Value > maxRequestBodySize)
        {
            throw new InvalidOperationException(
                $"İstek gövdesi boyutu ({source.Headers.ContentLength.Value} bayt), izin verilen maksimum yeniden deneme tamponu üst sınırını ({maxRequestBodySize} bayt) aşıyor.");
        }

        var ms = new MemoryStream();
        await source.CopyToAsync(ms, ct).ConfigureAwait(false);

        if (ms.Length > maxRequestBodySize)
        {
            throw new InvalidOperationException(
                $"Okunan istek akış boyutu ({ms.Length} bayt), izin verilen maksimum yeniden deneme tamponu üst sınırını ({maxRequestBodySize} bayt) aşıyor.");
        }

        ms.Position = 0;
        var copy = new StreamContent(ms);
        foreach (var header in source.Headers)
        {
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return copy;
    }
}
