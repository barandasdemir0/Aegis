using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Strategies.Chaos;

/// <summary>
/// Sistem dayanıklılığını test etmek için yapay gecikmeler ve istisnalar enjekte eden Kaos stratejisi.
/// </summary>
public sealed class ChaosStrategy : AegisStrategy
{
    private readonly ChaosOptions _staticOptions;

    /// <inheritdoc />
    public override object? Options => _staticOptions;

    private ChaosOptions? _lastGoodOptions;

    /// <summary>Canlı seçenekleri doğrulayarak çözer; geçersizse son geçerli seçeneklerle devam eder (AEGIS-145).</summary>
    private ChaosOptions ResolveOptions() => DynamicOptionsResolver.Resolve(_staticOptions, _staticOptions.OptionsProvider, static o => o.Validate(), ref _lastGoodOptions);

    public override string Name => "Chaos";

    public ChaosStrategy(ChaosOptions options)
    {
        _staticOptions = options ?? throw new ArgumentNullException(nameof(options));
        _staticOptions.Validate(); // fail-fast: geçersiz yapılandırma kurulum anında bildirilir (AEGIS-130)
    }

    protected override async ValueTask<Outcome<TResult>> ExecuteCoreAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        var options = ResolveOptions();

        if (await ShouldInjectAsync(options, context).ConfigureAwait(context.ContinueOnCapturedContext))
        {
            AegisTelemetry.ChaosInjectionsTotal.Add(1, new KeyValuePair<string, object?>("pipeline", context.PipelineName ?? "default"));

            // 1. Özel davranış enjeksiyonu (Simmy Chaos Behavior)
            if (options.BehaviorGenerator != null)
            {
                await NotifyAsync(options, context, AegisEventNames.ChaosOnBehavior, ChaosInjectionKind.Behavior).ConfigureAwait(context.ContinueOnCapturedContext);
                await options.BehaviorGenerator(context).ConfigureAwait(context.ContinueOnCapturedContext);
            }

            // 2. Gecikme enjeksiyonu (çağrı başına üretici varsa o; sıfır veya negatif = gecikme yok)
            var latency = options.LatencyGenerator is { } latencyGenerator
                ? await latencyGenerator(context).ConfigureAwait(context.ContinueOnCapturedContext)
                : options.Latency;
            if (latency > TimeSpan.Zero)
            {
                await NotifyAsync(options, context, AegisEventNames.ChaosOnLatency, ChaosInjectionKind.Latency, latency: latency)
                    .ConfigureAwait(context.ContinueOnCapturedContext);
                await Task.Delay(AegisTimers.Normalize(latency), TimeProvider, context.CancellationToken).ConfigureAwait(context.ContinueOnCapturedContext);
            }

            // 3. Ağırlıklı sonuç üretici (Polly OutcomeGenerator): ağırlığa göre istisna veya sonuç
            if (options.OutcomeGenerator is { } generator)
            {
                if (generator.Generate(context, NextRandom(options)) is not { } picked)
                {
                    return await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext); // boş üretici: enjeksiyon yok
                }

                return picked.Exception is { } pickedException
                    ? await InjectFaultAsync<TResult>(options, context, pickedException).ConfigureAwait(context.ContinueOnCapturedContext)
                    : await InjectResultAsync<TResult>(options, context, picked.Result, nameof(ChaosOptions.OutcomeGenerator)).ConfigureAwait(context.ContinueOnCapturedContext);
            }

            // 4. Sonuç enjeksiyonu (Simmy Chaos Result - istisna fırlatmadan degraded sonuç döner)
            if (options.ResultGenerator != null)
            {
                return await InjectResultAsync<TResult>(options, context, options.ResultGenerator(context), nameof(ChaosOptions.ResultGenerator)).ConfigureAwait(context.ContinueOnCapturedContext);
            }

            // 5. Hata enjeksiyonu: üretici null dönerse bu çağrıda hata enjekte edilmez (Polly 8.8 ile aynı)
            if (options.FaultGenerator?.Invoke() is { } exception)
            {
                return await InjectFaultAsync<TResult>(options, context, exception).ConfigureAwait(context.ContinueOnCapturedContext);
            }
        }

        return await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
    }

    private async ValueTask<Outcome<TResult>> InjectResultAsync<TResult>(ChaosOptions options, AegisContext context, object? degradedResult, string generatorName)
    {
        await NotifyAsync(options, context, AegisEventNames.ChaosOnOutcome, ChaosInjectionKind.Outcome, result: degradedResult)
            .ConfigureAwait(context.ContinueOnCapturedContext);

        if (degradedResult is TResult typedResult)
        {
            return Outcome<TResult>.FromResult(typedResult);
        }

        if (degradedResult is null && default(TResult) is null)
        {
            return Outcome<TResult>.FromResult(default!);
        }

        return Outcome<TResult>.FromException(new InvalidOperationException(
            $"Kaos '{generatorName}' üreticisi beklenen '{typeof(TResult)}' tipinde bir sonuç döndürmedi " +
            $"(dönen tip: '{degradedResult?.GetType().ToString() ?? "null"}')."));
    }

    private async ValueTask<Outcome<TResult>> InjectFaultAsync<TResult>(ChaosOptions options, AegisContext context, Exception exception)
    {
        await NotifyAsync(options, context, AegisEventNames.ChaosOnFault, ChaosInjectionKind.Fault, exception: exception)
            .ConfigureAwait(context.ContinueOnCapturedContext);
        return Outcome<TResult>.FromException(exception); // fırlatılmaz; en dışta bir kez fırlatılır
    }

    private static double NextRandom(ChaosOptions options) => options.Randomizer?.Invoke() ?? Random.Shared.NextDouble();

    /// <summary>Etkinlik ve oran: çağrı bazlı üreticiler (Polly: EnabledGenerator / InjectionRateGenerator) varsa onlar kullanılır.</summary>
    private static async ValueTask<bool> ShouldInjectAsync(ChaosOptions options, AegisContext context)
    {
        var enabled = options.EnabledGenerator is { } enabledGenerator
            ? await enabledGenerator(context).ConfigureAwait(context.ContinueOnCapturedContext)
            : options.Enabled;
        if (!enabled)
        {
            return false;
        }

        var rate = options.InjectionRateGenerator is { } rateGenerator
            ? Math.Min(1.0, Math.Max(0.0, await rateGenerator(context).ConfigureAwait(context.ContinueOnCapturedContext)))
            : options.InjectionRate;
        return NextRandom(options) < rate;
    }

    private async ValueTask NotifyAsync(
        ChaosOptions options,
        AegisContext context,
        string eventName,
        ChaosInjectionKind kind,
        TimeSpan? latency = null,
        Exception? exception = null,
        object? result = null)
    {
        if (!Telemetry.IsEnabled && options.OnInjected is null)
        {
            return;
        }

        var arguments = new ChaosInjectionArguments { Context = context, Kind = kind, Latency = latency, Exception = exception, Result = result };
        Telemetry.Report(eventName, AegisEventSeverity.Information, context, exception, result, arguments);
        await AegisCallbacks.InvokeSafelyAsync(options.OnInjected, arguments, nameof(options.OnInjected)).ConfigureAwait(context.ContinueOnCapturedContext);
    }
}
