using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Telemetry;
using Aegis.Resilience.Core.Strategies.Timeout;

namespace Aegis.Resilience.Core.Strategies.Hedging;

/// <summary>
/// Kuyruk gecikmesini (Tail latency) düşürmek için yanıt vermeyen isteklere paralel spekülatif alternatif istek fırlatan Hedging stratejisi.
/// Her deneme kendi iptal jetonuna sahip izole bir alt bağlamda (child context) çalışır; ancak çağıranın
/// Properties ve CorrelationId değerleri alt bağlamlara kopyalanır ve kazanan denemenin yazdıkları çağırana geri birleştirilir (AEGIS-110).
/// </summary>
public sealed class HedgingStrategy : AegisStrategy
{
    private readonly HedgingOptions _staticOptions;

    /// <inheritdoc />
    public override object? Options => _staticOptions;

    private HedgingOptions? _lastGoodOptions;

    /// <summary>Canlı seçenekleri doğrulayarak çözer; geçersizse son geçerli seçeneklerle devam eder (AEGIS-145).</summary>
    private HedgingOptions ResolveOptions() => DynamicOptionsResolver.Resolve(_staticOptions, _staticOptions.OptionsProvider, static o => o.Validate(), ref _lastGoodOptions);

    public override string Name => "Hedging";

    public HedgingStrategy(HedgingOptions options)
    {
        _staticOptions = options ?? throw new ArgumentNullException(nameof(options));
        _staticOptions.Validate(); // fail-fast: geçersiz yapılandırma kurulum anında bildirilir (AEGIS-130)
    }

    protected override async ValueTask<Outcome<TResult>> ExecuteCoreAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        if (context.CancellationToken.IsCancellationRequested)
        {
            return Outcome<TResult>.FromException(new OperationCanceledException(context.CancellationToken));
        }

        var options = ResolveOptions();
        var maxAttempts = ResolveMaxAttempts(context, options);
        var hedgingDelay = ResolveHedgingDelay(options);

        var primary = StartPrimary(callback, state, context, options);
        if (TryFinishSynchronously(primary, context, options, maxAttempts, out var immediate, out var primaryTask))
        {
            return immediate;
        }

