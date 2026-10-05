using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Hedging;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Extensions.DependencyInjection;

namespace Aegis.Resilience.AspNetCore;

/// <summary>
/// Uç nokta meta verisindeki boru hattını uygular ve Aegis reddini HTTP yanıtına çevirir: hız / eşzamanlılık reddi
/// 429 (+ <c>Retry-After</c>), açık devre 503, zaman aşımı 504. Zaman aşımı uç noktanın <c>RequestAborted</c> token'ına
/// aktarılır, böylece uç nokta gerçekten iptal edilir.
/// </summary>
internal sealed class AegisInboundPipelineMiddleware(RequestDelegate next, IAegisPipelineRegistry registry)
{
    private readonly ConcurrentDictionary<string, IAegisPipeline> _validated = new(StringComparer.Ordinal);

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<AegisInboundPipelineAttribute>() is not { } metadata)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var pipeline = _validated.GetOrAdd(metadata.PipelineName, name => ValidateForInbound(registry.GetPipeline(name)));
        var requestAborted = context.RequestAborted;
        try
        {
            await pipeline.ExecuteAsync(
                static async (aegisContext, state) =>
                {
                    state.HttpContext.RequestAborted = aegisContext.CancellationToken; // zaman aşımı uç noktayı iptal etsin
                    await state.Next(state.HttpContext).ConfigureAwait(false);
                },
                new InboundState(context, next),
                new AegisContext(requestAborted) { OperationKey = context.GetEndpoint()?.DisplayName }).ConfigureAwait(false);
        }
        catch (RateLimitRejectedException ex) when (!context.Response.HasStarted)
        {
            InboundRejection.Write(context, StatusCodes.Status429TooManyRequests, InboundRejection.GrpcResourceExhausted, ex.Message, ex.RetryAfter);
        }
        catch (BrokenCircuitException ex) when (!context.Response.HasStarted)
        {
            InboundRejection.Write(context, StatusCodes.Status503ServiceUnavailable, InboundRejection.GrpcUnavailable, ex.Message, ex.RetryAfter);
        }
        catch (AegisTimeoutException ex) when (!context.Response.HasStarted)
        {
            InboundRejection.Write(context, StatusCodes.Status504GatewayTimeout, InboundRejection.GrpcDeadlineExceeded, ex.Message, retryAfter: null);
        }
        finally
        {
            context.RequestAborted = requestAborted;
        }
    }

    /// <summary>
    /// Sunucu tarafında isteği yeniden çalıştırmak güvenli değildir (istek gövdesi tüketilmiş, yanıt yazılmış olabilir):
    /// Retry ve Hedging içeren boru hattı ilk kullanımda açık bir hatayla reddedilir (iç içe boru hatları dahil).
    /// </summary>
    private static IAegisPipeline ValidateForInbound(IAegisPipeline pipeline)
    {
        if (ContainsReexecutingStrategy(pipeline))
        {
            throw new InvalidOperationException(
                $"'{pipeline.Name}' boru hattı Retry veya Hedging içeriyor; gelen istekte uç noktayı yeniden çalıştırmak güvenli değildir. " +
                "Gelen istek için eşzamanlılık sınırı, hız sınırı, zaman aşımı ve devre kesici kullanın.");
        }

        return pipeline;
    }

    private static bool ContainsReexecutingStrategy(IAegisPipeline pipeline) =>
        pipeline.Strategies.Any(strategy => strategy is RetryStrategy or HedgingStrategy ||
                                            (strategy is PipelineStrategyAdapter adapter && ContainsReexecutingStrategy(adapter.InnerPipeline)));

    private readonly record struct InboundState(HttpContext HttpContext, RequestDelegate Next);
}
