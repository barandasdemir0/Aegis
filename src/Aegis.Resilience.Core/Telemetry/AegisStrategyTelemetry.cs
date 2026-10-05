using System.Diagnostics;
using System.Diagnostics.Metrics;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Telemetry;

/// <summary>
/// Bir stratejinin (veya boru hattının) olay kaynağı (Polly: <c>ResilienceStrategyTelemetry</c>). Dinleyici yoksa ve
/// standart metrikler dinlenmiyorsa <see cref="IsEnabled"/> false'tur ve stratejiler olay nesnesini hiç oluşturmaz.
/// </summary>
public sealed class AegisStrategyTelemetry
{
    private static readonly AegisTelemetryListener[] NoListeners = [];
    private static readonly Action<AegisEnrichmentContext>[] NoEnrichers = [];

    private readonly AegisTelemetryListener[] _listeners;
    private readonly Action<AegisEnrichmentContext>[] _enrichers;
    private readonly Func<AegisTelemetryEvent, AegisEventSeverity>? _severityProvider;
    private AegisTelemetrySource? _source;

    internal AegisStrategyTelemetry(string? pipelineName, string? strategyName, AegisTelemetryOptions? options, string? instanceName = null)
    {
        PipelineName = pipelineName;
        StrategyName = strategyName;
        InstanceName = instanceName;
        _listeners = options is { Listeners.Count: > 0 } ? [.. options.Listeners] : NoListeners;
        _enrichers = options is { MeteringEnrichers.Count: > 0 } ? [.. options.MeteringEnrichers] : NoEnrichers;
        _severityProvider = options?.SeverityProvider;
    }

    public string? PipelineName { get; }
    public string? StrategyName { get; }

    /// <summary>Boru hattı örneğinin adı (bkz. <see cref="AegisTelemetryEvent.PipelineInstance"/>).</summary>
    public string? InstanceName { get; }

    /// <summary>Bu kaynağın kimliği; strateji redlerinde <c>AegisException.TelemetrySource</c> olarak taşınır (tek örnek, tahsissiz).</summary>
    public AegisTelemetrySource Source => _source ??= new(PipelineName, InstanceName, StrategyName);

    /// <summary>Dinleyici bağlı mı. Satır içi alınabilen ucuz denetim: sıcak yolda olay metodu yalnızca dinleyici varken çağrılır.</summary>
    internal bool HasListeners => _listeners.Length > 0;

    /// <summary>Olay raporlamanın bir etkisi var mı (dinleyici, etkin metrik ya da iz dinleyicisi).</summary>
    public bool IsEnabled => _listeners.Length > 0 || AegisTelemetry.StrategyEvents.Enabled || AegisTelemetry.IsSpanRecording;

    /// <summary>Deneme süresi raporlamanın bir etkisi var mı (süre ölçümü yalnızca o zaman yapılır).</summary>
    public bool IsAttemptEnabled => _listeners.Length > 0 || AegisTelemetry.AttemptDuration.Enabled || AegisTelemetry.IsSpanRecording;

    /// <summary>Boru hattı süresi raporlamanın bir etkisi var mı.</summary>
    public bool IsPipelineEnabled => _listeners.Length > 0 || AegisTelemetry.PipelineDuration.Enabled;

    /// <summary>Genel bir strateji olayı raporlar (<c>aegis.strategy.events</c> + dinleyiciler).</summary>
    public void Report(
        string eventName,
        AegisEventSeverity severity,
        AegisContext context,
        Exception? exception = null,
        object? result = null,
        object? arguments = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        Write(new AegisTelemetryEvent(eventName, severity, PipelineName ?? context.PipelineName, StrategyName, context,
                exception, result, arguments) { PipelineInstance = InstanceName },
            AegisTelemetry.StrategyEvents, value: 1);
    }

