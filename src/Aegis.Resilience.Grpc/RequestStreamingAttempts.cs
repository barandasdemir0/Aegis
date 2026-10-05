using Grpc.Core;
using Grpc.Core.Interceptors;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Grpc;

/// <summary>
/// İstemci akışı ve çift yönlü akış denemeleri. Her deneme tampondaki mesajları baştan gönderir, sonra canlı mesajları iletir
/// (gRPC A6). İstemci akışında deneme yanıtla biter; çift yönlü akışta ilk yanıt mesajıyla commit olur ve istek pompası
/// commit olan çağrıda sürer.
/// </summary>
internal sealed class RequestStreamingAttempts<TRequest, TResponse>(
    ClientInterceptorContext<TRequest, TResponse> call, ReplayBuffer<TRequest> buffer, CancellationToken callToken)
    where TRequest : class
    where TResponse : class
{
    private int _attempts;

    public Interceptor.AsyncClientStreamingCallContinuation<TRequest, TResponse>? ClientStreaming { get; init; }

    public Interceptor.AsyncDuplexStreamingCallContinuation<TRequest, TResponse>? Duplex { get; init; }

    /// <summary>İstemci akışı denemesi: istek pompası + yanıt.</summary>
    public async ValueTask<ClientStreamingResult<TRequest, TResponse>> SendAsync(AegisContext context)
    {
        var previousAttempts = NextAttempt();
        var attempt = ClientStreaming!(NextContext(context.CancellationToken, previousAttempts));
        // Pompa beklenmez: sunucu istek akışı bitmeden yanıt verebilir; bekleyen yazmalar çağrı bitince serbest bırakılır.
        _ = PumpAsync(attempt.RequestStream, context.CancellationToken);
        try
        {
            var response = await attempt.ResponseAsync.ConfigureAwait(false);
            return new ClientStreamingResult<TRequest, TResponse>(attempt, response);
        }
        catch (RpcException ex)
        {
            attempt.Dispose();
            throw Translate(ex, context);
        }
        catch
        {
            attempt.Dispose();
            throw;
        }
    }

    /// <summary>Çift yönlü akış denemesi: ilk yanıt mesajına kadar; sonra commit (pompa sürer, deneme süresi artık kesmez).</summary>
    public async ValueTask<StreamingResult<TResponse>> StartDuplexAsync(AegisContext context)
    {
        var previousAttempts = NextAttempt();
        var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(callToken);
        var untilCommit = context.CancellationToken.Register(static s => ((CancellationTokenSource)s!).Cancel(), attemptCts);
        var attempt = Duplex!(NextContext(attemptCts.Token, previousAttempts));
        _ = PumpAsync(attempt.RequestStream, attemptCts.Token);
        try
        {
            var hasFirst = await attempt.ResponseStream.MoveNext(attemptCts.Token).ConfigureAwait(false);
            buffer.Commit(); // ilk yanıt: çağrı commit oldu
            untilCommit.Dispose();
            var call = new AsyncServerStreamingCall<TResponse>(
                attempt.ResponseStream, attempt.ResponseHeadersAsync, attempt.GetStatus, attempt.GetTrailers, attempt.Dispose);
            return new StreamingResult<TResponse>(call, attemptCts, hasFirst, hasFirst ? attempt.ResponseStream.Current : default);
        }
        catch (RpcException ex)
        {
            untilCommit.Dispose();
            attempt.Dispose();
            attemptCts.Dispose();
            throw Translate(ex, context);
        }
        catch
        {
            untilCommit.Dispose();
            attempt.Dispose();
            attemptCts.Dispose();
            throw;
        }
    }

    // Tampondaki mesajları baştan, sonra canlı mesajları gönderir; çağıran akışı tamamlayınca isteği tamamlar. Gönderim hatası
    // (çağrı başarısız) yutulur: hata yanıt tarafında görünür ve deneme oradan değerlendirilir.
    private async Task PumpAsync(IClientStreamWriter<TRequest> requestStream, CancellationToken cancellationToken)
    {
        try
        {
            if (buffer.WriteOptions is { } writeOptions)
            {
                requestStream.WriteOptions = writeOptions;
            }

            for (var index = 0; ; index++)
            {
                var (hasMessage, message) = await buffer.NextAsync(index, cancellationToken).ConfigureAwait(false);
                if (!hasMessage)
                {
                    await requestStream.CompleteAsync().ConfigureAwait(false);
                    return;
                }
#if NET

                await requestStream.WriteAsync(message!, cancellationToken).ConfigureAwait(false); // takılan yazma iptalde kesilir
#else

                await requestStream.WriteAsync(message!).ConfigureAwait(false);
#endif
                buffer.MarkWritten(index);
            }
        }
#pragma warning disable CA1031 // Pompa hatası çağrının hatasıdır; yanıt tarafında değerlendirilir.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    // Commit olmuş (tampon sınırı aşılmış) çağrı yeniden denenemez: hata yeniden deneme koşuluna takılmayan sarmalayıcıyla taşınır.
    private Exception Translate(RpcException exception, AegisContext context)
    {
        var translated = GrpcAttempt.Translate(exception, context);
        return buffer.IsCommitted && translated is RpcException ? new CommittedCallException(exception) : translated;
    }

    private int NextAttempt() => Interlocked.Increment(ref _attempts) - 1;

    private ClientInterceptorContext<TRequest, TResponse> NextContext(CancellationToken token, int previousAttempts) =>
        new(call.Method, call.Host, GrpcAttempt.Options(call.Options, token, previousAttempts));
}

/// <summary>Kazanan istemci akışı denemesi: yanıt ve onu üreten çağrı (başlık, durum ve trailer'lar buradan).</summary>
internal sealed class ClientStreamingResult<TRequest, TResponse>(AsyncClientStreamingCall<TRequest, TResponse> call, TResponse response) : IGrpcCallResult
{
    public AsyncClientStreamingCall<TRequest, TResponse> Call { get; } = call;

    public Task<Metadata> ResponseHeadersAsync => Call.ResponseHeadersAsync;

    public Status GetStatus() => Call.GetStatus();

    public Metadata GetTrailers() => Call.GetTrailers();

    public TResponse Response { get; } = response;

    public void Dispose() => Call.Dispose();
}
