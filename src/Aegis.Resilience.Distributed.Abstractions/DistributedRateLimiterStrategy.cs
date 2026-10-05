using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.RateLimiter;

namespace Aegis.Resilience.Distributed.Abstractions;

/// <summary>
/// Kotayı <see cref="IDistributedRateLimitStore"/> üzerinden tüm pod'lar arasında paylaşan hız sınırlayıcı. Ör. üçüncü
/// taraf API'nin "saniyede 50" sınırı, 10 pod çalışırken de toplamda aşılmaz. Red; sayaç, telemetri, <c>OnRejected</c>
/// ve <c>RetryAfter</c> ile diğer sınırlayıcılarla aynıdır (<see cref="RateLimitRejectedException"/>).
/// </summary>
public sealed class DistributedRateLimiterStrategy : AegisStrategy, IObservableSharedState
{
    private readonly IDistributedRateLimitStore _store;
    private readonly DistributedRateLimiterOptions _staticOptions;
    private DistributedRateLimiterOptions? _lastGoodOptions;

    public DistributedRateLimiterStrategy(IDistributedRateLimitStore store, DistributedRateLimiterOptions options)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _staticOptions = options ?? throw new ArgumentNullException(nameof(options));
        _staticOptions.Validate();
    }

    /// <inheritdoc />
    public override string Name => "DistributedRateLimiter";

    /// <inheritdoc />
    public override object? Options => _staticOptions;

    /// <inheritdoc />
    public bool IsSharedStateAvailable => _store.IsAvailable;

    private DistributedRateLimiterOptions ResolveOptions() =>
        DynamicOptionsResolver.Resolve(_staticOptions, _staticOptions.OptionsProvider, static o => o.Validate(), ref _lastGoodOptions);

    // Depo eşzamanlı yanıt verirse (bellek içi depo) async durum makinesi kurulmaz: izin varsa geri çağrının ValueTask'ı
    // doğrudan döner. Gerçekten async depo (Redis) AwaitDecisionAsync'ten geçer.
    protected override ValueTask<Outcome<TResult>> ExecuteCoreAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        var options = ResolveOptions();
        var key = ResolveKey(options, context);
        var pending = _store.TryAcquireAsync(key, options.ToRule(), permitCount: 1, context.CancellationToken);
        return pending.IsCompletedSuccessfully
            ? Continue(pending.Result, callback, context, state, options, key)
            : AwaitDecisionAsync(pending, callback, context, state, options, key);
    }

    private async ValueTask<Outcome<TResult>> AwaitDecisionAsync<TResult, TState>(
        ValueTask<DistributedRateLimitDecision> pending,
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state,
        DistributedRateLimiterOptions options,
        string key)
    {
        var decision = await pending.ConfigureAwait(context.ContinueOnCapturedContext);
        return await Continue(decision, callback, context, state, options, key).ConfigureAwait(context.ContinueOnCapturedContext);
    }

    private ValueTask<Outcome<TResult>> Continue<TResult, TState>(
        DistributedRateLimitDecision decision,
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state,
        DistributedRateLimiterOptions options,
        string key) =>
        decision.IsAcquired
            ? callback(context, state)
            : RateLimiterRejection.RejectAsync<TResult>(
                Telemetry, context, Name, options.OnRejected,
                $"Dağıtık hız sınırı aşıldı ('{key}': {options.PermitLimit} / {options.Window.TotalMilliseconds:F0} ms).",
                decision.RetryAfter);

    private static string ResolveKey(DistributedRateLimiterOptions options, AegisContext context)
    {
        var limiterKey = options.LimiterKey ?? context.PipelineName ?? "default";
        return options.PartitionKeySelector?.Invoke(context) is { } partition ? $"{limiterKey}:{partition}" : limiterKey;
    }
}
