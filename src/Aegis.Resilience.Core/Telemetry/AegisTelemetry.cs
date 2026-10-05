using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Aegis.Resilience.Core.Telemetry;

/// <summary>
/// .NET Metrics API (System.Diagnostics.Metrics) tabanlı merkezi telemetri ve sayaç sağlayıcısı.
/// OpenTelemetry ile doğrudan uyumludur.
/// </summary>
public static class AegisTelemetry
{
    public const string MeterName = "Aegis";
    public const string MeterVersion = "1.0.0";

    /// <summary>
    /// İz (trace) kaynağının adı; metrik <see cref="MeterName"/> ile aynıdır. OpenTelemetry'de
    /// <c>tracing.AddSource(AegisTelemetry.ActivitySourceName)</c> (ya da <c>AddAegisServiceDefaults</c>) ile açılır.
    /// Boru hattı yürütmesi başına bir span üretilir; dinleyici yoksa hiçbir nesne oluşturulmaz.
    /// </summary>
    public const string ActivitySourceName = "Aegis";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, MeterVersion);

    /// <summary>
    /// Şu an kayıt yapan bir Aegis span'ı var mı. Strateji olayları yalnızca o zaman span olayı üretir. Dinleyici yoksa tek
    /// <c>HasListeners()</c> denetimiyle biter; dinleyici bağlı ama örnekleme yoksa (span üretilmedi) olay nesneleri hiç oluşturulmaz.
    /// </summary>
    internal static bool IsSpanRecording =>
        ActivitySource.HasListeners() &&
        Activity.Current is { IsAllDataRequested: true } span &&
        ReferenceEquals(span.Source, ActivitySource);

    private static readonly Meter Meter = new(MeterName, MeterVersion);

    public static readonly Counter<long> ExecutionsTotal = Meter.CreateCounter<long>(
        "aegis.executions.total",
        description: "Toplam boru hattı ve strateji çalıştırma sayısı");

    public static readonly Counter<long> RetriesTotal = Meter.CreateCounter<long>(
        "aegis.retry.attempts.total",
        description: "Toplam yeniden deneme (retry) sayısı");

    public static readonly Counter<long> CircuitStateChangesTotal = Meter.CreateCounter<long>(
        "aegis.circuitbreaker.state_changes.total",
        description: "Circuit Breaker durum değişim sayısı");

    public static readonly Counter<long> TimeoutsTotal = Meter.CreateCounter<long>(
        "aegis.timeout.total",
        description: "Gerçekleşen zaman aşımı sayısı");

    public static readonly Counter<long> RateLimitRejectionsTotal = Meter.CreateCounter<long>(
        "aegis.ratelimit.rejections.total",
        description: "Reddedilen eşzamanlılık/hız limiti isteği sayısı");

    public static readonly Counter<long> ChaosInjectionsTotal = Meter.CreateCounter<long>(
        "aegis.chaos.injections.total",
        description: "Yapay olarak enjekte edilen hata ve gecikme sayısı");

    public static readonly Counter<long> CacheHitsTotal = Meter.CreateCounter<long>(
        "aegis.cache.hits.total",
        description: "Toplam önbellek isabet sayısı");

    public static readonly Counter<long> CacheMissesTotal = Meter.CreateCounter<long>(
        "aegis.cache.misses.total",
        description: "Toplam önbellek ıskalama sayısı");

    /// <summary>
    /// Devre durum değişimini <c>pipeline</c> ve <c>state</c> (closed/open/half_open/isolated) etiketleriyle kaydeder.
    /// Eskiden etiketsizdi: hangi devrenin hangi duruma geçtiği panoda görülemiyor, "devre açıldı" alarmı kurulamıyordu.
    /// </summary>
    public static void RecordCircuitStateChange(string? pipeline, Strategies.CircuitBreaker.CircuitState newState) =>
        CircuitStateChangesTotal.Add(1,
            new KeyValuePair<string, object?>("pipeline", pipeline ?? "default"),
            new KeyValuePair<string, object?>("state", newState switch
            {
                Strategies.CircuitBreaker.CircuitState.Closed => "closed",
                Strategies.CircuitBreaker.CircuitState.Open => "open",
                Strategies.CircuitBreaker.CircuitState.HalfOpen => "half_open",
                Strategies.CircuitBreaker.CircuitState.Isolated => "isolated",
                _ => "unknown"
            }));

    public static readonly Counter<long> CallbackErrorsTotal = Meter.CreateCounter<long>(
        "aegis.callback.errors.total",
        description: "Kullanıcı bildirimlerinin (OnOpened, OnClosed, OnHalfOpened) fırlattığı ve yutulan istisna sayısı");

    // ---------------------------------------------------------------------------------------------------------------
    // Standart (Polly / OpenTelemetry resilience semantiğiyle uyumlu) metrikler. Etiketler: event.name, event.severity,
    // pipeline.name, strategy.name, operation.key, exception.type (+ attempt.number, attempt.handled). Yukarıdaki
    // aegis.* sayaçları geriye uyumluluk için aynen korunur.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Stratejilerde gerçekleşen olay sayısı (Polly: <c>resilience.polly.strategy.events</c>).</summary>
    public static readonly Counter<int> StrategyEvents = Meter.CreateCounter<int>(
        "aegis.strategy.events",
        description: "Dayanıklılık stratejilerinde gerçekleşen olay sayısı (retry, timeout, devre açılması, red, hedging, fallback, kaos)");

    /// <summary>Yürütme denemesi süresi (Polly: <c>resilience.polly.strategy.attempt.duration</c>).</summary>
    public static readonly Histogram<double> AttemptDuration = Meter.CreateHistogram<double>(
        "aegis.strategy.attempt.duration",
        unit: "ms",
        description: "Retry ve hedging yürütme denemelerinin süresi");

    /// <summary>Boru hattı süresi, sonuç ve işlem anahtarıyla (Polly: <c>resilience.polly.pipeline.duration</c>).</summary>
    public static readonly Histogram<double> PipelineDuration = Meter.CreateHistogram<double>(
        "aegis.pipeline.duration",
        unit: "ms",
        description: "Boru hattının yürütme süresi (pipeline.name, operation.key, exception.type etiketleriyle)");

    internal static void CountCallbackError(string? pipeline, string callbackName, Exception exception)
    {
        if (CallbackErrorsTotal.Enabled)
        {
            CallbackErrorsTotal.Add(1,
                new KeyValuePair<string, object?>("callback", callbackName),
                new KeyValuePair<string, object?>("exception", exception.GetType().Name),
                new KeyValuePair<string, object?>("pipeline", pipeline ?? "default"));
        }
    }

    public static readonly Histogram<double> ExecutionDurationMs = Meter.CreateHistogram<double>(
        "aegis.execution.duration.ms",
        unit: "ms",
        description: "Boru hattı ve strateji çalıştırma süresi (milisaniye)");
}
