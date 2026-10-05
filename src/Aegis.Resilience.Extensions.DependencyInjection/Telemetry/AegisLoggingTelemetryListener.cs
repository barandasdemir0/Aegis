using System.Net.Http;
using Microsoft.Extensions.Logging;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Extensions.DependencyInjection.Telemetry;

/// <summary>
/// Dayanıklılık olaylarını <see cref="ILogger"/>'a yazan dinleyici (Polly: <c>TelemetryListenerImpl</c> günlük kısmı).
/// Kategori: <c>Aegis</c>. Log düzeyi olayın önem düzeyinden gelir; düzey kapalıysa hiçbir biçimlendirme yapılmaz.
/// </summary>
public sealed partial class AegisLoggingTelemetryListener : AegisTelemetryListener
{
    /// <summary>Günlük kategorisi.</summary>
    public const string CategoryName = "Aegis";

    private readonly ILogger _logger;
    private readonly Func<AegisContext, object?, object?> _resultFormatter;

    public AegisLoggingTelemetryListener(ILoggerFactory loggerFactory, Func<AegisContext, object?, object?>? resultFormatter = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger(CategoryName);
        _resultFormatter = resultFormatter ?? DefaultResultFormatter;
    }

    /// <summary>
    /// Varsayılan sonuç biçimleyici: <see cref="HttpResponseMessage"/> için yalnızca durum kodu (gövde/başlık loglanmaz),
    /// diğer sonuçlar olduğu gibi.
    /// </summary>
    public static object? DefaultResultFormatter(AegisContext context, object? result) => result switch
    {
        HttpResponseMessage response => (int)response.StatusCode,
        _ => result
    };

    /// <inheritdoc />
    public override void Write(in AegisTelemetryEvent telemetryEvent)
    {
        var level = ToLogLevel(telemetryEvent.Severity);
        if (level == LogLevel.None || !_logger.IsEnabled(level))
        {
            return;
        }

        var pipeline = telemetryEvent.PipelineName ?? "(null)";
        var strategy = telemetryEvent.StrategyName ?? "(null)";
        var operationKey = telemetryEvent.Context.OperationKey;
        var exception = telemetryEvent.Exception;
        var result = exception is not null ? exception.Message : _resultFormatter(telemetryEvent.Context, telemetryEvent.Result);

        switch (telemetryEvent.EventName)
        {
            case AegisEventNames.ExecutionAttempt:
                LogExecutionAttempt(_logger, level, pipeline, strategy, operationKey, result, telemetryEvent.Handled ?? false,
                    telemetryEvent.AttemptNumber, telemetryEvent.Duration?.TotalMilliseconds ?? 0, exception);
                break;

            case AegisEventNames.PipelineExecuted:
                LogPipelineExecuted(_logger, level, pipeline, operationKey, result, telemetryEvent.Duration?.TotalMilliseconds ?? 0, exception);
                break;

            default:
                LogResilienceEvent(_logger, level, telemetryEvent.EventName, pipeline, strategy, operationKey, result, exception);
                break;
        }
    }

    private static LogLevel ToLogLevel(AegisEventSeverity severity) => severity switch
    {
        AegisEventSeverity.Debug => LogLevel.Debug,
        AegisEventSeverity.Information => LogLevel.Information,
        AegisEventSeverity.Warning => LogLevel.Warning,
        AegisEventSeverity.Error => LogLevel.Error,
        AegisEventSeverity.Critical => LogLevel.Critical,
        _ => LogLevel.None
    };

    // Kaynak üretici (Polly ile aynı mesaj yapısı ve EventId'ler): şablon bir kez derlenir, her olayda params dizisi ayrılmaz.
    [LoggerMessage(EventId = 0, EventName = "ResilienceEvent", Message =
        "Resilience event occurred. EventName: '{EventName}', Pipeline: '{PipelineName}', Strategy: '{StrategyName}', " +
        "OperationKey: '{OperationKey}', Result: '{Result}'")]
    private static partial void LogResilienceEvent(ILogger logger, LogLevel level, string eventName, string pipelineName,
        string strategyName, string? operationKey, object? result, Exception? exception);

    [LoggerMessage(EventId = 2, EventName = "PipelineExecuted", Message =
        "Resilience pipeline executed. Pipeline: '{PipelineName}', OperationKey: '{OperationKey}', Result: '{Result}', " +
        "Execution Time: {ExecutionTimeMs}ms")]
    private static partial void LogPipelineExecuted(ILogger logger, LogLevel level, string pipelineName, string? operationKey,
        object? result, double executionTimeMs, Exception? exception);

    [LoggerMessage(EventId = 3, EventName = "ExecutionAttempt", Message =
        "Execution attempt. Pipeline: '{PipelineName}', Strategy: '{StrategyName}', OperationKey: '{OperationKey}', " +
        "Result: '{Result}', Handled: '{Handled}', Attempt: '{Attempt}', Execution Time: {ExecutionTimeMs}ms")]
    private static partial void LogExecutionAttempt(ILogger logger, LogLevel level, string pipelineName, string strategyName,
        string? operationKey, object? result, bool handled, int attempt, double executionTimeMs, Exception? exception);
}
