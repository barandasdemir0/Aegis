using System.Diagnostics;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Distributed.Abstractions;

/// <summary>
/// Devre kesici durumunu ve sayaçlarını <see cref="ICircuitBreakerStateStore"/> üzerinden tüm pod'lar
/// arasında paylaşan dağıtık Circuit Breaker stratejisi (AEGIS-113).
/// <para>
/// Bir pod devreyi açtığında durum ortak depoya yazılır ve diğer pod'lar bir sonraki
/// <see cref="DistributedCircuitBreakerOptions.StateCacheDuration"/> içinde fail-fast davranışına geçer.
/// Açılma süresi dolunca devre HalfOpen olur ve deneme hakkı depo üzerinden TÜM pod'lar arasında tekildir
/// (<see cref="ICircuitBreakerStateStore.TryAcquireProbeAsync"/>). Depo erişilemezse strateji fail-open çalışır.
/// </para>
/// </summary>
public sealed class DistributedCircuitBreakerStrategy : AegisStrategy, IObservableCircuitState, IObservableSharedState, IManuallyControllableCircuit
{
    private readonly ICircuitBreakerStateStore _stateStore;
    private readonly DistributedCircuitBreakerOptions _staticOptions;

    /// <inheritdoc />
    public override object? Options => _staticOptions;
    private readonly string _ownerId = Guid.NewGuid().ToString("N");
    private readonly object _lock = new();

    private CircuitState _cachedState = CircuitState.Closed;
    private long _cachedStateTimestamp;
    private bool _hasCachedState;
    private bool _localProbeRunning;

    // Bu pod'un gözlediği art arda başarısız deneme sayısı (Polly: HalfOpenAttempts); devre kapanınca sıfırlanır.
    private int _halfOpenAttempts;
    private string? _pipelineName;

    private DistributedCircuitBreakerOptions? _lastGoodOptions;

    /// <summary>Canlı seçenekleri doğrulayarak çözer; geçersizse son geçerli seçeneklerle devam eder (AEGIS-145).</summary>
    private DistributedCircuitBreakerOptions ResolveOptions() => DynamicOptionsResolver.Resolve(_staticOptions, _staticOptions.OptionsProvider, static o => o.Validate(), ref _lastGoodOptions);

    public override string Name => "DistributedCircuitBreaker";

    /// <inheritdoc />
    public bool IsSharedStateAvailable => _stateStore.IsAvailable;

    /// <summary>
    /// En son bilinen devre durumu (yerel önbellekten okunur, uzak depoyu sorgulamaz).
    /// İlk çağrı henüz yapılmadıysa Closed varsayılır.
    /// </summary>
    public CircuitState LastKnownState
    {
        get
        {
            lock (_lock)
            {
                return _hasCachedState ? _cachedState : CircuitState.Closed;
            }
        }
    }

    public DistributedCircuitBreakerStrategy(
        ICircuitBreakerStateStore stateStore,
        DistributedCircuitBreakerOptions options)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _staticOptions = options ?? throw new ArgumentNullException(nameof(options));
        _staticOptions.Validate(); // fail-fast (AEGIS-130)

