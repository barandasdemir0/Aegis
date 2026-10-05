using Grpc.Core;
using Grpc.Core.Interceptors;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Grpc;

/// <summary>
/// gRPC istemci dayanıklılığı: Aegis boru hattını gRPC katmanında çalıştırır. HTTP katmanındaki işleyiciler (Polly / Microsoft
/// <c>AddStandardResilienceHandler</c>) gRPC hatasını göremez: durum <c>grpc-status</c> trailer'ındadır ve yanıt HTTP 200'dür;
/// her gRPC çağrısı da HTTP POST olduğu için yeniden denenmez.
/// <list type="bullet">
/// <item>Unary (async ve senkron): tüm stratejiler (retry, hedging, devre kesici, zaman aşımı, hız sınırı).</item>
/// <item>Sunucu akışı: ilk mesaja kadar tüm stratejiler; ilk mesajla commit (gRPC A6).</item>
/// <item>İstemci ve çift yönlü akış: gönderilen mesajlar tamponlanıp her denemede yeniden oynatılır
/// (<see cref="AegisGrpcClientOptions.MaxRetryBufferBytes"/> aşılınca ya da ilk yanıtla commit).</item>
/// </list>
/// Deadline tüm denemeleri kapsar; dolunca kalan yeniden denemeler atlanır ve <see cref="StatusCode.DeadlineExceeded"/> döner.
/// Retler <see cref="RpcException"/>'a çevrilir (açık devre → Unavailable, hız sınırı → ResourceExhausted).
/// </summary>
public sealed class AegisGrpcClientInterceptor : Interceptor
{
    private readonly IAegisPipeline _pipeline;
    private readonly AegisGrpcClientOptions _options;

