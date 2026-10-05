using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Strategies.Retry;

namespace Aegis.Resilience.Core.Strategies.Hedging;

/// <summary>
/// Hedging deneme sonuçlarının sınıflandırılması (kazanır mı) ve retry bütçesine kaydı. Yarış mantığı
/// <see cref="HedgingStrategy"/>'dedir; burası yalnızca "bu sonuç ne demek" sorusunu yanıtlar.
/// </summary>
internal static class HedgingOutcome
{
    /// <summary>
    /// Koşulun "başarısız" saydığı sonucu hedging içinde başarısızlık olarak taşır (fırlatılmaz). Böylece kazanan seçimi
    /// değişmeden çalışır; en sonda <see cref="Unwrap{TResult}"/> özgün sonucu geri verir.
    /// </summary>
    internal sealed class HandledResult<TResult>(Outcome<TResult> original) : Exception("Yedek deneme (hedging): sonuç koşul tarafından başarısız sayıldı.")
    {
        public Outcome<TResult> Original { get; } = original;
    }

    /// <summary>
    /// Deneme sonucu kabul edilir mi (kazanır): başarı ya da <see cref="HedgingOptions.ShouldHandle"/>'ın ele almadığı istisna
    /// (Polly: ele alınmayan sonuç yedeklemeyi bitirir). Koşulun başarısız saydığı sonuç (<see cref="HandledResult{TResult}"/>) kabul edilmez.
    /// </summary>
    public static bool IsAccepted<TResult>(in Outcome<TResult> outcome, HedgingOptions options) =>
        outcome.Exception is not { } exception ||
        (options.ShouldHandle is { } shouldHandle && exception is not HandledResult<TResult> && !shouldHandle(exception));

    public static bool Succeeded<TResult>(Task<Outcome<TResult>> task, HedgingOptions options) =>
        task.IsCompletedSuccessfully && IsAccepted(task.Result, options);

    /// <summary>Bitmiş denemenin çağırana dönecek sonucu (bozuk görev de sonuca çevrilir; koşulun sardığı sonuç açılır).</summary>
    public static Outcome<TResult> OutcomeOf<TResult>(Task<Outcome<TResult>> task) =>
        task.IsCompletedSuccessfully
            ? Unwrap(task.Result)
            : Outcome<TResult>.FromException(task.Exception?.InnerException ?? new OperationCanceledException());

    public static Outcome<TResult> Unwrap<TResult>(in Outcome<TResult> outcome) =>
        outcome.Exception is HandledResult<TResult> handled ? handled.Original : outcome;

    /// <summary>Koşul tanımlıysa sonucu sınıflandırır; bütçe tanımlıysa kaydeder. İkisi de yoksa bekleyen sonuç aynen döner (tahsissiz).</summary>
    public static ValueTask<Outcome<TResult>> Track<TResult>(
        ValueTask<Outcome<TResult>> pending, AegisContext context, int attemptNumber, HedgingOptions options) =>
        WithBudget(
            options.ShouldHandleOutcome is null && options.ShouldHandleResult is null ? pending : ClassifyAsync(pending, context, attemptNumber, options),
            context, options);

    /// <summary>Sonucu koşula göre sınıflandırır: ele alınan (kötü) sonuç sarılarak başarısız sayılır.</summary>
    private static async ValueTask<Outcome<TResult>> ClassifyAsync<TResult>(
        ValueTask<Outcome<TResult>> pending, AegisContext context, int attemptNumber, HedgingOptions options)
    {
        var outcome = await pending.ConfigureAwait(context.ContinueOnCapturedContext);
        if (!outcome.IsSuccess)
        {
            return outcome; // istisnalar zaten sonraki denemeye geçer
        }

        bool handled;
        try
        {
            handled = options.ShouldHandleOutcome is { } predicate
                ? await predicate.ShouldHandleAsync(new OutcomeArguments<TResult>(outcome, context, attemptNumber)).ConfigureAwait(context.ContinueOnCapturedContext)
                : options.ShouldHandleResult!(outcome.Result);
        }
        catch (Exception ex)
        {
            return Outcome<TResult>.FromException(ex); // koşul hatası da denemenin sonucudur
        }

        return handled ? Outcome<TResult>.FromException(new HandledResult<TResult>(outcome)) : outcome;
    }

    // gRPC A6: her deneme sonucu bütçeye yazılır (kabul edilen başarı jeton ekler, ele alınan hata düşürür). İptal edilen deneme
    // (kaybeden ya da çağıran iptali) sayılmaz.
    private static ValueTask<Outcome<TResult>> WithBudget<TResult>(ValueTask<Outcome<TResult>> pending, AegisContext context, HedgingOptions options)
    {
        if (options.Budget is not { } budget)
        {
            return pending;
        }

        if (pending.IsCompletedSuccessfully)
        {
            var outcome = pending.Result;
            RecordInBudget(budget, outcome, context, options);
            return new ValueTask<Outcome<TResult>>(outcome);
        }

        return RecordWhenCompletedAsync(pending, budget, context, options);
    }

    private static async ValueTask<Outcome<TResult>> RecordWhenCompletedAsync<TResult>(
        ValueTask<Outcome<TResult>> pending, RetryBudget budget, AegisContext context, HedgingOptions options)
    {
        var outcome = await pending.ConfigureAwait(context.ContinueOnCapturedContext);
        RecordInBudget(budget, outcome, context, options);
        return outcome;
    }

    private static void RecordInBudget<TResult>(RetryBudget budget, in Outcome<TResult> outcome, AegisContext context, HedgingOptions options)
    {
        if (IsAccepted(outcome, options))
        {
            if (outcome.IsSuccess)
            {
                budget.RecordSuccess();
            }
        }
        else if (!(outcome.Exception is OperationCanceledException && context.CancellationToken.IsCancellationRequested))
        {
            budget.RecordFailure();
        }
    }
}
