using Grpc.Core;
using Grpc.Core.Interceptors;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Grpc;

/// <summary>
/// Sunucu akışlı çağrının denemeleri. gRPC A6 commit kuralı: ilk yanıt mesajı alınana kadar yeniden denenebilir; ilk mesajla
/// çağrı commit olur ve akışın geri kalanı çağırana olduğu gibi verilir (sonraki hatalar yeniden denenmez).
/// </summary>
internal sealed class ServerStreamingAttempts<TRequest, TResponse>(
    TRequest request,
    ClientInterceptorContext<TRequest, TResponse> call,
    Interceptor.AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation,
    CancellationToken callToken)
    where TRequest : class
    where TResponse : class
{
    private int _attempts;

    public async ValueTask<StreamingResult<TResponse>> StartAsync(AegisContext context)
    {
        // Akış çağrıdan uzun yaşar: deneme kendi iptal kaynağına bağlanır (çağıran + deadline). Bağlamın iptali (deneme zaman
        // aşımı, hedging kaybı) yalnızca commit'e kadar akışı keser.
        var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(callToken);
        var untilCommit = context.CancellationToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), attemptCts);
        var previousAttempts = Interlocked.Increment(ref _attempts) - 1;
        var attempt = continuation(request, new ClientInterceptorContext<TRequest, TResponse>(
            call.Method, call.Host, GrpcAttempt.Options(call.Options, attemptCts.Token, previousAttempts)));
        try
        {
            var hasFirst = await attempt.ResponseStream.MoveNext(attemptCts.Token).ConfigureAwait(false);
            untilCommit.Dispose(); // commit: deneme zaman aşımı artık akışı kesmez
            return new StreamingResult<TResponse>(attempt, attemptCts, hasFirst, hasFirst ? attempt.ResponseStream.Current : default);
        }
        catch (RpcException ex)
        {
            Release(untilCommit, attempt, attemptCts);
            throw GrpcAttempt.Translate(ex, context);
        }
        catch
        {
            Release(untilCommit, attempt, attemptCts);
            throw;
        }
    }

    private static void Release(CancellationTokenRegistration registration, AsyncServerStreamingCall<TResponse> attempt, CancellationTokenSource cts)
    {
        registration.Dispose();
        attempt.Dispose();
        cts.Dispose();
    }
}

/// <summary>Commit olmuş sunucu akışı: ilk mesaj (varsa) ve akışın kalanını okuyacak çağrı.</summary>
internal sealed class StreamingResult<TResponse>(
    AsyncServerStreamingCall<TResponse> call, CancellationTokenSource attemptCts, bool hasFirst, TResponse? first) : IGrpcCallResult
{
    public AsyncServerStreamingCall<TResponse> Call { get; } = call;

    public Task<Metadata> ResponseHeadersAsync => Call.ResponseHeadersAsync;

    public Status GetStatus() => Call.GetStatus();

    public Metadata GetTrailers() => Call.GetTrailers();

    public bool HasFirst { get; } = hasFirst;

    public TResponse? First { get; } = first;

    public void Dispose()
    {
        Call.Dispose();
        attemptCts.Dispose();
    }
}

/// <summary>
/// Çağırana verilen akış okuyucu: başlatma (denemeler) bitene kadar bekler, sonra ilk mesajı ve akışın kalanını verir. Başlatma
/// hatası ilk <see cref="MoveNext"/>'te gRPC durumu olarak yükselir.
/// </summary>
internal sealed class CommittedStreamReader<TResponse>(Task<StreamingResult<TResponse>> start) : IAsyncStreamReader<TResponse>
{
    private bool _firstDelivered;

    public TResponse Current { get; private set; } = default!;

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        var result = await start.ConfigureAwait(false);
        if (!_firstDelivered)
        {
            _firstDelivered = true;
            if (!result.HasFirst)
            {
                return false;
            }

            Current = result.First!;
            return true;
        }

        if (!await result.Call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        Current = result.Call.ResponseStream.Current;
        return true;
    }
}
