using System.Runtime.CompilerServices;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Strategies.CircuitBreaker;

/// <summary>
/// Hedef servisin sürekli hata vermesi veya aşırı yavaşlaması (Slow Call Rate) durumunda
/// sistemi ve hedefi korumak için devreyi açan Circuit Breaker stratejisi.
/// <para>
/// Kapalı durumda sağlık, <see cref="HealthWindow.BucketCount"/> dilimli kayan pencerede izlenir. Açılma süresi dolunca
/// ilk çağrı devreyi HalfOpen'a alır ve tek deneme isteği (probe) olarak geçer; diğerleri reddedilir.
/// Durumu okumak (<see cref="State"/>) hiçbir geçiş yapmaz; geçişler ve olaylar yalnızca çağrı yolunda gerçekleşir.
/// </para>
/// </summary>
public sealed class CircuitBreakerStrategy : AegisStrategy, IObservableCircuitState, IManuallyControllableCircuit
{
    private const long NoProbe = 0;

    private readonly CircuitBreakerOptions _staticOptions;

    /// <inheritdoc />
    public override object? Options => _staticOptions;
    private readonly AegisLock _lock = new();
    private readonly HealthWindow _window;

    // Art arda işlenen hata sayısı (ConsecutiveFailureThreshold); kilit altında, yalnızca Closed durumunda değişir.
    private int _consecutiveFailures;

    // Art arda başarısız HalfOpen deneme sayısı (Polly HalfOpenAttempts); kilit altında, kapanınca sıfırlanır.
    private int _halfOpenAttempts;

    // HalfOpen'da art arda başarılı deneme sayısı (HalfOpenSuccessThreshold'a ulaşınca devre kapanır).
    private int _halfOpenSuccesses;

    // volatile: kapalı devrede ReserveProbe kilitsiz okur (en sık yol). Tüm geçişler yine kilit altında yazılır.
    private volatile CircuitState _state = CircuitState.Closed;
    private long _lastStateChangeTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(); // = TimeProvider.System.GetTimestamp()
    private TimeSpan _currentBreakDuration;

    // Deneme isteği kimliği: bool bayrak yerine kimlik tutulur ki, devre yeniden açılıp yeni bir deneme başladıktan sonra
    // biten ESKİ bir denemenin finally bloğu yeni denemenin kilidini yanlışlıkla serbest bırakamasın.
    private long _activeProbeId = NoProbe;
    private long _probeSequence;
    private string? _pipelineName;

    private CircuitBreakerOptions? _lastGoodOptions;

    /// <summary>Canlı seçenekleri doğrulayarak çözer; geçersizse son geçerli seçeneklerle devam eder (AEGIS-145).</summary>
    private CircuitBreakerOptions ResolveOptions() => DynamicOptionsResolver.Resolve(_staticOptions, _staticOptions.OptionsProvider, static o => o.Validate(), ref _lastGoodOptions);

    public override string Name => "CircuitBreaker";

    /// <summary>
    /// Güncel etkin durum. Yan etkisizdir: açılma süresi dolmuş bir devre için <see cref="CircuitState.HalfOpen"/> raporlar
    /// ancak geçişi yapmaz; geçiş ve <c>OnHalfOpened</c> olayı bir sonraki çağrıya aittir. Health check ve pano
    /// yoklamaları bu sayede olayları "yutamaz".
    /// </summary>
    public CircuitState State
    {
        get
        {
            lock (_lock)
            {
                return IsBreakElapsed() ? CircuitState.HalfOpen : _state;
            }
        }
    }

    /// <inheritdoc />
    CircuitState IObservableCircuitState.LastKnownState => State;

    public CircuitBreakerStrategy(CircuitBreakerOptions options)
    {
        _staticOptions = options ?? throw new ArgumentNullException(nameof(options));
        _staticOptions.Validate(); // fail-fast: geçersiz yapılandırma kurulum anında bildirilir (AEGIS-130)
        _currentBreakDuration = options.BreakDuration;
        _window = new HealthWindow(options.SamplingDuration);

        // Polly: ManualControl / StateProvider kurulumdan önce oluşturulur ve devre kesici kurulurken bağlanır.
        options.StateProvider?.Initialize(this);
        options.ManualControl?.Register(this);
    }

