using System.Collections.Concurrent;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using Aegis.Resilience.Core.Abstractions;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// HTTP işleyicileri (DelegatingHandler) tarafından paylaşılan dayanıklılık yürütme çekirdeği. Akışı yönetir: bağlamı kiralar,
/// gövdeyi tekrar oynatılabilir yapar, gerekirse ek denemeleri bastırır ve boru hattını çalıştırır. Tek bir denemenin
/// gönderimi <see cref="HttpAttempts"/>'tadır (AEGIS-103 / AEGIS-068).
/// </summary>
internal static class HttpResilienceExecutor
{
    internal static Task<HttpResponseMessage> ExecuteAsync(
        IAegisPipeline pipeline,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        AegisHttpSender sendAsync,
        HttpHandlerRules rules,
        long maxRequestBodySize = HttpRequestReplayHandler.DefaultMaxRequestBodySize) =>
        ExecuteAsync(pipeline, request, cancellationToken, sendAsync, rules, default(NoLease), maxRequestBodySize);

    /// <summary>
    /// İsteği boru hattında yürütür. Tek async katmandır: bağlam kiralama, yeniden yüklenebilir işleyicinin nesil kirası
    /// (<paramref name="lease"/>) ve yürütme aynı durum makinesinde (istek başına ek görev tahsisi yok).
    /// </summary>
    internal static async Task<HttpResponseMessage> ExecuteAsync<TLease>(
        IAegisPipeline pipeline,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        AegisHttpSender sendAsync,
        HttpHandlerRules rules,
        TLease lease,
        long maxRequestBodySize = HttpRequestReplayHandler.DefaultMaxRequestBodySize)
        where TLease : struct, IDisposable
    {
        using var _ = lease;
        // İstekte kullanıcının bağlamı yoksa havuzdan kiralanır, istek bitince iade edilir (Microsoft ResilienceHandler ile aynı).
        var context = request.RentAegisContext(pipeline.Name, cancellationToken, out var ownsContext);
        var suppression = default(AttemptSuppression);
        HttpAttempts? attempts = null;
        try
        {
            var isBodyReplayable = TryBufferBody(request, maxRequestBodySize, context.CancellationToken, out var buffering);
            if (buffering is not null)
            {
                isBodyReplayable = await buffering.ConfigureAwait(false);
            }

            // Yeniden gönderilmesi güvenli olmayan istekte Retry/Hedging ek deneme üretmez: ilk denemenin GERÇEK sonucu veya
            // istisnası (ör. bağlantı koptu -> HttpRequestException) olduğu gibi yükselir.
            if (!isBodyReplayable || !rules.AllowsResend(request))
            {
                suppression = AttemptSuppression.Apply(context);
            }

            attempts = new HttpAttempts(request, sendAsync, rules, maxRequestBodySize, isBodyReplayable);

            // Durumlu, statik geri çağrı: istek başına closure ve delegate tahsisi yok.
            return await pipeline.ExecuteAsync(static (ctx, a) => a.SendAsync(ctx), attempts, context).ConfigureAwait(false);
        }
        catch (HttpConnectionTimeoutException ex)
        {
            // Bağlantı zaman aşımı denemeler arasında geçici hata olarak taşındı; denemeler tükenince özgün iptal geri verilir.
            ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
            throw;
        }
        catch (HttpRequestException ex) when (attempts is not null && attempts.TryTakeHeldResponse(ex, out var finalResponse))
        {
            return finalResponse;
        }
        finally
        {
            attempts?.ReleaseHeldResponses();
            suppression.Restore(context); // kullanıcı aynı bağlamı başka isteklerde de kullanabilir; işaret sızmamalı
            request.ReleaseAegisContext(context, ownsContext);
        }
    }