    /// <summary>Bir yürütme denemesini raporlar (<c>aegis.strategy.attempt.duration</c> + dinleyiciler).</summary>
    public void ReportAttempt(AegisContext context, int attemptNumber, TimeSpan duration, bool handled, Exception? exception = null, object? result = null)
    {
        if (!IsAttemptEnabled)
        {
            return;
        }

        // Polly başarılı denemeyi Information yazar; her istekte log üretmemek için Aegis Debug kullanır (SeverityProvider ile değiştirilebilir).
        var severity = handled ? AegisEventSeverity.Warning : AegisEventSeverity.Debug;
        Write(new AegisTelemetryEvent(AegisEventNames.ExecutionAttempt, severity, PipelineName ?? context.PipelineName, StrategyName, context,
                exception, result, arguments: null, attemptNumber, duration, handled) { PipelineInstance = InstanceName },
            AegisTelemetry.AttemptDuration, duration.TotalMilliseconds);
    }

    /// <summary>
    /// Boru hattı girişini dinleyicilere raporlar (Polly: <c>PipelineExecuting</c>, Debug). Polly gibi metrik yazılmaz;
    /// çağıran önce <see cref="HasListeners"/> denetler.
    /// </summary>
    internal void ReportPipelineExecuting(AegisContext context)
    {
        Notify(new AegisTelemetryEvent(AegisEventNames.PipelineExecuting, AegisEventSeverity.Debug, PipelineName ?? context.PipelineName,
            strategyName: null, context) { PipelineInstance = InstanceName });
    }

    /// <summary>Boru hattı sonucunu raporlar (<c>aegis.pipeline.duration</c> + dinleyiciler).</summary>
    public void ReportPipelineExecuted(AegisContext context, TimeSpan duration, Exception? exception)
    {
        if (!IsPipelineEnabled)
        {
            return;
        }

        var severity = exception is null ? AegisEventSeverity.Information : AegisEventSeverity.Warning;
        Write(new AegisTelemetryEvent(AegisEventNames.PipelineExecuted, severity, PipelineName ?? context.PipelineName, strategyName: null, context,
                exception, result: null, arguments: null, attemptNumber: -1, duration) { PipelineInstance = InstanceName },
            AegisTelemetry.PipelineDuration, duration.TotalMilliseconds);
    }

    private void Write<T>(AegisTelemetryEvent telemetryEvent, Instrument<T> instrument, T value)
        where T : struct
    {
        telemetryEvent = Notify(telemetryEvent);
        if (!instrument.Enabled)
        {
            return;
        }

        var tags = BuildTags(telemetryEvent);

#if AEGIS_LEGACY
        ReadOnlySpan<KeyValuePair<string, object?>> span = tags.ToArray(); // eski hedeflerde CollectionsMarshal yok
#else
        ReadOnlySpan<KeyValuePair<string, object?>> span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(tags);
#endif
        switch (instrument)
        {
            case Counter<T> counter:
                counter.Add(value, span);
                break;
            case Histogram<T> histogram:
                histogram.Record(value, span);
                break;
        }
    }

    /// <summary>Standart etiketleri üretir, ardından zenginleştiricilerin ek etiketlerini toplar (hatalıları sayılır, yutulur).</summary>
    private List<KeyValuePair<string, object?>> BuildTags(AegisTelemetryEvent telemetryEvent)
    {
        var enrichment = new AegisEnrichmentContext { TelemetryEvent = telemetryEvent };
        var tags = enrichment.Tags;
        tags.Add(new(AegisTelemetryTags.EventName, telemetryEvent.EventName));
        tags.Add(new(AegisTelemetryTags.EventSeverity, SeverityText(telemetryEvent.Severity)));
        if (telemetryEvent.PipelineName is { } pipeline)
        {
            tags.Add(new(AegisTelemetryTags.PipelineName, pipeline));
        }

        if (telemetryEvent.PipelineInstance is { } instance)
        {
            tags.Add(new(AegisTelemetryTags.PipelineInstance, instance));
        }

        if (telemetryEvent.StrategyName is { } strategy)
        {
            tags.Add(new(AegisTelemetryTags.StrategyName, strategy));
        }

        if (telemetryEvent.Context.OperationKey is { } operationKey)
        {
            tags.Add(new(AegisTelemetryTags.OperationKey, operationKey));
        }

        if (telemetryEvent.Exception is { } exception)
        {
            tags.Add(new(AegisTelemetryTags.ExceptionType, exception.GetType().FullName));
        }

        if (telemetryEvent.AttemptNumber >= 0)
        {
            tags.Add(new(AegisTelemetryTags.AttemptNumber, telemetryEvent.AttemptNumber));
            tags.Add(new(AegisTelemetryTags.AttemptHandled, telemetryEvent.Handled ?? false));
        }

        foreach (var enricher in _enrichers)
        {
            try
            {
                enricher(enrichment);
            }
            catch (Exception ex)
            {
                AegisTelemetry.CountCallbackError(PipelineName, nameof(AegisTelemetryOptions.MeteringEnrichers), ex);
            }
        }

        return tags;
    }

