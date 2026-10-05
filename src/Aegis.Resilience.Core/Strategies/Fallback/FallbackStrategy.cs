using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Strategies.Fallback;

/// <summary>
/// İşlem başarısız olduğunda önceden tanımlanmış alternatif bir değer veya işlem döndüren Fallback stratejisi.
/// İstisna tabanlı (<see cref="FallbackOptions.ShouldHandle"/>) ve sonuç tabanlı (<see cref="FallbackOptions.ShouldHandleResult"/>,
/// <see cref="FallbackOptions.ShouldHandleOutcome"/>) yedeklemeyi destekler.
/// </summary>
public sealed class FallbackStrategy : AegisStrategy
{
    private readonly FallbackOptions _options;

    /// <inheritdoc />
    public override object? Options => _options;

    public override string Name => "Fallback";

    public FallbackStrategy(FallbackOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    protected override async ValueTask<Outcome<TResult>> ExecuteCoreAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        var outcome = await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);

        bool handled;
        if (outcome.Exception is OperationCanceledException)
        {
            handled = false; // iptal asla yedeğe düşmez
        }
        else if (_options.ShouldHandleOutcome is { } predicate)
        {
            handled = await predicate.ShouldHandleAsync(new OutcomeArguments<TResult>(outcome, context, 0)).ConfigureAwait(context.ContinueOnCapturedContext);
        }
        else if (outcome.Exception is { } exception)
        {
            handled = _options.ShouldHandle?.Invoke(exception) ?? true;
        }
        else
        {
            // Sonuç yalnızca koşul tanımlıysa değerlendirilir (başarı yolunda kutulama yok).
            handled = _options.ShouldHandleResult is { } resultPredicate && resultPredicate(outcome.Result);
        }

        if (!handled)
        {
            return outcome;
        }

        var ex = outcome.Exception;
        var arguments = new FallbackArguments
        {
            Context = context,
            Exception = ex,
            Result = ex is null ? outcome.Result : null
        };

        Telemetry.Report(AegisEventNames.OnFallback, AegisEventSeverity.Warning, context, ex, arguments.Result, arguments);
        await AegisCallbacks.InvokeSafelyAsync(_options.OnFallback, arguments, nameof(_options.OnFallback)).ConfigureAwait(context.ContinueOnCapturedContext);

        object? fallbackResult;
        if (_options.FallbackAction is { } action)
        {
            fallbackResult = await action(arguments).ConfigureAwait(context.ContinueOnCapturedContext);
        }
        else if (_options.FallbackHandler is { } handler && ex is not null)
        {
            fallbackResult = await handler(context, ex).ConfigureAwait(context.ContinueOnCapturedContext);
        }
        else
        {
            // AEGIS-129: Handler yoksa eskiden sessizce default(TResult) dönülüyordu — string için null,
            // int için 0. Çağıran bunu GERÇEK bir sonuç sanır (sessiz veri bozulması). Yapılandırma hatası
            // açıkça bildirilmeli. Sonuç tabanlı yedek için FallbackAction gerekir (FallbackHandler istisna ister).
            return Outcome<TResult>.FromException(new InvalidOperationException(
                ex is null
                    ? "Fallback stratejisi bir sonucu yedeğe düşürdü ancak FallbackOptions.FallbackAction tanımlı değil. " +
                      "Sonuç tabanlı yedek için FallbackAction atayın."
                    : "Fallback stratejisi istisnayı yakaladı ancak FallbackOptions.FallbackHandler tanımlı değil. " +
                      "Varsayılan bir değer sunmak için FallbackHandler atayın.",
                ex));
        }

        if (fallbackResult is TResult typed)
        {
            return Outcome<TResult>.FromResult(typed);
        }

        // null, yalnızca TResult null kabul eden bir tipse (referans tipi / Nullable<T>) geçerli bir yedek değerdir
        if (fallbackResult is null && default(TResult) is null)
        {
            return Outcome<TResult>.FromResult(default!);
        }

        // AEGIS-129: Yanlış tipte yedek değer (ör. int beklenirken string) eskiden sessizce default(TResult)
        // olarak dönüyordu: çağıran, çöken servisin sonucu olarak "0" görüyordu. Açık hata fırlatılır.
        return Outcome<TResult>.FromException(new InvalidOperationException(
            $"FallbackHandler '{fallbackResult?.GetType().Name ?? "null"}' tipinde bir değer döndürdü ancak boru hattı " +
            $"'{typeof(TResult).Name}' bekliyor. Yedek değerin tipi, çağrının dönüş tipiyle uyumlu olmalıdır.", ex));
    }
}
