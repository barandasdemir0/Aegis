using Grpc.Core;
using Grpc.Core.Interceptors;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Grpc.AspNetCore;

/// <summary>
/// gRPC sunucu koruması: gelen çağrıları Aegis boru hattından geçirir (eşzamanlılık sınırı, hız sınırı, zaman aşımı, devre
/// kesici). Retler istemcinin anladığı gRPC durumuyla döner: hız sınırı / eşzamanlılık → <see cref="StatusCode.ResourceExhausted"/>,
/// açık devre → <see cref="StatusCode.Unavailable"/>, zaman aşımı → <see cref="StatusCode.DeadlineExceeded"/>; öneri süresi
/// <see cref="AegisGrpcMetadata.RetryPushback"/> trailer'ıyla bildirilir ve istemcinin (Aegis ya da Grpc.Net.Client) yeniden
/// deneme zamanlaması buna uyar. Çağrının iptal token'ı (istemci iptali ve deadline) boru hattına aktarılır.
/// <para>
/// Kayıt: <c>services.AddGrpc(o =&gt; o.Interceptors.Add&lt;AegisGrpcServerInterceptor&gt;(pipeline));</c>
/// </para>
/// </summary>
public sealed class AegisGrpcServerInterceptor : Interceptor
{
    private readonly IAegisPipeline _pipeline;

    /// <summary>
    /// Interceptor'ı oluşturur. Retry ve hedging içeren boru hattı reddedilir: sunucu, gelen çağrıyı istemci adına yeniden
    /// çalıştıramaz (istek akışı tüketilmiştir, yan etkiler tekrarlanır); yeniden deneme istemcinin işidir.
    /// </summary>
    public AegisGrpcServerInterceptor(IAegisPipeline pipeline)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        if (ContainsReexecutingStrategy(pipeline))
        {
            throw new InvalidOperationException(
                $"'{pipeline.Name}' boru hattı Retry veya Hedging içeriyor; sunucu gelen gRPC çağrısını yeniden çalıştıramaz. " +
                "Sunucuda eşzamanlılık sınırı, hız sınırı, zaman aşımı ve devre kesici kullanın; yeniden deneme istemcide yapılır.");
        }
    }

    /// <inheritdoc />
    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation) =>
        RunAsync(static (ctx, s) => new ValueTask<TResponse>(s.Continuation(s.Request, Wrap(s.Call, ctx))), (Request: request, Call: context, Continuation: continuation), context);

    /// <inheritdoc />
    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation) =>
        RunAsync(static (ctx, s) => new ValueTask<TResponse>(s.Continuation(s.Stream, Wrap(s.Call, ctx))), (Stream: requestStream, Call: context, Continuation: continuation), context);

    /// <inheritdoc />
    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation) =>
        RunAsync(static async (ctx, s) => { await s.Continuation(s.Request, s.Stream, Wrap(s.Call, ctx)).ConfigureAwait(false); return true; },
            (Request: request, Stream: responseStream, Call: context, Continuation: continuation), context);

    /// <inheritdoc />
    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation) =>
        RunAsync(static async (ctx, s) => { await s.Continuation(s.Requests, s.Responses, Wrap(s.Call, ctx)).ConfigureAwait(false); return true; },
            (Requests: requestStream, Responses: responseStream, Call: context, Continuation: continuation), context);

    // Servis kodu boru hattının iptalini görür (sunucu zaman aşımı kodu gerçekten keser); token değişmediyse asıl bağlam kullanılır.
    private static ServerCallContext Wrap(ServerCallContext call, AegisContext context) =>
        context.CancellationToken == call.CancellationToken ? call : new PipelineServerCallContext(call, context.CancellationToken);

    // İç içe boru hatları (AddPipeline) dahil taranır.
    private static bool ContainsReexecutingStrategy(IAegisPipeline pipeline) =>
        pipeline.Strategies.Any(strategy =>
            strategy is Aegis.Resilience.Core.Strategies.Retry.RetryStrategy or Aegis.Resilience.Core.Strategies.Hedging.HedgingStrategy ||
            (strategy is Aegis.Resilience.Core.Pipeline.PipelineStrategyAdapter adapter && ContainsReexecutingStrategy(adapter.InnerPipeline)));

    private async Task<TResult> RunAsync<TResult, TState>(Func<AegisContext, TState, ValueTask<TResult>> handler, TState state, ServerCallContext call)
    {
        var context = AegisContextPool.Rent(call.CancellationToken, _pipeline.Name);
        context.OperationKey = call.Method; // telemetride metot bazında ayrım
        try
        {
            return await _pipeline.ExecuteAsync(handler, state, context).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not RpcException && AegisGrpcStatusMapper.ToRpcException(ex) is { } rejected)
        {
            throw rejected;
        }
        finally
        {
            AegisContextPool.Return(context);
        }
    }
}