    /// <inheritdoc />
    ValueTask IManuallyControllableCircuit.IsolateCircuitAsync(CancellationToken cancellationToken) =>
        ReportManualTransitionAsync(Transition(CircuitState.Isolated), CircuitState.Isolated, cancellationToken);

    /// <inheritdoc />
    void IManuallyControllableCircuit.IsolateOnRegistration() => Transition(CircuitState.Isolated);

    /// <inheritdoc />
    ValueTask IManuallyControllableCircuit.CloseCircuitAsync(CancellationToken cancellationToken) =>
        ReportManualTransitionAsync(Transition(CircuitState.Closed), CircuitState.Closed, cancellationToken);

    // ManualControl / pano yolu: durum gerçekten değiştiyse OnOpened / OnClosed IsManual = true ile bildirilir (Polly ile aynı).
    private ValueTask ReportManualTransitionAsync(CircuitState oldState, CircuitState newState, CancellationToken cancellationToken)
    {
        if (oldState == newState)
        {
            return default;
        }

        var options = ResolveOptions();
        var context = new AegisContext(cancellationToken, _pipelineName);
        var transition = CircuitBreakerEventContext.Manual(oldState, newState, _currentBreakDuration, context);
        return newState == CircuitState.Closed
            ? ReportTransitionAsync(options, context, openedEvent: null, closedEvent: transition)
            : ReportTransitionAsync(options, context, openedEvent: transition, closedEvent: null);
    }

    // En sık yol (kapalı devre, deneme isteği ve olay yok, koşul senkron, süre ölçümü yok) async değildir: geri çağrı
    // eşzamanlı tamamlanırsa sonuç kilit altında kaydedilip doğrudan döner. Diğer her durum ExecuteGeneralAsync'tedir.
    protected override ValueTask<Outcome<TResult>> ExecuteCoreAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        var options = ResolveOptions();
        if (options.Mode == CircuitBreakerMode.Disabled)
        {
            return callback(context, state); // geçirgen: kayıt, durum ve olay yok
        }

        _pipelineName ??= context.PipelineName; // manuel Isolate/Reset metriklerini etiketlemek için
        var probeId = ReserveProbe(context, out var halfOpenedEvent, out var rejection);
        if (rejection is not null and not IsolatedCircuitException && options.Mode == CircuitBreakerMode.Shadow)
        {
            // Gölge kip: otomatik açılan devre isteği reddetmez (sonucu açık devrede yok sayılır); olaylar ve metrikler normal
            // üretilir. Elle izolasyon bir operatör kararıdır ve gölge kipte de uygulanır.
            rejection = null;
        }

        if (rejection != null)
        {
            // Açık devre reddi FIRLATILMAZ; sonuç olarak döner ve en dışta bir kez fırlatılır (Polly ile aynı maliyet).
            return new ValueTask<Outcome<TResult>>(Outcome<TResult>.FromException(rejection));
        }

        if (probeId != NoProbe || halfOpenedEvent != null || options.ShouldHandleOutcome != null || options.SlowCallDurationThreshold != null)
        {
            return ExecuteGeneralAsync(callback, context, state, options, probeId, halfOpenedEvent);
        }

        var pending = callback(context, state);
        if (!pending.IsCompletedSuccessfully)
        {
            return AwaitAndRecordAsync(pending, options, context);
        }

        var outcome = pending.Result;
        var report = RecordClassifiedAsync(options, context, NoProbe, outcome, TimeSpan.Zero);
        return report.IsCompletedSuccessfully ? new ValueTask<Outcome<TResult>>(outcome) : ReturnAfterReportAsync(report, outcome, context);
    }

