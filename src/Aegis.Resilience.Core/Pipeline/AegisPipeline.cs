using System.Diagnostics;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Pipeline;

/// <summary>
/// Sıralı dayanıklılık stratejilerini tek bir boru hattında birleştiren ve yürüten çekirdek sınıf.
/// Sıfır-tahsisat (Zero-allocation) hedefiyle strateji zincirini Build anında önceden derler (Pre-compiled chain).
/// Tüm çalıştırma biçimleri (async / senkron / CancellationToken / TState / Outcome) tek bir çekirdekten geçer
/// (bkz. <c>AegisPipeline.Execute.cs</c>).
/// </summary>
public sealed partial class AegisPipeline : IAegisPipeline
{
    private readonly PipelineComponent _entryComponent;

    public string Name { get; }
    public IReadOnlyList<IAegisStrategy> Strategies { get; }

    /// <summary>
    /// Hiçbir strateji içermeyen boru hattı (Polly: <c>ResiliencePipeline.Empty</c>): geri çağrıyı doğrudan çalıştırır.
    /// Dayanıklılığın isteğe bağlı olduğu kodda null denetimi yerine kullanılır.
    /// </summary>
    public static AegisPipeline Empty { get; } = new("Empty", Array.Empty<IAegisStrategy>());

    /// <summary>Boru hattı örneğinin adı (bkz. <see cref="AegisPipelineBuilder.InstanceName"/>); yoksa null.</summary>
    public string? InstanceName { get; }

    private readonly AegisStrategyTelemetry _telemetry;

    /// <summary>Span adı (boru hattı başına bir kez kurulur; çağrı başına dize oluşturulmaz).</summary>
    private readonly string _spanName;

    public AegisPipeline(string name, IReadOnlyList<IAegisStrategy> strategies)
        : this(name, strategies, telemetryOptions: null)
    {
    }

    /// <summary>Telemetri seçenekleriyle (dinleyiciler, zenginleştiriciler) boru hattı oluşturur.</summary>
    public AegisPipeline(string name, IReadOnlyList<IAegisStrategy> strategies, AegisTelemetryOptions? telemetryOptions)
        : this(name, strategies, telemetryOptions, instanceName: null)
    {
    }

    internal AegisPipeline(string name, IReadOnlyList<IAegisStrategy> strategies, AegisTelemetryOptions? telemetryOptions, string? instanceName)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Strategies = strategies ?? throw new ArgumentNullException(nameof(strategies));
        InstanceName = instanceName;
        _spanName = "Aegis " + name;
        _telemetry = new AegisStrategyTelemetry(name, strategyName: null, telemetryOptions, instanceName);