        options.StateProvider?.Initialize(this);
        options.ManualControl?.Register(this);
    }

    // Manuel kontrol anahtar bilinmeden (CircuitKey yok, boru hattı adı henüz atanmamış) izole ettiyse ilk çağrıda uygulanır.
    private int _pendingManualIsolation;

    private string? ResolveManualKey() => _staticOptions.CircuitKey ?? Telemetry.PipelineName ?? _pipelineName;

    /// <inheritdoc />
    ValueTask IManuallyControllableCircuit.IsolateCircuitAsync(CancellationToken cancellationToken)
    {
        if (ResolveManualKey() is { } key)
        {
            return TransitionManuallyAsync(key, CircuitState.Isolated, cancellationToken);
        }

        Volatile.Write(ref _pendingManualIsolation, 1);
        return default;
    }

    /// <inheritdoc />
    /// <remarks>Kurucuda uzak depoya (Redis) bloklayan yazma yapılmaz; izolasyon ilk çağrıda, çağrı geçmeden uygulanır.</remarks>
    void IManuallyControllableCircuit.IsolateOnRegistration() => Volatile.Write(ref _pendingManualIsolation, 1);

    /// <inheritdoc />
    ValueTask IManuallyControllableCircuit.CloseCircuitAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _pendingManualIsolation, 0);
        return ResolveManualKey() is { } key ? TransitionManuallyAsync(key, CircuitState.Closed, cancellationToken) : default;
    }

    protected override async ValueTask<Outcome<TResult>> ExecuteCoreAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        _pipelineName ??= context.PipelineName; // manuel Isolate/Reset metriklerini etiketlemek için
        var options = ResolveOptions();
        var circuitKey = options.CircuitKey ?? context.PipelineName ?? "default";
        if (Volatile.Read(ref _pendingManualIsolation) == 1 && Interlocked.Exchange(ref _pendingManualIsolation, 0) == 1)
        {
            await IsolateAsync(circuitKey, CancellationToken.None).ConfigureAwait(context.ContinueOnCapturedContext);
        }

        var circuitState = await GetStateAsync(circuitKey, options, context.CancellationToken).ConfigureAwait(context.ContinueOnCapturedContext);

        if (circuitState is CircuitState.Open or CircuitState.Isolated)
        {
            // Red FIRLATILMAZ; sonuç olarak döner ve en dışta bir kez fırlatılır
            // İzolasyon ayrı tipte (Polly: IsolatedCircuitException); kalan süre dağıtık depodan okunmadığından RetryAfter null.
            var message = $"Dağıtık devre kesici '{circuitKey}' {circuitState} durumunda; istek iletilmedi.";
            return Outcome<TResult>.FromException(circuitState == CircuitState.Isolated
                ? new IsolatedCircuitException(message) { TelemetrySource = Telemetry.Source }
                : new BrokenCircuitException(message) { TelemetrySource = Telemetry.Source });
        }

        var isProbe = circuitState == CircuitState.HalfOpen;
        if (isProbe && await AcquireProbeAsync(circuitKey, options, context).ConfigureAwait(context.ContinueOnCapturedContext) is { } rejection)
        {
            return Outcome<TResult>.FromException(rejection);
        }

        try
        {
            var outcome = await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
            if (outcome.Exception is { } exception)
            {
                // İşlenmeyen istisnalar (iptal, açık-devre reddi, ShouldHandle=false) sayılmaz — eski davranışla aynı
                if (options.IsFailure(exception, context.CancellationToken))
                {
                    await RecordOutcomeAsync(circuitKey, options, context, isProbe, isFailure: true, outcome).ConfigureAwait(context.ContinueOnCapturedContext);
                }
            }
            else
            {
                await RecordOutcomeAsync(circuitKey, options, context, isProbe, options.IsFailureResult(outcome.Result), outcome).ConfigureAwait(context.ContinueOnCapturedContext);
            }

            return outcome;
        }
        finally
        {
            if (isProbe)
            {
                await ReleaseProbeAsync(circuitKey).ConfigureAwait(context.ContinueOnCapturedContext);
            }
        }
    }

    /// <summary>
    /// Devreyi tüm pod'lar için elle izole eder (manuel kesme).
    /// </summary>
    public ValueTask IsolateAsync(string circuitKey, CancellationToken cancellationToken = default)
    {
        InvalidateCache();
        AegisTelemetry.RecordCircuitStateChange(_pipelineName ?? circuitKey, CircuitState.Isolated);
        return _stateStore.SetStateAsync(circuitKey, CircuitState.Isolated, TimeSpan.FromDays(365), cancellationToken);
    }

    /// <summary>
    /// Devreyi tüm pod'lar için kapalı (sağlıklı) duruma döndürür.
    /// </summary>
    public ValueTask ResetAsync(string circuitKey, CancellationToken cancellationToken = default)
    {
        InvalidateCache();
        Volatile.Write(ref _halfOpenAttempts, 0);
        AegisTelemetry.RecordCircuitStateChange(_pipelineName ?? circuitKey, CircuitState.Closed);
        return _stateStore.SetStateAsync(circuitKey, CircuitState.Closed, TimeSpan.Zero, cancellationToken);
    }

    /// <summary>Deneme hakkını almaya çalışır; alınamazsa reddi FIRLATMADAN döner (null = hak alındı).</summary>
    private async ValueTask<BrokenCircuitException?> AcquireProbeAsync(string circuitKey, DistributedCircuitBreakerOptions options, AegisContext context)
    {
        // 1. Pod içi kapı: aynı pod'daki eşzamanlı çağrılar depoya hiç gitmeden reddedilir.
        lock (_lock)
        {
            if (_localProbeRunning)
            {
                return new BrokenCircuitException(
                    $"Dağıtık devre kesici '{circuitKey}' yarı açık (HalfOpen) durumda ve deneme isteği yürütülüyor; istek iletilmedi.") { TelemetrySource = Telemetry.Source };
            }

            _localProbeRunning = true;
        }

        // 2. Küme kapısı: tüm pod'lar arasında tek deneme. Hak, BreakDuration sonunda kendiliğinden düşer.
        bool acquired;
        try
        {
            acquired = await _stateStore.TryAcquireProbeAsync(circuitKey, _ownerId, options.BreakDuration, CancellationToken.None)
                .ConfigureAwait(context.ContinueOnCapturedContext);
        }
        catch
        {
            ReleaseLocalProbe();
            throw;
        }

        if (!acquired)
        {
            ReleaseLocalProbe();
            return new BrokenCircuitException(
                $"Dağıtık devre kesici '{circuitKey}' yarı açık (HalfOpen) durumda ve başka bir pod deneme isteği yürütüyor; istek iletilmedi.") { TelemetrySource = Telemetry.Source };
        }

        SetCachedState(CircuitState.HalfOpen);
        Telemetry.Report(AegisEventNames.OnCircuitHalfOpened, AegisEventSeverity.Warning, context);
        await AegisCallbacks.InvokeSafelyAsync(options.OnHalfOpened, CircuitBreakerEventContext.HalfOpened(options.BreakDuration, context), nameof(options.OnHalfOpened)).ConfigureAwait(context.ContinueOnCapturedContext);
        return null;
    }

    private async ValueTask ReleaseProbeAsync(string circuitKey)
    {
        try
        {
            await _stateStore.ReleaseProbeAsync(circuitKey, _ownerId, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Hak bırakılamazsa kiralama süresi sonunda kendiliğinden düşer; çağıranın sonucunu ezmemeli.
        catch
#pragma warning restore CA1031
        {
        }
        finally
        {
            ReleaseLocalProbe();
        }
    }

    private void ReleaseLocalProbe()
    {
        lock (_lock)
        {
            _localProbeRunning = false;
        }
    }

    private async ValueTask<CircuitState> GetStateAsync(
        string circuitKey,
        DistributedCircuitBreakerOptions options,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_hasCachedState && Stopwatch.GetElapsedTime(_cachedStateTimestamp) < options.StateCacheDuration)
            {
                return _cachedState;
            }
        }

        var state = await _stateStore.GetStateAsync(circuitKey, cancellationToken).ConfigureAwait(false);
        SetCachedState(state);
        return state;
    }

    /// <summary>
    /// Çağrı sonucunu depoya işler. İşlem zaten tamamlandığı için çağıranın iptal jetonu kullanılmaz:
    /// başarılı bir sonuç, sonradan gelen iptal yüzünden kaybolmamalı ve HalfOpen kararı yarım kalmamalıdır.
    /// </summary>
    private async ValueTask RecordOutcomeAsync<TResult>(
        string circuitKey,
        DistributedCircuitBreakerOptions options,
        AegisContext context,
        bool isProbe,
        bool isFailure,
        Outcome<TResult> outcome)
    {
        if (isProbe && !isFailure)
        {
            // Deneme isteği başarılı: devreyi tüm pod'lar için kapat (depo sayaçları da sıfırlar)
            await _stateStore.SetStateAsync(circuitKey, CircuitState.Closed, TimeSpan.Zero, CancellationToken.None).ConfigureAwait(context.ContinueOnCapturedContext);
            SetCachedState(CircuitState.Closed);
            Volatile.Write(ref _halfOpenAttempts, 0);
            AegisTelemetry.RecordCircuitStateChange(context.PipelineName, CircuitState.Closed);
            var closed = CircuitBreakerEventContext.Closed(options.BreakDuration, context, outcome);
            Telemetry.Report(AegisEventNames.OnCircuitClosed, AegisEventSeverity.Information, context, arguments: closed);
            await AegisCallbacks.InvokeSafelyAsync(options.OnClosed, closed, nameof(options.OnClosed)).ConfigureAwait(context.ContinueOnCapturedContext);
            return;
        }

        if (isProbe)
        {
            Interlocked.Increment(ref _halfOpenAttempts);
            await OpenAsync(circuitKey, options, context, CircuitState.HalfOpen, new CircuitHealth(0, 1, 0), outcome).ConfigureAwait(context.ContinueOnCapturedContext);
            return;
        }

        var (successCount, failureCount) = await _stateStore
            .RecordResultAsync(circuitKey, !isFailure, options.SamplingDuration, CancellationToken.None)
            .ConfigureAwait(context.ContinueOnCapturedContext);

        if (isFailure && options.IsFailureThresholdExceeded(successCount, failureCount))
        {
            await OpenAsync(circuitKey, options, context, CircuitState.Closed, new CircuitHealth(successCount, failureCount, 0), outcome).ConfigureAwait(context.ContinueOnCapturedContext);
        }
    }

    private async ValueTask OpenAsync<TResult>(
        string circuitKey, DistributedCircuitBreakerOptions options, AegisContext context, CircuitState oldState, CircuitHealth health, Outcome<TResult> outcome)
    {
        var halfOpenAttempts = Volatile.Read(ref _halfOpenAttempts);
        var breakDuration = options.ResolveBreakDuration(
            CircuitBreakerEventContext.Opened(oldState, options.BreakDuration, context, health, halfOpenAttempts, outcome));

        await _stateStore.SetStateAsync(circuitKey, CircuitState.Open, breakDuration, CancellationToken.None).ConfigureAwait(context.ContinueOnCapturedContext);
        SetCachedState(CircuitState.Open);
        AegisTelemetry.RecordCircuitStateChange(context.PipelineName, CircuitState.Open);

        var opened = CircuitBreakerEventContext.Opened(oldState, breakDuration, context, health, halfOpenAttempts, outcome);
        Telemetry.Report(AegisEventNames.OnCircuitOpened, AegisEventSeverity.Error, context, opened.Exception, opened.Result, opened);
        await AegisCallbacks.InvokeSafelyAsync(options.OnOpened, opened, nameof(options.OnOpened)).ConfigureAwait(context.ContinueOnCapturedContext);
    }

    // ManualControl / pano yolu: durum gerçekten değiştiyse OnOpened / OnClosed IsManual = true ile bildirilir (yerel devreyle aynı).
    private async ValueTask TransitionManuallyAsync(string circuitKey, CircuitState newState, CancellationToken cancellationToken)
    {
        var oldState = await _stateStore.GetStateAsync(circuitKey, cancellationToken).ConfigureAwait(false);
        if (newState == CircuitState.Isolated)
        {
            await IsolateAsync(circuitKey, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ResetAsync(circuitKey, cancellationToken).ConfigureAwait(false);
        }

        if (oldState == newState)
        {
            return;
        }

        var options = ResolveOptions();
        var context = new AegisContext(cancellationToken, _pipelineName);
        var transition = CircuitBreakerEventContext.Manual(oldState, newState, options.BreakDuration, context);
        if (newState == CircuitState.Closed)
        {
            Telemetry.Report(AegisEventNames.OnCircuitClosed, AegisEventSeverity.Information, context, arguments: transition);
            await AegisCallbacks.InvokeSafelyAsync(options.OnClosed, transition, nameof(options.OnClosed)).ConfigureAwait(false);
        }
        else
        {
            Telemetry.Report(AegisEventNames.OnCircuitOpened, AegisEventSeverity.Error, context, arguments: transition);
            await AegisCallbacks.InvokeSafelyAsync(options.OnOpened, transition, nameof(options.OnOpened)).ConfigureAwait(false);
        }
    }

    private void SetCachedState(CircuitState state)
    {
        lock (_lock)
        {
            _cachedState = state;
            _cachedStateTimestamp = Stopwatch.GetTimestamp();
            _hasCachedState = true;
        }
    }

    private void InvalidateCache()
    {
        lock (_lock)
        {
            _hasCachedState = false;
        }
    }
}
