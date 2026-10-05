using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// Bir isteğin denemeleri: boru hattının her denemede çağırdığı gönderim ve denemeler arası durum (deneme sayacı,
/// <c>ReturnFinalResponse</c> için saklanan yanıtlar).
/// </summary>
internal sealed class HttpAttempts(
    HttpRequestMessage request,
    AegisHttpSender sendAsync,
    HttpHandlerRules rules,
    long maxRequestBodySize,
    bool isBodyReplayable)
{
    private int _attemptCount;

    // İsteğin ilk denemeden ÖNCEKİ başlık sayısı. Zincirdeki iç işleyiciler (imza, korelasyon kimliği) her denemede başlık
    // ekleyebilir; klon yalnızca özgün başlıkları alır ki başlıklar denemeler arasında çoğalmasın (Microsoft aynı isteği yeniden
    // gönderdiği için orada çoğalır). Sayaç .NET 6+ üzerinde tahsissizdir; eski hedeflerde tüm başlıklar kopyalanır.
#if AEGIS_LEGACY
    private readonly int _originalHeaderCount = int.MaxValue;
#else
    private readonly int _originalHeaderCount = request.Headers.NonValidated.Count;
#endif

    // ReturnFinalResponse: geçici yanıt yine istisnaya çevrilir (retry/devre kesici aynen tetiklenir) ama dispose edilmez;
    // istisnayla eşlenip saklanır. Boru hattı bu istisnalardan biriyle biterse onun yanıtı döner. Kalanlar (önceki denemeler,
    // kaybeden hedging akışları) her durumda bırakılır. Kapalıyken yol ve istisna tipi değişmez.
    private readonly ConcurrentDictionary<Exception, HttpResponseMessage>? _heldResponses =
        rules.ReturnFinalResponse ? new ConcurrentDictionary<Exception, HttpResponseMessage>() : null;

    /// <summary>Tek bir deneme. <c>async ValueTask</c>: iç gönderim eşzamanlı tamamlanırsa durum makinesi kutulanmaz.</summary>
    public async ValueTask<HttpResponseMessage> SendAsync(AegisContext ctx)
    {
        var attempt = Interlocked.Increment(ref _attemptCount);
        if (attempt > 1)
        {
            EnsureResendAllowed();
        }

        // İlk denemede orijinal istek kullanılır; sonraki denemelerde (ve paralel hedging akışlarında) aynı HttpRequestMessage
        // örneğinin tekrar gönderilmemesi için klon üretilir.
        var requestToSend = attempt == 1
            ? request
            : await HttpRequestReplayHandler.CloneRequestAsync(request, maxRequestBodySize, _originalHeaderCount, ctx.CancellationToken).ConfigureAwait(false);

        HttpResponseMessage response;
        try
        {
            response = await sendAsync(requestToSend, ctx.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (AegisHttpTransientErrors.IsConnectionTimeout(ex, ctx.CancellationToken))
        {
            // Bağlantı kurma zaman aşımı iptal değil geçici hatadır (Microsoft ile aynı); OCE olarak kalsaydı Retry ve devre
            // kesici onu "iptal" sayıp hiç denemezdi.
            throw new HttpConnectionTimeoutException(ex);
        }
        finally
        {
            if (attempt > 1)
            {
                requestToSend.Dispose();
            }
        }

        return IsFinal(response, ctx) ? response : throw ToTransientFailure(response, ctx);
    }

    /// <summary>Boru hattı saklanan bir yanıtın istisnasıyla bittiyse o yanıtı alır (ReturnFinalResponse).</summary>
    public bool TryTakeHeldResponse(HttpRequestException exception, out HttpResponseMessage response)
    {
        response = null!;
        return _heldResponses is not null && _heldResponses.TryRemove(exception, out response!);
    }

    /// <summary>Alınmayan saklı yanıtları bırakır (önceki denemeler, kaybeden hedging akışları).</summary>
    public void ReleaseHeldResponses()
    {
        if (_heldResponses is null)
        {
            return;
        }

        foreach (var response in _heldResponses.Values)
        {
            response.Dispose();
        }
    }

    // Son savunma hattı (AEGIS-103): normalde SuppressAdditionalAttempts işareti ek denemeyi hiç başlatmaz; işareti tanımayan
    // özel bir strateji yine de ikinci deneme isterse mükerrer ağ çağrısı yapılmaz.
    private void EnsureResendAllowed()
    {
        if (!rules.AllowsResend(request))
        {
            throw new InvalidOperationException(
                $"HTTP isteği ({request.Method}) yeniden gönderilemez: yöntem yeniden denemeye kapatılmış ya da istek idempotent değil ve 'Idempotency-Key' başlığı yok (Finansal / Çift İşlem Koruması - AEGIS-103).");
        }

        if (!isBodyReplayable)
        {
            throw new InvalidOperationException(
                $"İstek gövdesi izin verilen yeniden deneme tampon sınırını ({maxRequestBodySize} bayt) aştığı için istek yeniden denenemez.");
        }
    }

    // Yanıt çağırana olduğu gibi döner mi: başarılı/kalıcı kod, geçici kod işlenmiyor ya da istek yeniden gönderilemiyor.
    private bool IsFinal(HttpResponseMessage response, AegisContext ctx) =>
        !rules.HandleHttpFailureStatuses ||
        !AegisHttpTransientErrors.IsTransient(response.StatusCode) ||
        AegisContextKeys.AreAdditionalAttemptsSuppressed(ctx);

    // Geçici yanıtı istisnaya çevirir (Retry/devre kesici tetiklensin); Retry-After bağlama yazılır, yanıt saklanır ya da bırakılır.
    private HttpRequestException ToTransientFailure(HttpResponseMessage response, AegisContext ctx)
    {
        if (HttpRetryAfterHelper.TryParse(response, out var retryAfterDelay))
        {
            ctx.Properties[HttpRetryAfterHelper.RetryAfterPropertyKey] = retryAfterDelay;
        }

        var exception = CreateTransientStatusException(response.StatusCode);
        if (_heldResponses is null)
        {
            response.Dispose();
        }
        else
        {
            _heldResponses[exception] = response;
        }

        return exception;
    }

    private static HttpRequestException CreateTransientStatusException(HttpStatusCode statusCode) =>
#if AEGIS_LEGACY
        // .NET Framework / netstandard2.0'da HttpRequestException durum kodu taşımaz; kod mesajdadır.
        new($"HTTP isteği geçici hata kodu ({(int)statusCode} {statusCode}) ile döndü.");
#else
        new(message: $"HTTP isteği geçici hata kodu ({statusCode}) ile döndü.", inner: null, statusCode: statusCode);
#endif
}
