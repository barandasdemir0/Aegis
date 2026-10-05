using Aegis.Resilience.Core.Exceptions;
using Grpc.Core;
using Microsoft.Data.SqlClient;

namespace Shop.Api;

/// <summary>
/// Dayanıklılık sonuçlarını HTTP yanıtına çevirir: açık devre 503 (+Retry-After), kota 429 (+Retry-After), zaman aşımı 504,
/// bağımlılık hatası 502. Gövdede istisna türü yazar (testler hangi stratejinin devreye girdiğini görür).
/// </summary>
public static class ErrorMapping
{
    public static IApplicationBuilder UseShopErrorMapping(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (Exception ex) when (!context.Response.HasStarted && Map(ex) is { } status)
            {
                context.Response.Clear();
                context.Response.StatusCode = status;
                if (RetryAfter(ex) is { } wait)
                {
                    context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                await context.Response.WriteAsJsonAsync(new { error = ex.GetType().Name, message = ex.Message });
            }
        });

    private static int? Map(Exception ex) => ex switch
    {
        BrokenCircuitException => StatusCodes.Status503ServiceUnavailable,
        RateLimitRejectedException => StatusCodes.Status429TooManyRequests,
        AegisTimeoutException => StatusCodes.Status504GatewayTimeout,
        ChaosInjectedException => StatusCodes.Status503ServiceUnavailable,
        RpcException { StatusCode: StatusCode.DeadlineExceeded } => StatusCodes.Status504GatewayTimeout,
        RpcException { StatusCode: StatusCode.ResourceExhausted } => StatusCodes.Status429TooManyRequests,
        RpcException or HttpRequestException or SqlException => StatusCodes.Status502BadGateway,
        _ => null
    };

    private static TimeSpan? RetryAfter(Exception ex) => ex switch
    {
        BrokenCircuitException broken => broken.RetryAfter,
        RateLimitRejectedException limited => limited.RetryAfter,
        _ => null
    };
}
