using Grpc.Core;
using Grpc.Core.Interceptors;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Grpc;

/// <summary>Bir unary çağrının denemeleri (async ve senkron). Her deneme yeni bir gRPC çağrısıdır; istek mesajı değişmez.</summary>
internal sealed class UnaryAttempts<TRequest, TResponse>(TRequest request, ClientInterceptorContext<TRequest, TResponse> call)
    where TRequest : class
    where TResponse : class
{
    private int _attempts;

    public Interceptor.AsyncUnaryCallContinuation<TRequest, TResponse>? AsyncContinuation { get; init; }

    public Interceptor.BlockingUnaryCallContinuation<TRequest, TResponse>? BlockingContinuation { get; init; }

    public async ValueTask<UnaryResult<TResponse>> SendAsync(AegisContext context)
    {
        var attempt = AsyncContinuation!(request, NextContext(context));
        try
        {
            var response = await attempt.ResponseAsync.ConfigureAwait(false);
            return new UnaryResult<TResponse>(attempt, response);
        }
        catch (RpcException ex)
        {
            attempt.Dispose();
            throw GrpcAttempt.Translate(ex, context);
        }
        catch
        {
            attempt.Dispose();
            throw;
        }
    }

    public ValueTask<UnaryResult<TResponse>> Send(AegisContext context)
    {
        try
        {
            return new ValueTask<UnaryResult<TResponse>>(new UnaryResult<TResponse>(null, BlockingContinuation!(request, NextContext(context))));
        }
        catch (RpcException ex)
        {
            throw GrpcAttempt.Translate(ex, context);
        }
    }

    private ClientInterceptorContext<TRequest, TResponse> NextContext(AegisContext context)
    {
        var previousAttempts = Interlocked.Increment(ref _attempts) - 1;
        return new ClientInterceptorContext<TRequest, TResponse>(
            call.Method, call.Host, GrpcAttempt.Options(call.Options, context.CancellationToken, previousAttempts));
    }
}

/// <summary>Kazanan unary deneme: yanıt ve onu üreten çağrı (başlıklar, durum ve trailer'lar bu çağrıdan okunur).</summary>
internal sealed class UnaryResult<TResponse>(AsyncUnaryCall<TResponse>? call, TResponse response) : IGrpcCallResult
{
    public AsyncUnaryCall<TResponse>? Call { get; } = call;

    public TResponse Response { get; } = response;

    // Senkron (blocking) çağrıda gerçek çağrı nesnesi yoktur: başarı durumu ve boş metadata.
    public Task<Metadata> ResponseHeadersAsync => Call?.ResponseHeadersAsync ?? Task.FromResult(new Metadata());

    public Status GetStatus() => Call?.GetStatus() ?? Status.DefaultSuccess;

    public Metadata GetTrailers() => Call?.GetTrailers() ?? new Metadata();

    public void Dispose() => Call?.Dispose(); // hedging'de kaybeden başarılı deneme de bırakılır
}