    /// <summary>Önem sağlayıcıyı uygular ve olayı dinleyicilere yazar; son hâlini (metrik etiketleri için) döner.</summary>
    private AegisTelemetryEvent Notify(AegisTelemetryEvent telemetryEvent)
    {
        if (_severityProvider is { } provider)
        {
            try
            {
                telemetryEvent = telemetryEvent.WithSeverity(provider(telemetryEvent));
            }
            catch (Exception ex)
            {
                AegisTelemetry.CountCallbackError(PipelineName, nameof(AegisTelemetryOptions.SeverityProvider), ex);
            }
        }

        foreach (var listener in _listeners)
        {
            try
            {
                listener.Write(in telemetryEvent);
            }
            catch (Exception ex)
            {
                // Telemetri asla çağrıyı bozmamalı (Polly ile aynı ilke).
                AegisTelemetry.CountCallbackError(PipelineName, listener.GetType().Name, ex);
            }
        }

        AddToSpan(telemetryEvent);
        return telemetryEvent;
    }

    /// <summary>
    /// Strateji olayını (retry, devre açıldı, zaman aşımı ...) çalışan Aegis span'ına <see cref="ActivityEvent"/> olarak ekler.
    /// Boru hattının kendi başlangıç/bitiş olayları eklenmez: span zaten bunları temsil eder. Aegis span'ı yoksa ya da kayıt
    /// yapılmıyorsa (örnekleme) hiçbir nesne oluşturulmaz.
    /// </summary>
    private static void AddToSpan(in AegisTelemetryEvent telemetryEvent)
    {
        if (Activity.Current is not { IsAllDataRequested: true } span ||
            !ReferenceEquals(span.Source, AegisTelemetry.ActivitySource) ||
            telemetryEvent.EventName is AegisEventNames.PipelineExecuting or AegisEventNames.PipelineExecuted)
        {
            return;
        }

        // Sorunsuz tamamlanan, ele alınmayan deneme span'a bilgi katmaz (her başarılı çağrıda bir olay = gürültü ve tahsis).
        if (telemetryEvent is { EventName: AegisEventNames.ExecutionAttempt, Handled: false, Exception: null })
        {
            return;
        }

        var tags = new ActivityTagsCollection { { AegisTelemetryTags.EventSeverity, SeverityText(telemetryEvent.Severity) } };
        if (telemetryEvent.StrategyName is { } strategy)
        {
            tags.Add(AegisTelemetryTags.StrategyName, strategy);
        }

        if (telemetryEvent.Exception is { } exception)
        {
            tags.Add(AegisTelemetryTags.ExceptionType, exception.GetType().FullName);
        }

        if (telemetryEvent.AttemptNumber >= 0)
        {
            tags.Add(AegisTelemetryTags.AttemptNumber, telemetryEvent.AttemptNumber);
            tags.Add(AegisTelemetryTags.AttemptHandled, telemetryEvent.Handled ?? false);
        }

        span.AddEvent(new ActivityEvent(telemetryEvent.EventName, tags: tags));
    }

    /// <summary>Önem düzeyinin metrik/günlük metni (Polly ile aynı).</summary>
    public static string SeverityText(AegisEventSeverity severity) => severity switch
    {
        AegisEventSeverity.Debug => "Debug",
        AegisEventSeverity.Information => "Information",
        AegisEventSeverity.Warning => "Warning",
        AegisEventSeverity.Error => "Error",
        AegisEventSeverity.Critical => "Critical",
        _ => "None"
    };
}