        var attempts = new List<HedgedAttempt<TResult>>(maxAttempts) { primary.ToAttempt(primaryTask) };
        try
        {
            // Kazanan ya da (bütçe yeni denemeye izin vermediyse) son başarısız deneme.
            var settled = await StartAdditionalAttemptsAsync(callback, state, context, attempts, maxAttempts, hedgingDelay, TimeProvider, options)
                .ConfigureAwait(context.ContinueOnCapturedContext);
            if (settled != null)
            {
                return CompleteWithWinner(settled, attempts, context);
            }

            return await AwaitRemainingAttemptsAsync(attempts, context, options).ConfigureAwait(context.ContinueOnCapturedContext);
        }
        finally
        {
            // Kalan (kaybeden veya terk edilen) denemeleri iptal et; bittiklerinde kaynaklarını serbest bırak
            foreach (var attempt in attempts)
            {
                attempt.Abandon();
            }
        }
    }

    // Toplam deneme = birincil + ek denemeler (Polly ile aynı). Yeniden gönderilmesi güvenli olmayan işlemde ek deneme yapılmaz;
    // çağrı bazında üst sınır (ör. yönlendirme grubu sayısı) bağlamdan gelebilir.
    private static int ResolveMaxAttempts(AegisContext context, HedgingOptions options)
    {
        var maxAttempts = AegisContextKeys.AreAdditionalAttemptsSuppressed(context) ? 1 : 1 + Math.Max(0, options.MaxHedgedAttempts);
        if (context.TryGetProperty(HedgingOptions.MaxAttemptsKey, out var callCap) && callCap > 0)
        {
            maxAttempts = Math.Min(maxAttempts, callCap);
        }

        return maxAttempts;
    }

    // AEGIS-136: System.Threading.Timeout.InfiniteTimeSpan (-1ms) "ardışık yedekleme" modudur: yeni deneme YALNIZCA öncekiler
    // başarısız olunca başlar, asla paralel çalışılmaz (Polly: InfiniteHedgingDelay_EnsureNoConcurrentExecutions).
    private static TimeSpan ResolveHedgingDelay(HedgingOptions options) =>
        options.HedgingDelay == System.Threading.Timeout.InfiniteTimeSpan
            ? System.Threading.Timeout.InfiniteTimeSpan
            : options.HedgingDelay > TimeSpan.Zero ? AegisTimers.Normalize(options.HedgingDelay) : TimeSpan.Zero;

    /// <summary>Başlatılmış birincil deneme: bekleyen sonuç, iptal kaynağı, çağıran iptaline kayıt ve havuzdan alt bağlam.</summary>
    private readonly struct PrimaryAttempt<TResult>(
        ValueTask<Outcome<TResult>> pending, CancellationTokenSource cts, CancellationTokenRegistration registration, AegisContext context)
    {
        public ValueTask<Outcome<TResult>> Pending { get; } = pending;
        public CancellationTokenSource Cts { get; } = cts;
        public CancellationTokenRegistration Registration { get; } = registration;
        public AegisContext Context { get; } = context;

        /// <summary>
        /// Birincil yarışa katılır. Buradan sonra çağrıdan uzun yaşayabilir (kaybeden/terk edilen deneme): kimlik sabitlenir,
        /// bağlam havuza iade edilmez — eski CreateChild davranışıyla birebir aynı.
        /// </summary>
        public HedgedAttempt<TResult> ToAttempt(Task<Outcome<TResult>> task)
        {
            Context.DetachFromParent();
            return new HedgedAttempt<TResult> { Task = task, Cts = Cts, ParentRegistration = Registration, Context = Context };
        }
    }

    // Birincil deneme havuzdan kiralanan alt bağlamda başlatılır; başlatma başarısız olursa kaynaklar hemen geri verilir.
    private static PrimaryAttempt<TResult> StartPrimary<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback, TState state, AegisContext context, HedgingOptions options)
    {
        var cts = CancellationTokenSourcePool.Rent();
        var registration = HedgedAttempt<TResult>.RegisterParent(context.CancellationToken, cts);
        AegisContext primaryContext;
        try
        {
            primaryContext = AegisContextPool.RentChild(context, cts.Token);
        }
        catch
        {
            registration.Dispose();
            CancellationTokenSourcePool.Return(cts);
            throw;
        }

        try
        {
            return new PrimaryAttempt<TResult>(RunPrimary(callback, state, primaryContext, options), cts, registration, primaryContext);
        }
        catch
        {
            registration.Dispose();
            CancellationTokenSourcePool.Return(cts);
            primaryContext.DetachFromParent(); // geri çağrı bağlamı saklamış olabilir: havuza verilmez
            throw;
        }
    }

    /// <summary>
    /// Birincil eşzamanlı bitti ve kabul edildiyse (ya da tek deneme hakkı varsa) çağrı burada tamamlanır: Task/liste/zamanlayıcı
    /// hiç oluşturulmaz, kaynaklar ve alt bağlam hemen iade edilir. Aksi halde yarışa katılacak görevi verir. ValueTask yalnızca
    /// BİR kez tüketilir (CA2012): okunduysa Task.FromResult ile sarılır, okunmadıysa AsTask.
    /// </summary>
    private static bool TryFinishSynchronously<TResult>(
        in PrimaryAttempt<TResult> primary, AegisContext context, HedgingOptions options, int maxAttempts,
        out Outcome<TResult> outcome, out Task<Outcome<TResult>> primaryTask)
    {
        outcome = default;
        if (!primary.Pending.IsCompletedSuccessfully)
        {
            primaryTask = primary.Pending.AsTask();
            return false;
        }

        var immediate = primary.Pending.Result;
        if (!HedgingOutcome.IsAccepted(immediate, options) && maxAttempts > 1)
        {
            primaryTask = Task.FromResult(immediate); // eşzamanlı başarısız: hemen yedek deneme başlar
            return false;
        }

        if (immediate.IsSuccess)
        {
            context.MergePropertiesFrom(primary.Context);
        }

        primary.Registration.Dispose();
        CancellationTokenSourcePool.Return(primary.Cts);
        AegisContextPool.Return(primary.Context); // deneme bitti; bağlam havuza döner (boru hattı bağlamıyla aynı sözleşme)
        outcome = HedgingOutcome.Unwrap(immediate);
        primaryTask = null!;
        return true;
    }

    /// <summary>
    /// Bir yedek denemeyi başlatır: <see cref="HedgingOptions.ActionGenerator"/> işlem ürettiyse o, yoksa asıl geri çağrı;
    /// sonuç koşulu tanımlıysa sınıflandırılır.
    /// </summary>
    private static ValueTask<Outcome<TResult>> RunAttempt<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        TState state,
        AegisContext context,
        int attemptNumber,
        HedgingOptions options)
    {
        Func<AegisContext, ValueTask<object?>>? action;
        try
        {
            action = options.ActionGenerator?.Invoke(new HedgingAttemptArguments { Context = context, AttemptNumber = attemptNumber });
        }
        catch (Exception ex)
        {
            return new ValueTask<Outcome<TResult>>(Outcome<TResult>.FromException(ex));
        }

        var pending = action is not null ? RunGeneratedAsync<TResult>(action, context) : callback(context, state);
        return HedgingOutcome.Track(pending, context, attemptNumber, options);
    }

    /// <summary>Birincil deneme: sonuç koşulu yoksa geri çağrı doğrudan (sıfır tahsisli yol korunur).</summary>
    private static ValueTask<Outcome<TResult>> RunPrimary<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        TState state,
        AegisContext context,
        HedgingOptions options) =>
        HedgingOutcome.Track(callback(context, state), context, attemptNumber: 0, options);

    /// <summary><see cref="HedgingOptions.ActionGenerator"/>'ın ürettiği işlemi çalıştırır ve sonucu boru hattı tipine çevirir.</summary>
    private static async ValueTask<Outcome<TResult>> RunGeneratedAsync<TResult>(Func<AegisContext, ValueTask<object?>> action, AegisContext context)
    {
        try
        {
            var result = await action(context).ConfigureAwait(context.ContinueOnCapturedContext);
            if (result is TResult typed)
            {
                return Outcome<TResult>.FromResult(typed);
            }

            if (result is null && default(TResult) is null)
            {
                return Outcome<TResult>.FromResult(default!);
            }

            return Outcome<TResult>.FromException(new InvalidOperationException(
                $"Hedging ActionGenerator '{result?.GetType().Name ?? "null"}' tipinde sonuç döndürdü ancak boru hattı '{typeof(TResult).Name}' bekliyor."));
        }
        catch (Exception ex)
        {
            return Outcome<TResult>.FromException(ex);
        }
    }

    /// <summary>Yeni yedek deneme öncesi: telemetri olayı + kullanıcı bildirimi (hatası yutulur).</summary>
    private async ValueTask NotifyHedgingAsync(HedgingOptions options, AegisContext context, int attemptNumber)
    {
        if (options.OnHedging is null)
        {
            Telemetry.Report(AegisEventNames.OnHedging, AegisEventSeverity.Warning, context);
            return;
        }

        var arguments = new HedgingAttemptArguments { Context = context, AttemptNumber = attemptNumber };
        Telemetry.Report(AegisEventNames.OnHedging, AegisEventSeverity.Warning, context, arguments: arguments);
        await AegisCallbacks.InvokeSafelyAsync(options.OnHedging, arguments, nameof(options.OnHedging)).ConfigureAwait(context.ContinueOnCapturedContext);
    }

    /// <summary>Üretilen gecikmeyi seçeneklerdeki kuralla normalleştirir (sonsuz korunur, negatif sıfır sayılır).</summary>
    private static TimeSpan NormalizeDelay(TimeSpan delay) =>
        delay == System.Threading.Timeout.InfiniteTimeSpan ? delay : delay > TimeSpan.Zero ? AegisTimers.Normalize(delay) : TimeSpan.Zero;

    private async ValueTask<Task<Outcome<TResult>>?> StartAdditionalAttemptsAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        TState state,
        AegisContext context,
        List<HedgedAttempt<TResult>> attempts,
        int maxAttempts,
        TimeSpan hedgingDelay,
        TimeProvider timeProvider,
        HedgingOptions options)
    {
        var spawnedAttempts = 1;
        while (spawnedAttempts < maxAttempts && attempts.Count > 0)
        {
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            var roundDelay = options.DelayGenerator is { } generator
                ? NormalizeDelay(await generator(new HedgingAttemptArguments { Context = context, AttemptNumber = spawnedAttempts }).ConfigureAwait(context.ContinueOnCapturedContext))
                : hedgingDelay;
            var delayTask = Task.Delay(roundDelay, timeProvider, delayCts.Token);

            var waitList = new List<Task>(attempts.Count + 1);
            foreach (var attempt in attempts)
            {
                waitList.Add(attempt.Task);
            }
            waitList.Add(delayTask);

            var completed = await Task.WhenAny(waitList).ConfigureAwait(context.ContinueOnCapturedContext);

            // Kullanılmayan gecikme zamanlayıcısını bu tur bitmeden durdur.
            if (completed != delayTask)
            {
                delayCts.Cancel();
            }

            if (completed == delayTask)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (!BudgetAllowsHedge(options, context))
                {
                    break;
                }

                await NotifyHedgingAsync(options, context, spawnedAttempts).ConfigureAwait(context.ContinueOnCapturedContext);
                attempts.Add(StartAttempt(callback, state, context, spawnedAttempts, options));
                spawnedAttempts++;
                continue;
            }

            var finished = (Task<Outcome<TResult>>)completed;
            if (HedgingOutcome.Succeeded(finished, options))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                return finished;
            }

            RemoveFailedAttempt(finished, attempts);

            if (attempts.Count == 0 && spawnedAttempts < maxAttempts)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (!BudgetAllowsHedge(options, context))
                {
                    return finished; // yeni deneme yok: son denemenin özgün sonucu döner
                }

                await NotifyHedgingAsync(options, context, spawnedAttempts).ConfigureAwait(context.ContinueOnCapturedContext);
                attempts.Add(StartAttempt(callback, state, context, spawnedAttempts, options));
                spawnedAttempts++;
            }
        }

        return null;
    }

    // gRPC A6: ilk istek her zaman gider; ek hedging denemesi ancak bütçe yarının üstündeyse başlar.
    private bool BudgetAllowsHedge(HedgingOptions options, AegisContext context)
    {
        if (options.Budget is not { CanRetry: false })
        {
            return true;
        }

        Telemetry.Report(AegisEventNames.OnRetryBudgetExhausted, AegisEventSeverity.Warning, context);
        return false;
    }

    private static async ValueTask<Outcome<TResult>> AwaitRemainingAttemptsAsync<TResult>(
        List<HedgedAttempt<TResult>> attempts,
        AegisContext context,
        HedgingOptions options)
    {
        while (attempts.Count > 0)
        {
            var waitList = new List<Task<Outcome<TResult>>>(attempts.Count);
            foreach (var attempt in attempts)
            {
                waitList.Add(attempt.Task);
            }

            var winner = await Task.WhenAny(waitList)
                .WaitAsync(context.CancellationToken).ConfigureAwait(context.ContinueOnCapturedContext);
            if (HedgingOutcome.Succeeded(winner, options))
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                return CompleteWithWinner(winner, attempts, context);
            }

            if (attempts.Count == 1)
            {
                // Son denemenin özgün istisnasını koru.
                var lastAttempt = attempts[0];
                attempts.Clear();
                lastAttempt.Release();
                return HedgingOutcome.OutcomeOf(winner);
            }

            RemoveFailedAttempt(winner, attempts);
        }

        context.CancellationToken.ThrowIfCancellationRequested();
        return Outcome<TResult>.FromException(new InvalidOperationException("Yedek denemelerin (hedging) tamamı başarısız oldu."));
    }

    private static HedgedAttempt<TResult> StartAttempt<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        TState state,
        AegisContext parentContext,
        int attemptNumber,
        HedgingOptions options)
    {
        var cts = CancellationTokenSourcePool.Rent();
        var registration = HedgedAttempt<TResult>.RegisterParent(parentContext.CancellationToken, cts);
        try
        {
            var childContext = parentContext.CreateChild(cts.Token);
            childContext.SetProperty(HedgingOptions.AttemptNumberKey, attemptNumber);

            return new HedgedAttempt<TResult>
            {
                Task = RunAttempt(callback, state, childContext, attemptNumber, options).AsTask(),
                Cts = cts,
                ParentRegistration = registration,
                Context = childContext
            };
        }
        catch
        {
            registration.Dispose();
            CancellationTokenSourcePool.Return(cts);
            throw;
        }
    }

    /// <summary>
    /// Kazanan denemenin sonucunu döner; diğer denemeleri iptal eder ve kazananın bağlam verilerini çağırana taşır.
    /// </summary>
    private static Outcome<TResult> CompleteWithWinner<TResult>(
        Task<Outcome<TResult>> winnerTask,
        List<HedgedAttempt<TResult>> attempts,
        AegisContext parentContext)
    {
        HedgedAttempt<TResult>? winner = null;

        for (var i = attempts.Count - 1; i >= 0; i--)
        {
            var attempt = attempts[i];
            if (attempt.Task == winnerTask)
            {
                winner = attempt;
                attempts.RemoveAt(i);
                continue;
            }

            attempt.Abandon();
            attempts.RemoveAt(i);
        }

        if (winner != null)
        {
            parentContext.MergePropertiesFrom(winner.Context);
            winner.Release();
        }

        return HedgingOutcome.OutcomeOf(winnerTask);
    }

    private static void RemoveFailedAttempt<TResult>(Task<Outcome<TResult>> failedTask, List<HedgedAttempt<TResult>> attempts)
    {
        for (var i = 0; i < attempts.Count; i++)
        {
            if (attempts[i].Task != failedTask)
            {
                continue;
            }

            attempts[i].ReleaseWhenCompleted();
            attempts.RemoveAt(i);
            return;
        }
    }
}
