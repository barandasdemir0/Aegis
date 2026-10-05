using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Strategies.RateLimiter;

/// <summary>
/// Tüm sınırlayıcıların ortak red yolu: sayaç, telemetri olayı, <c>OnRejected</c> bildirimi ve istisna (DRY).
/// Özel sınırlayıcı stratejileri de aynı davranış için kullanabilir.
/// </summary>
public static class RateLimiterRejection
{
    public static ValueTask<Outcome<TResult>> RejectAsync<TResult>(
        AegisStrategyTelemetry telemetry,
        AegisContext context,
        string strategyName,
        Func<RateLimiterRejectedArguments, ValueTask>? onRejected,
        string message,
        TimeSpan? retryAfter) =>
        RejectAsync<TResult>(telemetry, context, strategyName, onRejected, message, retryAfter, metadata: null);

    /// <summary>Red ayrıntılarıyla (<see cref="RateLimiterRejectedArguments.Metadata"/>); yalnızca olay oluşturulursa okunur.</summary>
    public static async ValueTask<Outcome<TResult>> RejectAsync<TResult>(
        AegisStrategyTelemetry telemetry,
        AegisContext context,
        string strategyName,
        Func<RateLimiterRejectedArguments, ValueTask>? onRejected,
        string message,
        TimeSpan? retryAfter,
        Func<IReadOnlyList<KeyValuePair<string, object?>>>? metadata)
    {
        AegisTelemetry.RateLimitRejectionsTotal.Add(1, new KeyValuePair<string, object?>("pipeline", context.PipelineName ?? "default"));

        RateLimiterRejectedArguments? arguments = null;
        if (telemetry.IsEnabled || onRejected is not null)
        {
            arguments = new RateLimiterRejectedArguments
            {
                Context = context, StrategyName = strategyName, RetryAfter = retryAfter, Metadata = metadata?.Invoke()
            };
            telemetry.Report(AegisEventNames.OnRateLimiterRejected, AegisEventSeverity.Error, context, arguments: arguments);
        }

        if (onRejected is not null)
        {
            await AegisCallbacks.InvokeSafelyAsync(onRejected, arguments!, "OnRejected").ConfigureAwait(context.ContinueOnCapturedContext);
        }

        // Reddedilme fırlatılmaz; en dışta bir kez fırlatılır.
        return Outcome<TResult>.FromException(new RateLimitRejectedException(message, retryAfter) { TelemetrySource = telemetry.Source });
    }
}
