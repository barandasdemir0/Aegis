using System.Runtime.CompilerServices;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Strategies.Retry;

/// <summary>
/// Geçici hatalarda üstel gecikme (Exponential Backoff) ve Jitter desteği sunan yeniden deneme stratejisi.
/// </summary>
public sealed class RetryStrategy : AegisStrategy
{
    private readonly RetryOptions _staticOptions;

    /// <inheritdoc />
    public override object? Options => _staticOptions;

    private RetryOptions? _lastGoodOptions;

    /// <summary>Canlı seçenekleri doğrulayarak çözer; geçersizse son geçerli seçeneklerle devam eder (AEGIS-145).</summary>
    private RetryOptions ResolveOptions() => DynamicOptionsResolver.Resolve(_staticOptions, _staticOptions.OptionsProvider, static o => o.Validate(), ref _lastGoodOptions);

    public override string Name => "Retry";

    public RetryStrategy(RetryOptions options)
    {
        _staticOptions = options ?? throw new ArgumentNullException(nameof(options));
        _staticOptions.Validate(); // fail-fast: geçersiz yapılandırma kurulum anında bildirilir (AEGIS-130)
    }

#if !AEGIS_LEGACY // .NET 8+: iç katman yalnızca Aegis tarafından tek sefer beklenir; async durum makinesi kutusu havuzlanır
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    protected override async ValueTask<Outcome<TResult>> ExecuteCoreAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        var options = ResolveOptions();
        // Yeniden gönderilmesi güvenli olmayan işlemlerde (ör. Idempotency-Key'siz POST) tek deneme yapılır; ilk hata olduğu gibi yükselir.
        var maxRetryAttempts = AegisContextKeys.AreAdditionalAttemptsSuppressed(context) ? 0 : options.MaxRetryAttempts;
        var attempt = 0;
        TimeSpan? previousDelay = null;