        // Stratejileri Build anında tek seferlik bağlama (Zero-allocation hot path)
        PipelineComponent current = TerminalPipelineComponent.Instance;
        for (var i = strategies.Count - 1; i >= 0; i--)
        {
            current = strategies[i] is AegisStrategy fast
                ? new FastStrategyComponent(fast, current)            // yerleşik: sıfır tahsisli hızlı yol
                : new LegacyStrategyComponent(strategies[i], current); // özel IAegisStrategy: eski (closure'lı) yol
        }
        _entryComponent = current;
    }

    /// <summary>
    /// Tüm çalıştırma biçimlerinin ortak çekirdeği. Asla istisna fırlatmaz; sonucu <see cref="Outcome{TResult}"/> olarak döner.
    /// </summary>
    private ValueTask<Outcome<TResult>> ExecuteCoreAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        TState state,
        AegisContext? context,
        CancellationToken cancellationToken)
    {
        var activeContext = AcquireContext(context, cancellationToken, out var isPooled);

        // AEGIS-133: Önceden iptal edilmiş token ile hiçbir strateji ve callback çalışmamalı (Polly:
        // Execute_CancellationRequested_Throws). Merkezi giriş noktasında tek seferde garanti edilir.
        if (activeContext.CancellationToken.IsCancellationRequested)
        {
            var cancelled = Outcome<TResult>.FromException(new OperationCanceledException(activeContext.CancellationToken));
            if (isPooled)
            {
                AegisContextPool.Return(activeContext);
            }

            return new ValueTask<Outcome<TResult>>(cancelled);
        }

        var scope = BeginExecution(activeContext, isPooled);

        // Stratejiler (Timeout, Hedging) bağlamın token'ını geçici olarak ikame eder; çağıranın token'ı ŞİMDİ yakalanır.
        var callerToken = activeContext.CancellationToken;
        var pending = InvokeEntry(callback, activeContext, state);

        if (scope.Span is not null)
        {
            // StartActivity Activity.Current'ı çağıranın bağlamında değiştirir ve bu metot async olmadığı için değişiklik çağırana
            // sızar. Geri çağrılar span'ı kendi await noktalarında yakaladı (alt span'lar doğru ebeveyni alır); çağıranınki geri konur.
            Activity.Current = scope.CallerActivity;
        }

        if (pending.IsCompletedSuccessfully)
        {
            var outcome = RestoreCallerToken(pending.Result, callerToken);
            Complete(activeContext, scope, outcome.Exception);
            return new ValueTask<Outcome<TResult>>(outcome);
        }

        return CompleteAsync(pending, activeContext, callerToken, scope);
    }

    // Bağlam verilmediyse havuzdan kiralanır. AEGIS-146: kullanıcı bağlamı adsızsa boru hattı adı yazılır; aksi halde strateji
    // metrikleri "default" etiketine, executions sayacı gerçek ada gider ve aynı isteğin metrikleri iki etikete dağılırdı.
    private AegisContext AcquireContext(AegisContext? context, CancellationToken cancellationToken, out bool isPooled)
    {
        if (context is null)
        {
            isPooled = true;
            return AegisContextPool.Rent(cancellationToken, Name);
        }

        isPooled = false;
        context.PipelineName ??= Name;
        return context;
    }

    // Telemetri ve iz yalnızca bir dinleyici (OpenTelemetry, dotnet-counters, MeterListener) bağlıysa çalışır; dinleyici yokken
    // sıcak yol birkaç bayrak denetimi dışında iş yapmaz (Polly de yalnızca dinleyici varsa ölçer).
    private ExecutionScope BeginExecution(AegisContext context, bool isPooled)
    {
        var measureDuration = AegisTelemetry.ExecutionDurationMs.Enabled || _telemetry.IsPipelineEnabled;
        long startTimestamp = 0;
        if (measureDuration)
        {
            startTimestamp = Stopwatch.GetTimestamp();
            if (_telemetry.HasListeners)
            {
                _telemetry.ReportPipelineExecuting(context); // Polly: PipelineExecuting (yalnızca dinleyicilere)
            }
        }

        if (AegisTelemetry.ExecutionsTotal.Enabled)
        {
            AegisTelemetry.ExecutionsTotal.Add(1, new KeyValuePair<string, object?>("pipeline", Name));
        }

        if (!AegisTelemetry.ActivitySource.HasListeners())
        {
            return new ExecutionScope(isPooled, measureDuration, startTimestamp, span: null, callerActivity: null);
        }

        var callerActivity = Activity.Current;
        return new ExecutionScope(isPooled, measureDuration, startTimestamp, StartSpan(), callerActivity);
    }

    // Giriş bileşenini çağırır; eşzamanlı fırlatılan istisna da sonuca çevrilir (çekirdek asla fırlatmaz).
    private ValueTask<Outcome<TResult>> InvokeEntry<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback, AegisContext context, TState state)
    {
        try
        {
            return _entryComponent.ExecuteAsync(callback, context, state);
        }
        catch (Exception ex)
        {
            return new ValueTask<Outcome<TResult>>(Outcome<TResult>.FromException(ex));
        }
    }

    private async ValueTask<Outcome<TResult>> CompleteAsync<TResult>(
        ValueTask<Outcome<TResult>> pending, AegisContext context, CancellationToken callerToken, ExecutionScope scope)
    {
        Outcome<TResult> outcome;
        try
        {
            outcome = RestoreCallerToken(await pending.ConfigureAwait(false), callerToken);
        }
        catch (Exception ex)
        {
            outcome = RestoreCallerToken(Outcome<TResult>.FromException(ex), callerToken);
        }

        Complete(context, scope, outcome.Exception);
        return outcome;
    }

    /// <summary>Bir çalıştırmanın tamamlanınca kapatılacak kaynakları: havuzdan bağlam, süre ölçümü ve iz.</summary>
    private readonly struct ExecutionScope(bool isPooled, bool measureDuration, long startTimestamp, Activity? span, Activity? callerActivity)
    {
        public bool IsPooled { get; } = isPooled;
        public bool MeasureDuration { get; } = measureDuration;
        public long StartTimestamp { get; } = startTimestamp;
        public Activity? Span { get; } = span;
        public Activity? CallerActivity { get; } = callerActivity;
    }
    /// <summary>
    /// AEGIS-134 (Polly #3086): Timeout ve Hedging gibi stratejiler çağıranın token'ını kendi token'larıyla ikame eder.
    /// Çağıran iptal ettiğinde sızan <see cref="OperationCanceledException"/>, çağıranın hiç görmediği iç token'ı
    /// taşıyordu; özgün istisna InnerException olarak korunarak çağıranın token'ıyla yeniden oluşturulur.
    /// Tüm biçimlerde (fırlatmayan <c>ExecuteOutcomeAsync</c> dahil) aynı kural geçerlidir.
    /// </summary>
    private static Outcome<TResult> RestoreCallerToken<TResult>(Outcome<TResult> outcome, CancellationToken callerToken)
    {
        if (outcome.Exception is OperationCanceledException oce &&
            callerToken.IsCancellationRequested &&
            oce.CancellationToken != callerToken)
        {
            return Outcome<TResult>.FromException(new OperationCanceledException(oce.Message, oce, callerToken));
        }

        return outcome;
    }

    private Activity? StartSpan()
    {
        var span = AegisTelemetry.ActivitySource.StartActivity(_spanName, ActivityKind.Internal);
        if (span is { IsAllDataRequested: true })
        {
            span.SetTag(AegisTelemetryTags.PipelineName, Name);
            if (InstanceName is not null)
            {
                span.SetTag(AegisTelemetryTags.PipelineInstance, InstanceName);
            }
        }

        return span;
    }

    /// <summary>
    /// Span'ı sonuçla kapatır: işlem anahtarı etiketi, başarısızlıkta <c>Error</c> durumu ve istisna türü. İstisna iletisi yazılmaz
    /// (hassas veri sızdırabilir). Span bağlam havuza dönmeden ÖNCE kapatılır; bağlam o zaman hâlâ geçerlidir.
    /// </summary>
    private static void FinishSpan(Activity span, AegisContext context, Exception? exception)
    {
        if (span.IsAllDataRequested)
        {
            if (context.OperationKey is { } operationKey)
            {
                span.SetTag(AegisTelemetryTags.OperationKey, operationKey);
            }

            if (exception is not null)
            {
                span.SetTag(AegisTelemetryTags.ExceptionType, exception.GetType().FullName);
                span.SetStatus(ActivityStatusCode.Error);
            }
        }

        span.Stop();
    }

    private void Complete(AegisContext context, in ExecutionScope scope, Exception? exception)
    {
        if (scope.Span is { } span)
        {
            FinishSpan(span, context, exception);
        }

        if (scope.MeasureDuration)
        {
            var elapsed = Stopwatch.GetElapsedTime(scope.StartTimestamp);
            if (AegisTelemetry.ExecutionDurationMs.Enabled)
            {
                AegisTelemetry.ExecutionDurationMs.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("pipeline", Name));
            }

            // Polly: PipelineExecuted olayı + resilience.polly.pipeline.duration (operation.key, exception.type etiketli)
            _telemetry.ReportPipelineExecuted(context, elapsed, exception);
        }

        if (scope.IsPooled)
        {
            AegisContextPool.Return(context);
        }
    }

    /// <summary>
    /// Async biçimler için sonucu döner. Başarısızlık ASLA eşzamanlı fırlatılmaz: çağıran <c>ExecuteAsync(...)</c>'ı
    /// await etmeden önce (ör. <c>.AsTask()</c>, <c>Task.WhenAny</c>) istisna görmemeli, hatalı görev almalıdır.
    /// Bu yüzden başarısız sonuç async yardımcıdan geçer (istisna/iptal görevin içine yazılır).
    /// </summary>
    private static ValueTask<TResult> UnwrapAsync<TResult>(ValueTask<Outcome<TResult>> pending)
    {
        if (pending.IsCompletedSuccessfully)
        {
            var outcome = pending.Result;
            if (outcome.IsSuccess)
            {
                return new ValueTask<TResult>(outcome.Result!);
            }
        }

        return AwaitUnwrapAsync(pending);
    }

    private static async ValueTask<TResult> AwaitUnwrapAsync<TResult>(ValueTask<Outcome<TResult>> pending) =>
        (await pending.ConfigureAwait(false)).GetResultOrThrow();

    private static ValueTask UnwrapVoidAsync(ValueTask<Outcome<bool>> pending)
    {
        if (pending.IsCompletedSuccessfully && pending.Result.IsSuccess)
        {
            return default;
        }

        return new ValueTask(AwaitUnwrapAsync(pending).AsTask());
    }

    /// <summary>Senkron çağıran için: eşzamanlı tamamlandıysa bloklamadan, değilse bekleyerek sonucu alır.</summary>
    private static TResult UnwrapSync<TResult>(ValueTask<Outcome<TResult>> pending) =>
        (pending.IsCompletedSuccessfully ? pending.Result : pending.AsTask().GetAwaiter().GetResult()).GetResultOrThrow();

    // =============================================================================================================
    // Async — mevcut biçimler (davranış aynı)
    // =============================================================================================================

    public ValueTask<TResult> ExecuteAsync<TResult>(
        Func<AegisContext, ValueTask<TResult>> callback,
        AegisContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return UnwrapAsync(
            ExecuteCoreAsync(static (ctx, cb) => OutcomeInvoker.Invoke(cb, ctx), callback, context, default));
    }

    public ValueTask ExecuteAsync(
        Func<AegisContext, ValueTask> callback,
        AegisContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return UnwrapVoidAsync(
            ExecuteCoreAsync(static (ctx, cb) => OutcomeInvoker.InvokeVoid(static (c, f) => f(c), ctx, cb), callback, context, default));
    }

    private int _disposed;

    /// <summary>
    /// Sahip olunan stratejileri serbest bırakır. İdempotenttir (AEGIS-141): ikinci çağrı hiçbir şey yapmaz;
    /// aksi halde kullanıcı stratejilerinin Dispose'u iki kez çağrılıyordu (Polly: Dispose idempotent).
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var strategy in Strategies)
        {
            if (strategy is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