#if !AEGIS_LEGACY // .NET 8+: iç katman yalnızca Aegis tarafından tek sefer beklenir; async durum makinesi kutusu havuzlanır
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<Outcome<TResult>> AwaitAndRecordAsync<TResult>(
        ValueTask<Outcome<TResult>> pending, CircuitBreakerOptions options, AegisContext context)
    {
        var outcome = await pending.ConfigureAwait(context.ContinueOnCapturedContext);
        await RecordClassifiedAsync(options, context, NoProbe, outcome, TimeSpan.Zero).ConfigureAwait(context.ContinueOnCapturedContext);
        return outcome;
    }

    private static async ValueTask<Outcome<TResult>> ReturnAfterReportAsync<TResult>(ValueTask report, Outcome<TResult> outcome, AegisContext context)
    {
        await report.ConfigureAwait(context.ContinueOnCapturedContext);
        return outcome;
    }

#if !AEGIS_LEGACY // .NET 8+: iç katman yalnızca Aegis tarafından tek sefer beklenir; async durum makinesi kutusu havuzlanır
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<Outcome<TResult>> ExecuteGeneralAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state,
        CircuitBreakerOptions options,
        long probeId,
        CircuitBreakerEventContext? halfOpenedEvent)
    {
        if (halfOpenedEvent != null)
        {
            Telemetry.Report(AegisEventNames.OnCircuitHalfOpened, AegisEventSeverity.Warning, context, arguments: halfOpenedEvent);
            await AegisCallbacks.InvokeSafelyAsync(options.OnHalfOpened, halfOpenedEvent, nameof(options.OnHalfOpened)).ConfigureAwait(context.ContinueOnCapturedContext);
        }

        // Süre yalnızca yavaş çağrı eşiği tanımlıysa ölçülür (aksi halde kullanılmıyordu; çağrı başına iki saat okuması tasarruf).
        var measureSlowCalls = options.SlowCallDurationThreshold is not null;
        var startTimestamp = measureSlowCalls ? GetTimestamp() : 0;
        try
        {
            var outcome = await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
            var elapsed = measureSlowCalls ? GetElapsedTime(startTimestamp) : TimeSpan.Zero;
            if (options.ShouldHandleOutcome is { } predicate)
            {
                // Retler ve çağıranın kendi iptali sayılmaz; diğer her sonuç koşula sorulur ve kaydedilir.
                if (outcome.Exception is not { } neutral || !CircuitBreakerRules.IsCircuitNeutral(neutral, context.CancellationToken))
                {
                    var isFailure = await predicate.ShouldHandleAsync(new OutcomeArguments<TResult>(outcome, context, 0)).ConfigureAwait(context.ContinueOnCapturedContext);
                    await RecordOutcomeAsync(options, context, probeId, isFailure, elapsed, outcome).ConfigureAwait(context.ContinueOnCapturedContext);
                }
            }
            else
            {
                await RecordClassifiedAsync(options, context, probeId, outcome, elapsed).ConfigureAwait(context.ContinueOnCapturedContext);
            }

            return outcome;
        }
        finally
        {
            if (probeId != NoProbe)
            {
                lock (_lock)
                {
                    if (_activeProbeId == probeId)
                    {
                        _activeProbeId = NoProbe;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Koşul oluşturucusuz sınıflandırma: işlenmeyen istisnalar (iptal, açık-devre reddi, ShouldHandle=false) sayılmaz,
    /// sonuçlar ShouldHandleResult'a göre hata ya da başarı olarak kaydedilir.
    /// </summary>
    private ValueTask RecordClassifiedAsync<TResult>(
        CircuitBreakerOptions options, AegisContext context, long probeId, in Outcome<TResult> outcome, TimeSpan elapsed)
    {
        if (outcome.Exception is { } exception)
        {
            return options.IsFailure(exception, context.CancellationToken) ? RecordOutcomeAsync(options, context, probeId, isFailure: true, elapsed, outcome) : default;
        }

        return RecordOutcomeAsync(options, context, probeId, options.IsFailureResult(outcome.Result), elapsed, outcome);
    }

    /// <summary>
    /// Çağrının geçip geçemeyeceğine karar verir. Açılma süresi dolmuşsa Open -> HalfOpen geçişini burada yapar.
    /// Deneme isteği ise sıfırdan farklı bir kimlik döner. Reddedilirse <paramref name="rejection"/> dolar (fırlatılmaz).
    /// </summary>
    private long ReserveProbe(AegisContext context, out CircuitBreakerEventContext? halfOpenedEvent, out BrokenCircuitException? rejection)
    {
        halfOpenedEvent = null;
        rejection = null;

        // Kilitsiz hızlı yol: kapalı devrede karar verilecek bir şey yok (açılma süresi yalnızca Open'da işler). Okuma ile
        // bu çağrının başlaması arasında devre açılırsa sonuç, kilitli okumadaki yarışla aynıdır: çağrı geçer, sonucu
        // RecordOutcomeAsync'te "devre artık kapalı değil" diye yok sayılır.
        if (_state == CircuitState.Closed)
        {
            return NoProbe;
        }

        lock (_lock)
        {
            if (IsBreakElapsed())
            {
                _state = CircuitState.HalfOpen;
                _lastStateChangeTimestamp = GetTimestamp();
                _activeProbeId = NoProbe;
                _halfOpenSuccesses = 0;
                AegisTelemetry.RecordCircuitStateChange(context.PipelineName, CircuitState.HalfOpen);
                halfOpenedEvent = CircuitBreakerEventContext.HalfOpened(_currentBreakDuration, context);
            }

            switch (_state)
            {
                case CircuitState.Closed:
                    return NoProbe;

                case CircuitState.HalfOpen when _activeProbeId == NoProbe:
                    _activeProbeId = ++_probeSequence;
                    return _activeProbeId;

                // Takılan deneme isteği (zaman aşımı olmayan, hiç bitmeyen çağrı) devreyi sonsuza dek HalfOpen'da tutup herkesi
                // reddederdi (Polly'de de böyle; resilience4j: maxWaitDurationInHalfOpenState). Bir açık kalma süresinden uzun
                // süren deneme terk edilir ve yenisine izin verilir; eskisinin sonucu kimlik denetimiyle yok sayılır.
                case CircuitState.HalfOpen when GetElapsedTime(_lastStateChangeTimestamp) >= _currentBreakDuration:
                    _lastStateChangeTimestamp = GetTimestamp();
                    _activeProbeId = ++_probeSequence;
                    return _activeProbeId;

                case CircuitState.HalfOpen:
                    rejection = new BrokenCircuitException(RejectionMessage(context.PipelineName, probeRunning: true)) { TelemetrySource = Telemetry.Source };
                    return NoProbe;

                case CircuitState.Isolated:
                    rejection = new IsolatedCircuitException(RejectionMessage(context.PipelineName, probeRunning: false)) { TelemetrySource = Telemetry.Source };
                    return NoProbe;

                default:
                    // Açık devre: HalfOpen'a kalan süre yeniden deneme önerisidir (Polly: BrokenCircuitException.RetryAfter).
                    var remaining = _currentBreakDuration - GetElapsedTime(_lastStateChangeTimestamp);
                    rejection = new BrokenCircuitException(
                        RejectionMessage(context.PipelineName, probeRunning: false), remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) { TelemetrySource = Telemetry.Source };
                    return NoProbe;
            }
        }
    }

    // Kayıt kilit altında ve senkrondur; yalnızca durum değiştiyse (olay varsa) async bildirim yoluna girilir.
    private ValueTask RecordOutcomeAsync<TResult>(
        CircuitBreakerOptions options,
        AegisContext context,
        long probeId,
        bool isFailure,
        TimeSpan elapsed,
        in Outcome<TResult> outcome)
    {
        CircuitBreakerEventContext? openedEvent = null;
        CircuitBreakerEventContext? closedEvent = null;

        lock (_lock)
        {
            var now = GetTimestamp();
            var isSlow = options.IsSlow(elapsed);

            if (probeId != NoProbe)
            {
                // Yalnızca hâlâ geçerli olan deneme isteği HalfOpen kararını verir (manuel Isolate/Reset sonrası sonuç yok sayılır).
                if (_activeProbeId == probeId && _state == CircuitState.HalfOpen)
                {
                    if (isFailure)
                    {
                        // HalfOpen'da pencere yok: tek deneme başarısız oldu.
                        _halfOpenAttempts++;
                        openedEvent = OpenCircuit(options, context, CircuitState.HalfOpen, new CircuitHealth(0, 1, isSlow ? 1 : 0), outcome);
                    }
                    else if (++_halfOpenSuccesses >= options.HalfOpenSuccessThreshold)
                    {
                        closedEvent = CloseCircuit(options, context, now, isSlow, outcome);
                    }
                    else
                    {
                        // Eşiğe ulaşılmadı: devre HalfOpen kalır, sıradaki çağrı yeni deneme isteği olur (yine birer birer).
                        _activeProbeId = NoProbe;
                    }
                }
            }
            else if (_state == CircuitState.Closed)
            {
                _window.Configure(options.SamplingDuration);
                _window.ConfigureCount(options.SamplingCount);
                _window.Record(now, isFailure, isSlow);
                _consecutiveFailures = isFailure ? _consecutiveFailures + 1 : 0;

                // Hata oranı yalnızca hata kaydında değerlendirilir (Polly); yavaş çağrı oranı her kayıtta (Resilience4j).
                // Pencere toplamı yalnızca devreyi açabilecek durumda hesaplanır: yavaş çağrı oranı kapalıyken başarı kaydı
                // devreyi asla açamaz (sonuç aynı, başarı yolunda 10 dilimlik tarama atlanır).
                if (isFailure || options.SlowCallRateThreshold < 1.0)
                {
                    var health = _window.Snapshot(now);
                    var shouldOpen = (isFailure && (options.IsFailureThresholdExceeded(health.SuccessCount, health.FailureCount) ||
                                                   options.IsConsecutiveFailureThresholdReached(_consecutiveFailures))) ||
                                     options.IsSlowCallThresholdExceeded(health.Throughput, health.SlowCallCount);
                    if (shouldOpen)
                    {
                        openedEvent = OpenCircuit(options, context, CircuitState.Closed, health, outcome);
                    }
                }
            }

            // Diğer durumlar: çağrı sürerken devre başka bir çağrı veya manuel işlemle açıldı/izole edildi; sonuç yok sayılır.
        }

        return openedEvent is null && closedEvent is null ? default : ReportTransitionAsync(options, context, openedEvent, closedEvent);
    }

    private async ValueTask ReportTransitionAsync(
        CircuitBreakerOptions options, AegisContext context, CircuitBreakerEventContext? openedEvent, CircuitBreakerEventContext? closedEvent)
    {
        if (openedEvent != null)
        {
            Telemetry.Report(AegisEventNames.OnCircuitOpened, AegisEventSeverity.Error, context, arguments: openedEvent);
            await AegisCallbacks.InvokeSafelyAsync(options.OnOpened, openedEvent, nameof(options.OnOpened)).ConfigureAwait(context.ContinueOnCapturedContext);
        }
        else if (closedEvent != null)
        {
            Telemetry.Report(AegisEventNames.OnCircuitClosed, AegisEventSeverity.Information, context, arguments: closedEvent);
            await AegisCallbacks.InvokeSafelyAsync(options.OnClosed, closedEvent, nameof(options.OnClosed)).ConfigureAwait(context.ContinueOnCapturedContext);
        }
    }

    // Olay yalnızca durum değişince oluşturulur; sonucun kutulanması (Result) da yalnızca burada olur.
    private CircuitBreakerEventContext OpenCircuit<TResult>(
        CircuitBreakerOptions options, AegisContext context, CircuitState oldState, CircuitHealth health, in Outcome<TResult> outcome)
    {
        _state = CircuitState.Open;
        _lastStateChangeTimestamp = GetTimestamp();
        _activeProbeId = NoProbe;
        AegisTelemetry.RecordCircuitStateChange(context.PipelineName, CircuitState.Open);

        // Süre üreticisi, açılma olayını (yapılandırılmış süreyle) görür; bildirilen olay çözülen süreyi taşır.
        _currentBreakDuration = options.ResolveBreakDuration(
            CircuitBreakerEventContext.Opened(oldState, options.BreakDuration, context, health, _halfOpenAttempts, outcome));
        return CircuitBreakerEventContext.Opened(oldState, _currentBreakDuration, context, health, _halfOpenAttempts, outcome);
    }

    private CircuitBreakerEventContext CloseCircuit<TResult>(
        CircuitBreakerOptions options, AegisContext context, long now, bool probeWasSlow, in Outcome<TResult> outcome)
    {
        _state = CircuitState.Closed;
        _lastStateChangeTimestamp = now;
        _activeProbeId = NoProbe;
        _window.Configure(options.SamplingDuration);
        _window.ConfigureCount(options.SamplingCount);
        _consecutiveFailures = 0;
        _halfOpenAttempts = 0;
        _window.Reset();
        _window.Record(now, isFailure: false, probeWasSlow);
        AegisTelemetry.RecordCircuitStateChange(context.PipelineName, CircuitState.Closed);

        return CircuitBreakerEventContext.Closed(_currentBreakDuration, context, outcome);
    }

    // Red mesajı (boru hattı adı, durum) başına bir kez biçimlendirilir: açık devrede her red aynı metni üretiyordu
    // (çağrı başına ~130 B string). Metin birebir aynıdır. _lock altında çağrılır.
    private string? _cachedRejectionMessage;
    private string? _cachedRejectionPipeline;
    private CircuitState _cachedRejectionState;
    private bool _cachedRejectionProbe;

    private string RejectionMessage(string? pipelineName, bool probeRunning)
    {
        if (_cachedRejectionMessage is { } cached &&
            _cachedRejectionState == _state &&
            _cachedRejectionProbe == probeRunning &&
            string.Equals(_cachedRejectionPipeline, pipelineName, StringComparison.Ordinal))
        {
            return cached;
        }

        var message = probeRunning
            ? $"Devre kesici '{pipelineName ?? "default"}' yarı açık (HalfOpen) durumda ve deneme isteği yürütülüyor; istek iletilmedi."
            : $"Devre kesici '{pipelineName ?? "default"}' {_state} durumunda; istek iletilmedi.";

        _cachedRejectionMessage = message;
        _cachedRejectionPipeline = pipelineName;
        _cachedRejectionState = _state;
        _cachedRejectionProbe = probeRunning;
        return message;
    }

    /// <inheritdoc />
    protected override void OnTimeProviderChanged()
    {
        lock (_lock)
        {
            _lastStateChangeTimestamp = GetTimestamp();
            _window.UseFrequency(TimeProvider.TimestampFrequency);
        }
    }

    private bool IsBreakElapsed() =>
        _state == CircuitState.Open && GetElapsedTime(_lastStateChangeTimestamp) >= _currentBreakDuration;

    /// <summary>Devreyi elle izole eder (olay bildirmez; bildirimli yol <see cref="CircuitBreakerManualControl"/>).</summary>
    public void Isolate() => Transition(CircuitState.Isolated);

    /// <summary>Devreyi elle kapatır ve pencereyi sıfırlar (olay bildirmez; bildirimli yol <see cref="CircuitBreakerManualControl"/>).</summary>
    public void Reset() => Transition(CircuitState.Closed);

    /// <summary>Elle durum değişikliği (izole ya da kapalı); önceki durumu döner.</summary>
    private CircuitState Transition(CircuitState newState)
    {
        lock (_lock)
        {
            var oldState = _state;
            _state = newState;
            _lastStateChangeTimestamp = GetTimestamp();
            _activeProbeId = NoProbe;
            if (newState == CircuitState.Closed)
            {
                _window.Reset();
                _consecutiveFailures = 0;
                _halfOpenAttempts = 0;
            }

            AegisTelemetry.RecordCircuitStateChange(_pipelineName, newState);
            return oldState;
        }
    }
}