    /// <summary>Interceptor'ı oluşturur. Boru hattı gRPC kanalı/istemcisiyle aynı ömürde (tekil) olmalıdır.</summary>
    public AegisGrpcClientInterceptor(IAegisPipeline pipeline, AegisGrpcClientOptions? options = null)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _options = options ?? new AegisGrpcClientOptions();
    }

    /// <inheritdoc />
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var attempts = new UnaryAttempts<TRequest, TResponse>(request, context) { AsyncContinuation = continuation };
        var scope = CallScope.Create(context.Options);
        var result = RunAsync(static (ctx, a) => a.SendAsync(ctx), attempts, context, scope, ownsScope: true);
        // Durum nesneli yapıcı + statik delegeler: çağrı başına closure ve delege tahsisi yok.
        return new AsyncUnaryCall<TResponse>(
            ResponseOf(result),
            GrpcCallResult<UnaryResult<TResponse>>.HeadersOf,
            GrpcCallResult<UnaryResult<TResponse>>.Status,
            GrpcCallResult<UnaryResult<TResponse>>.Trailers,
            GrpcCallResult<UnaryResult<TResponse>>.Dispose,
            result);
    }

    /// <inheritdoc />
    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        TRequest request, ClientInterceptorContext<TRequest, TResponse> context, BlockingUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var attempts = new UnaryAttempts<TRequest, TResponse>(request, context) { BlockingContinuation = continuation };
        var scope = CallScope.Create(context.Options);
        return RunAsync(static (ctx, a) => a.Send(ctx), attempts, context, scope, ownsScope: true).GetAwaiter().GetResult().Response;
    }

    /// <inheritdoc />
    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        // Akış çağrıdan uzun yaşar: kapsam (çağıran + deadline) akış bırakılınca kapatılır.
        var scope = CallScope.Create(context.Options, alwaysOwnSource: true);
        var attempts = new ServerStreamingAttempts<TRequest, TResponse>(request, context, continuation, scope.Token);
        var start = RunAsync(static (ctx, a) => a.StartAsync(ctx), attempts, context, scope, ownsScope: false);
        return new AsyncServerStreamingCall<TResponse>(
            new CommittedStreamReader<TResponse>(start),
            GrpcCallResult<StreamingResult<TResponse>>.Headers(start),
            () => GrpcCallResult<StreamingResult<TResponse>>.Status(start),
            () => GrpcCallResult<StreamingResult<TResponse>>.Trailers(start),
            () =>
            {
                scope.Cancel();
                GrpcCallResult<StreamingResult<TResponse>>.DisposeWhenDone(start);
                scope.Dispose();
            });
    }

    /// <inheritdoc />
    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context, AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        if (_options.MaxRetryBufferBytes <= 0)
        {
            return continuation(context); // tamponlama kapalı: yeniden oynatılamaz, olduğu gibi geçer
        }

        var buffer = new ReplayBuffer<TRequest>(_options.MaxRetryBufferBytes, SizerOf(context.Method));
        var scope = CallScope.Create(context.Options);
        var attempts = new RequestStreamingAttempts<TRequest, TResponse>(context, buffer, scope.Token) { ClientStreaming = continuation };
        var result = RunAsync(static (ctx, a) => a.SendAsync(ctx), attempts, context, scope, ownsScope: true);
        _ = ReleaseWritersAsync(result, buffer, releaseOnSuccess: true);
        return new AsyncClientStreamingCall<TRequest, TResponse>(
            new ReplayingRequestWriter<TRequest>(buffer),
            ResponseOf(result),
            GrpcCallResult<ClientStreamingResult<TRequest, TResponse>>.HeadersOf,
            GrpcCallResult<ClientStreamingResult<TRequest, TResponse>>.Status,
            GrpcCallResult<ClientStreamingResult<TRequest, TResponse>>.Trailers,
            GrpcCallResult<ClientStreamingResult<TRequest, TResponse>>.Dispose,
            result);
    }

    /// <inheritdoc />
    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context, AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        if (_options.MaxRetryBufferBytes <= 0)
        {
            return continuation(context);
        }

        var buffer = new ReplayBuffer<TRequest>(_options.MaxRetryBufferBytes, SizerOf(context.Method));
        var scope = CallScope.Create(context.Options, alwaysOwnSource: true);
        var attempts = new RequestStreamingAttempts<TRequest, TResponse>(context, buffer, scope.Token) { Duplex = continuation };
        var start = RunAsync(static (ctx, a) => a.StartDuplexAsync(ctx), attempts, context, scope, ownsScope: false);
        _ = ReleaseWritersAsync(start, buffer, releaseOnSuccess: false); // commit sonrası istek pompası sürer
        return new AsyncDuplexStreamingCall<TRequest, TResponse>(
            new ReplayingRequestWriter<TRequest>(buffer),
            new CommittedStreamReader<TResponse>(start),
            GrpcCallResult<StreamingResult<TResponse>>.Headers(start),
            () => GrpcCallResult<StreamingResult<TResponse>>.Status(start),
            () => GrpcCallResult<StreamingResult<TResponse>>.Trailers(start),
            () =>
            {
                buffer.Fail(new ObjectDisposedException("Çağrı bırakıldı; yeni mesaj yazılamaz."));
                scope.Cancel();
                GrpcCallResult<StreamingResult<TResponse>>.DisposeWhenDone(start);
                scope.Dispose();
            });
    }

    // Mesaj boyutu (tampon sınırı için): Grpc.Net.Client gibi seri hale getirilmiş bayt sayısı.
    private static Func<T, int> SizerOf<T, TResponse>(Method<T, TResponse> method)
    {
        var marshaller = method.RequestMarshaller;
        return message => MessageSizer.Measure(marshaller, message);
    }

    // Çağrı bittiğinde (ya da başarısız olduğunda) bekleyen yazmalar serbest bırakılır; aksi halde çağıran sonsuza dek beklerdi.
    private static async Task ReleaseWritersAsync<T, TRequest>(Task<T> call, ReplayBuffer<TRequest> buffer, bool releaseOnSuccess)
        where TRequest : class
    {
        try
        {
            await call.ConfigureAwait(false);
            if (releaseOnSuccess)
            {
                buffer.Fail(new InvalidOperationException("Çağrı tamamlandı; yeni mesaj yazılamaz."));
            }
        }
#pragma warning disable CA1031 // Hata çağırana yanıt tarafında yükselir; burada yalnızca yazıcılar serbest bırakılır.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            buffer.Fail(ex);
        }
    }

    // Eşzamanlı tamamlanmışsa async durum makinesi oluşturulmaz.
    private static Task<TResponse> ResponseOf<TRequest, TResponse>(Task<ClientStreamingResult<TRequest, TResponse>> result) =>
        result.Status == TaskStatus.RanToCompletion ? Task.FromResult(result.Result.Response) : ResponseOfAsync(result);

    private static async Task<TResponse> ResponseOfAsync<TRequest, TResponse>(Task<ClientStreamingResult<TRequest, TResponse>> result) =>
        (await result.ConfigureAwait(false)).Response;

    private async Task<TResult> RunAsync<TResult, TState, TRequest, TResponse>(
        Func<AegisContext, TState, ValueTask<TResult>> attempt, TState state, ClientInterceptorContext<TRequest, TResponse> call,
        CallScope scope, bool ownsScope)
        where TRequest : class
        where TResponse : class
    {
        var context = AegisContextPool.Rent(scope.Token, _pipeline.Name);
        context.OperationKey = call.Method.FullName; // telemetride metot bazında ayrım
        try
        {
            return await _pipeline.ExecuteAsync(attempt, state, context).ConfigureAwait(false);
        }
        catch (CommittedCallException ex)
        {
            // Commit olmuş çağrının özgün hatası (yeniden deneme koşuluna takılmasın diye sarılmıştı).
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
            throw;
        }
        catch (OperationCanceledException ex) when (scope.ToRpcException(ex, call.Options.CancellationToken) is { } cancelled)
        {
            throw cancelled;
        }
        catch (Exception ex) when (_options.MapRejectionsToRpcException && AegisGrpcStatusMapper.ToRpcException(ex) is { } rejected)
        {
            throw rejected;
        }
        finally
        {
            AegisContextPool.Return(context);
            if (ownsScope)
            {
                scope.Dispose();
            }
        }
    }

    private static Task<TResponse> ResponseOf<TResponse>(Task<UnaryResult<TResponse>> result) =>
        result.Status == TaskStatus.RanToCompletion ? Task.FromResult(result.Result.Response) : ResponseOfAsync(result);

    private static async Task<TResponse> ResponseOfAsync<TResponse>(Task<UnaryResult<TResponse>> result) =>
        (await result.ConfigureAwait(false)).Response;
}
