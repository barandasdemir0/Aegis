using System.Diagnostics;
using System.Diagnostics.Metrics;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Telemetry;

/// <summary>
/// Bir dayanıklılık olayı (Polly: <c>TelemetryEventArguments</c>). Yalnızca bir dinleyici veya metrik dinleyicisi bağlıyken
/// oluşturulur; aksi halde hiç maliyeti yoktur.
/// </summary>
public readonly struct AegisTelemetryEvent
{
    public AegisTelemetryEvent(
        string eventName,
        AegisEventSeverity severity,
        string? pipelineName,
        string? strategyName,
        AegisContext context,
        Exception? exception = null,
        object? result = null,
        object? arguments = null,
        int attemptNumber = -1,
        TimeSpan? duration = null,
        bool? handled = null)
    {
        EventName = eventName;
        Severity = severity;
        PipelineName = pipelineName;
        StrategyName = strategyName;
        Context = context;
        Exception = exception;
        Result = result;
        Arguments = arguments;
        AttemptNumber = attemptNumber;
        Duration = duration;
        Handled = handled;
    }

    /// <summary>Olay adı (bkz. <see cref="AegisEventNames"/>).</summary>
    public string EventName { get; }

    /// <summary>Stratejinin önerdiği önem düzeyi (<see cref="AegisTelemetryOptions.SeverityProvider"/> ile değiştirilebilir).</summary>
    public AegisEventSeverity Severity { get; }

    public string? PipelineName { get; }
    public string? StrategyName { get; }

    /// <summary>
    /// Boru hattı örneğinin adı (Polly: <c>PipelineInstanceName</c>); aynı adlı boru hattının örneklerini (ör. kiracı,
    /// HTTP istemcisi) ayırt eder. Ayarlanmamışsa null ve <c>pipeline.instance</c> etiketi eklenmez.
    /// </summary>
    public string? PipelineInstance { get; init; }

    /// <summary>Çağrının bağlamı (<see cref="AegisContext.OperationKey"/>, özellikler, korelasyon kimliği).</summary>
    public AegisContext Context { get; }

    /// <summary>Olayı tetikleyen istisna (varsa).</summary>
    public Exception? Exception { get; }

    /// <summary>Olayı tetikleyen sonuç (varsa; istisnasız sonuç tabanlı işlemede).</summary>
    public object? Result { get; }

    /// <summary>Stratejiye özgü ayrıntı (ör. <c>RetryAttemptContext</c>, <c>CircuitBreakerEventContext</c>, zaman aşımı süresi).</summary>
    public object? Arguments { get; }

    /// <summary>Deneme numarası (0 tabanlı); deneme kavramı yoksa -1.</summary>
    public int AttemptNumber { get; }

    /// <summary>Süre (deneme veya boru hattı süresi); yoksa null.</summary>
    public TimeSpan? Duration { get; }

    /// <summary>Denemenin sonucu strateji tarafından ele alındı mı (yeniden denenecek / hata sayıldı).</summary>
    public bool? Handled { get; }

    internal AegisTelemetryEvent WithSeverity(AegisEventSeverity severity) =>
        new(EventName, severity, PipelineName, StrategyName, Context, Exception, Result, Arguments, AttemptNumber, Duration, Handled)
        {
            PipelineInstance = PipelineInstance
        };
}