        while (true)
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                return Outcome<TResult>.FromException(new OperationCanceledException(context.CancellationToken));
            }

            // Deneme süresi yalnızca telemetri dinleniyorsa ölçülür (Polly: ExecutionAttempt olayı).
            var measureAttempt = Telemetry.IsAttemptEnabled;
            var attemptStart = measureAttempt ? GetTimestamp() : 0;

            var outcome = await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);

            bool handled;
            if (options.ShouldHandleOutcome is { } predicate)
            {
                // Bağlam + deneme numarası gören (async) koşul: istisna ve sonuç tek noktada değerlendirilir. Polly gibi koşul
                // son denemede de sorulur; iptal ve açık devre reddi asla yeniden denenmez.
                handled = (outcome.Exception is not { } controlFlow || !CircuitBreakerRules.IsControlFlow(controlFlow)) &&
                          await predicate.ShouldHandleAsync(new OutcomeArguments<TResult>(outcome, context, attempt)).ConfigureAwait(context.ContinueOnCapturedContext);
            }
            else if (outcome.Exception is { } exception)
            {
                // Koşul sırası eskisiyle aynı: önce kullanıcı koşulu, sonra hak kontrolü
                handled = ShouldHandleException(exception, options);
            }
            else
            {
                // Zero-Exception Result-Based Handling
                handled = options.ShouldHandleResult != null && options.ShouldHandleResult(outcome.Result);
            }

            if (measureAttempt)
            {
                Telemetry.ReportAttempt(context, attempt, GetElapsedTime(attemptStart), handled, outcome.Exception,
                    outcome.Exception is null ? outcome.Result : null);
            }

            if (options.Budget is { } budget)
            {
                RecordInBudget(budget, outcome, handled);
            }

            if (!handled || attempt >= maxRetryAttempts)
            {
                return outcome; // istisna fırlatılmaz; en dışta bir kez fırlatılır
            }

            if (options.Budget is { CanRetry: false })
            {
                // gRPC retry throttling: bağımlılık bozukken yeniden deneme trafiği katlamasın; ilk hata beklemeden döner.
                Telemetry.Report(AegisEventNames.OnRetryBudgetExhausted, AegisEventSeverity.Warning, context, outcome.Exception,
                    outcome.Exception is null ? outcome.Result : null);
                return outcome;
            }

            attempt++;
            previousDelay = await RetryAfterAsync(options, new RetryAttemptContext
            {
                AttemptNumber = attempt - 1, // 0'dan başlar (Polly ile aynı): ilk yeniden deneme 0
                Exception = outcome.Exception,
                Result = outcome.Exception is null ? outcome.Result : null,
                Context = context
            }, previousDelay, outcome.Exception is null, TimeProvider, Telemetry).ConfigureAwait(context.ContinueOnCapturedContext);
        }
    }

    private static async ValueTask<TimeSpan> RetryAfterAsync(
        RetryOptions options,
        RetryAttemptContext attemptContext,
        TimeSpan? previousDelay,
        bool discardResult,
        TimeProvider timeProvider,
        AegisStrategyTelemetry telemetry)
    {
        var context = attemptContext.Context;
        AegisTelemetry.RetriesTotal.Add(1, new KeyValuePair<string, object?>("pipeline", context.PipelineName ?? "default"));

        var delay = await ResolveDelayAsync(options, attemptContext, previousDelay).ConfigureAwait(context.ContinueOnCapturedContext);

        if (telemetry.IsEnabled)
        {
            telemetry.Report(AegisEventNames.OnRetry, AegisEventSeverity.Warning, context, attemptContext.Exception, attemptContext.Result,
                new RetryAttemptContext
                {
                    AttemptNumber = attemptContext.AttemptNumber,
                    RetryDelay = delay,
                    Exception = attemptContext.Exception,
                    Result = attemptContext.Result,
                    Context = context
                });
        }

        if (options.OnRetry != null)
        {
            // Bildirim hatası yeniden denemeyi bozmaz (diğer olaylarla aynı kural): yutulur ve aegis.callback.errors.total'a yazılır.
            await AegisCallbacks.InvokeSafelyAsync(options.OnRetry, new RetryAttemptContext
            {
                AttemptNumber = attemptContext.AttemptNumber,
                RetryDelay = delay,
                Exception = attemptContext.Exception,
                Result = attemptContext.Result,
                Context = context
            }, nameof(options.OnRetry)).ConfigureAwait(context.ContinueOnCapturedContext);
        }

        // OnRetry sonucu inceleyebilir; sonucu ancak bildirim tamamlandıktan sonra serbest bırak.
        if (discardResult)
        {
            await DiscardedResultDisposer.DisposeAsync(attemptContext.Result).ConfigureAwait(context.ContinueOnCapturedContext);
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(AegisTimers.Normalize(delay), timeProvider, context.CancellationToken).ConfigureAwait(context.ContinueOnCapturedContext);
        }

        return delay;
    }

    // gRPC A6: yeniden denenebilir (ele alınan) hata jeton düşürür, başarı jeton ekler; ele alınmayan hata ve iptal bütçeyi
    // etkilemez (hedefin sağlığı hakkında bilgi taşımaz).
    private static void RecordInBudget<TResult>(RetryBudget budget, in Outcome<TResult> outcome, bool handled)
    {
        if (handled)
        {
            budget.RecordFailure();
        }
        else if (outcome.Exception is null)
        {
            budget.RecordSuccess();
        }
    }

    private static bool ShouldHandleException(Exception ex, RetryOptions options)
    {
        // Devre kesici zaten "isteği iletme" kararı vermişse (BrokenCircuitException),
        // Retry stratejisi bunu tekrar denememelidir: gerçek bir ağ çağrısı yapılmadığı için
        // fayda sağlamaz, yalnızca jitter gecikmeleri nedeniyle devre AÇIKKEN bile isteği
        // gereksiz yere saniyelerce bekletir (AEGIS-122). CircuitBreakerStrategy'nin kendi
        // ShouldHandleException'ı da aynı kuralı uygular; burada kullanıcı ShouldHandle'ı
        // ne olursa olsun bu davranış korunur (OperationCanceledException ile tutarlı).
        if (CircuitBreakerRules.IsControlFlow(ex))
        {
            return false;
        }

        return options.ShouldHandle?.Invoke(ex) ?? true;
    }

    public static TimeSpan CalculateDelay(int attempt, RetryOptions options, TimeSpan? previousDelay = null)
    {
        var maxMs = options.MaxDelay.TotalMilliseconds;

        if (options.BackoffType == DelayBackoffType.DecorrelatedJitter)
        {
            var baseMs = options.Delay.TotalMilliseconds;
            var prevMs = previousDelay?.TotalMilliseconds ?? baseMs;
            
            // AWS Decorrelated Jitter: min(cap, random_between(base, prev * 3))
            var low = Math.Min(baseMs, prevMs * 3);
            var high = Math.Max(baseMs, prevMs * 3);
            var randomMs = low + (NextRandom(options) * (high - low));
            var delayMs = Math.Min(maxMs, randomMs);
            return TimeSpan.FromMilliseconds(delayMs);
        }

        // Sayısal taşma (OverflowException) koruması: Math.Min(30, attempt - 1) ile üs sınırlandırılır
        var boundedAttempt = Math.Min(30, Math.Max(1, attempt));
        var baseDelayMs = options.BackoffType switch
        {
            DelayBackoffType.Constant => options.Delay.TotalMilliseconds,
            DelayBackoffType.Linear => options.Delay.TotalMilliseconds * boundedAttempt,
            DelayBackoffType.Exponential => options.Delay.TotalMilliseconds * Math.Pow(2, boundedAttempt - 1),
            _ => options.Delay.TotalMilliseconds
        };

        if (baseDelayMs > maxMs || double.IsInfinity(baseDelayMs) || double.IsNaN(baseDelayMs))
        {
            baseDelayMs = maxMs;
        }

        if (options.UseJitter && baseDelayMs > 0)
        {
            // Full Jitter formülü: 0.5 ile 1.0 arasında rastgele bir katsayı
            var jitterMultiplier = NextRandom(options);
            baseDelayMs = baseDelayMs * (0.5 + 0.5 * jitterMultiplier);
        }

        return TimeSpan.FromMilliseconds(Math.Min(maxMs, Math.Max(0, baseDelayMs)));
    }

    private static double NextRandom(RetryOptions options) => options.Randomizer?.Invoke() ?? Random.Shared.NextDouble();

    private static async ValueTask<TimeSpan> ResolveDelayAsync(
        RetryOptions options,
        RetryAttemptContext attemptContext,
        TimeSpan? previousDelay)
    {
        if (options.DelayGenerator != null)
        {
            var generated = await options.DelayGenerator(attemptContext).ConfigureAwait(attemptContext.Context.ContinueOnCapturedContext);
            if (generated.HasValue)
            {
                return generated.Value;
            }
        }

        // HTTP Retry-After bildirimi (ShouldRetryAfterHeader açıksa) gecikme olur; bildirim her durumda tüketilir ki sonraki denemeye sızmasın.
        if (attemptContext.Context.TryGetProperty<object>(AegisContextKeys.RetryAfterDelay, out var rawDelay) &&
            attemptContext.Context.Properties.Remove(AegisContextKeys.RetryAfterDelay) &&
            options.ShouldRetryAfterHeader && rawDelay is TimeSpan retryAfter && retryAfter > TimeSpan.Zero)
        {
            return retryAfter <= options.MaxDelay ? retryAfter : options.MaxDelay;
        }

        return CalculateDelay(attemptContext.AttemptNumber + 1, options, previousDelay); // hesap 1 tabanlı yeniden deneme sırasıyla
    }
}