    internal static bool IsGrpc(HttpRequestMessage request) =>
        request.Content?.Headers.ContentType?.MediaType is { } mediaType &&
        mediaType.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gövde varsa (POST, PUT vb.) birden fazla denemede akış tükenmesin diye belleğe alınır. Sınırı aşan gövde tamponlanmaz;
    /// o istek yeniden denenemez (false) ama tek denemeyle gönderilir. Tamponlama gerekiyorsa <paramref name="buffering"/>
    /// beklenir ve sonucu (yeniden oynatılabilir mi) kullanılır.
    /// </summary>
    private static bool TryBufferBody(HttpRequestMessage request, long maxRequestBodySize, CancellationToken cancellationToken, out Task<bool>? buffering)
    {
        buffering = null;
        if (request.Content is not { } content)
        {
            return true;
        }

        if (content.Headers.ContentLength > maxRequestBodySize)
        {
            return false;
        }

        // Uzunluğu bilinmeyen StreamContent geri sarılamayan akıştır: LoadIntoBuffer sınırda kısmen okuyup gövdeyi kaybederdi.
        buffering = content.Headers.ContentLength is null && content is StreamContent
            ? TryBufferStreamAsync(request, content, maxRequestBodySize, cancellationToken)
            : TryBufferContentAsync(content, maxRequestBodySize, cancellationToken);
        return true;
    }

    // Akış sınır+1 bayta kadar okunur. Sığarsa içerik bellekteki kopyayla değişir (yeniden oynatılabilir); sığmazsa okunan baş
    // kısım + akışın kalanı tek denemede gönderilir (yeniden denenemez ama gövde eksiksiz).
    private static async Task<bool> TryBufferStreamAsync(HttpRequestMessage request, HttpContent content, long maxRequestBodySize, CancellationToken cancellationToken)
    {
#if NET
        var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
        var source = await content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
        var prefix = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while (prefix.Length <= maxRequestBodySize &&
               (read = await ReadChunkAsync(source, chunk, (int)Math.Min(chunk.Length, maxRequestBodySize + 1 - prefix.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            prefix.Write(chunk, 0, read);
        }

        prefix.Position = 0;
        var fits = prefix.Length <= maxRequestBodySize;
        var replacement = fits ? new StreamContent(prefix) : new StreamContent(new PrefixedReadStream(prefix, source));
        foreach (var header in content.Headers)
        {
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        request.Content = replacement;
        if (fits)
        {
            content.Dispose(); // kaynak akış tümüyle kopyalandı
        }

        return fits;
    }

#if NET
    private static ValueTask<int> ReadChunkAsync(Stream source, byte[] chunk, int count, CancellationToken cancellationToken) =>
        source.ReadAsync(chunk.AsMemory(0, count), cancellationToken);
#else
    private static ValueTask<int> ReadChunkAsync(Stream source, byte[] chunk, int count, CancellationToken cancellationToken) =>
        new(source.ReadAsync(chunk, 0, count, cancellationToken));
#endif

    // Uzunluğu önceden bilinmeyen diğer içerik (ör. JsonContent) serileştirilirken sınırı aşabilir: istek düşmez, yeniden
    // denenemez sayılır (bu içerikler gönderimde yeniden serileştirilir).
    private static async Task<bool> TryBufferContentAsync(HttpContent content, long maxRequestBodySize, CancellationToken cancellationToken)
    {
        try
        {
            await BufferContentAsync(content, maxRequestBodySize, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (HttpRequestException) when (content.Headers.ContentLength is null)
        {
            return false;
        }
    }

    /// <summary>
    /// İstek gövdesini belirtilen üst sınıra kadar belleğe tamponlar.
    /// <c>LoadIntoBufferAsync</c> metodunun <see cref="CancellationToken"/> alan aşırı yüklemesi
    /// yalnızca .NET 9 ve üzerinde bulunduğundan çerçeveye göre ayrıştırılır.
    /// </summary>
    internal static Task BufferContentAsync(HttpContent content, long maxBufferSize, CancellationToken cancellationToken)
    {
#if NET9_0_OR_GREATER
        return content.LoadIntoBufferAsync(maxBufferSize, cancellationToken);
#else
        cancellationToken.ThrowIfCancellationRequested();
        return content.LoadIntoBufferAsync(maxBufferSize);
#endif
    }

    /// <summary>
    /// RFC 9110 ve finansal idempotency standartlarına göre isteğin güvenle tekrarlanabilir olup olmadığını denetler.
    /// </summary>
    internal static bool IsIdempotent(HttpRequestMessage request)
    {
        var method = request.Method;
        if (method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Options ||
            method == HttpMethod.Trace || method == HttpMethod.Put || method == HttpMethod.Delete)
        {
            return true;
        }

        // FinTech standardı: Idempotency-Key veya X-Idempotency-Key başlığı
        return request.Headers.Contains("Idempotency-Key") || request.Headers.Contains("X-Idempotency-Key");
    }
}
