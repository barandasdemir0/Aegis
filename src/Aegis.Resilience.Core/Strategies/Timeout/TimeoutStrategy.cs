using System.Runtime.CompilerServices;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Strategies.Timeout;

/// <summary>
/// İşlemin belirlenen süreyi aşması durumunda iptal edilmesini sağlayan Optimistic Timeout stratejisi.
/// </summary>
public sealed class TimeoutStrategy : AegisStrategy
{
    private readonly TimeoutOptions _staticOptions;

    /// <inheritdoc />
    public override object? Options => _staticOptions;

    private TimeoutOptions? _lastGoodOptions;

    /// <summary>Canlı seçenekleri doğrulayarak çözer; geçersizse son geçerli seçeneklerle devam eder (AEGIS-145).</summary>
    private TimeoutOptions ResolveOptions() => DynamicOptionsResolver.Resolve(_staticOptions, _staticOptions.OptionsProvider, static o => o.Validate(), ref _lastGoodOptions);

    public override string Name => "Timeout";

    public TimeoutStrategy(TimeoutOptions options)
    {
        _staticOptions = options ?? throw new ArgumentNullException(nameof(options));
        _staticOptions.Validate(); // fail-fast: geçersiz yapılandırma kurulum anında bildirilir (AEGIS-130)
    }

    protected override ValueTask<Outcome<TResult>> ExecuteCoreAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        var options = ResolveOptions();
        var timeout = AegisTimers.Normalize(options.TimeoutGenerator?.Invoke(context) ?? options.Timeout);

        if (timeout == System.Threading.Timeout.InfiniteTimeSpan)
        {
            return callback(context, state); // zaman aşımı bilinçli olarak devre dışı
        }

        if (timeout <= TimeSpan.Zero)
        {
            // AEGIS-131: Eskiden sıfır/negatif süre "zaman aşımı yok" sayılıyordu. Bu, TimeoutGenerator ile
            // kurulan İSTEK BÜTÇESİ (deadline budget) senaryosunda tam tersine çalışıyordu: kalan bütçe 0'a
            // düştüğünde istek SINIRSIZ çalışıyordu. Bütçe tükenmişse çağrı hiç başlatılmadan zaman aşımı verilir.
            return BudgetExhaustedAsync<TResult>(context, options, timeout);
        }

        return options.Mode == TimeoutStrategyMode.Pessimistic
            ? ExecutePessimisticAsync(callback, context, state, options, timeout, TimeProvider)
            : ExecuteOptimisticAsync(callback, context, state, options, timeout, TimeProvider);
    }

    private async ValueTask<Outcome<TResult>> BudgetExhaustedAsync<TResult>(AegisContext context, TimeoutOptions options, TimeSpan timeout)
    {
        return await TimedOutAsync<TResult>(context, options, timeout,
            $"Kalan zaman bütçesi tükendi ({timeout.TotalMilliseconds:F0}ms); işlem başlatılmadı.").ConfigureAwait(context.ContinueOnCapturedContext);
    }

#if !AEGIS_LEGACY // .NET 8+: iç katman yalnızca Aegis tarafından tek sefer beklenir; async durum makinesi kutusu havuzlanır
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<Outcome<TResult>> ExecuteOptimisticAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state,
        TimeoutOptions options,
        TimeSpan timeout,
        TimeProvider timeProvider)
    {
        // Havuzlanmış CTS + çağıran token'ına kayıt (bağlı CTS yerine): başarı yolunda tahsis yok. Eskiden her çağrı yeni
        // bağlı CTS + zamanlayıcı ayırıyordu (240 B; Polly 0 B — benchmark ile ölçüldü). İptal edilmiş CTS havuza dönmez.
        // Sahte/özel saat verilmişse zamanlayıcı o saate bağlanır (havuz yalnızca sistem saati için kullanılır).
        var pooled = ReferenceEquals(timeProvider, TimeProvider.System);
        CancellationTokenSource cts;
        if (pooled)
        {
            cts = CancellationTokenSourcePool.Rent();
            cts.CancelAfter(timeout);
        }
        else
        {
#if AEGIS_LEGACY
            cts = timeProvider.CreateCancellationTokenSource(timeout); // Microsoft.Bcl.TimeProvider
#else
            cts = new CancellationTokenSource(timeout, timeProvider);
#endif
        }

        var originalToken = context.CancellationToken;
        var callerRegistration = originalToken.CanBeCanceled
            ? originalToken.UnsafeRegister(static s => ((CancellationTokenSource)s!).Cancel(), cts)
            : default;
        context.CancellationToken = cts.Token;

        try
        {
            var outcome = await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);

            if (outcome.Exception is OperationCanceledException && !originalToken.IsCancellationRequested && cts.IsCancellationRequested)
            {
                AegisTelemetry.TimeoutsTotal.Add(1, new KeyValuePair<string, object?>("pipeline", context.PipelineName ?? "default"));

                return await TimedOutAsync<TResult>(context, options, timeout,
                    $"İşlem belirlenen zaman aşımı süresi olan {timeout.TotalSeconds} saniyeyi aştı.").ConfigureAwait(context.ContinueOnCapturedContext);
            }

            return outcome;
        }
        finally
        {
            context.CancellationToken = originalToken;
            callerRegistration.Dispose(); // çalışan bir Cancel geri çağrısı varsa bitmesini bekler
            if (pooled)
            {
                CancellationTokenSourcePool.Return(cts);
            }
            else
            {
                cts.Dispose();
            }
        }
    }

    private async ValueTask<Outcome<TResult>> ExecutePessimisticAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state,
        TimeoutOptions options,
        TimeSpan timeout,
        TimeProvider timeProvider)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        if (ReferenceEquals(timeProvider, TimeProvider.System))
        {
            cts.CancelAfter(timeout);
        }
        // Özel saatte iptal, aşağıdaki saat tabanlı gecikme kazandığında açıkça yapılır (cts.Cancel()).

        // Kötümser modda çağrı terk edilebildiği için ÇAĞIRANIN bağlamı asla paylaşılmaz.
        // Terk edilen görev, havuza iade edilmiş bir bağlamı mutasyona uğratamaz (AEGIS-111).
        var workerContext = context.CreateChild(cts.Token);

        // Task.Run'a token verilmez: iptal geri çağrının içinde Outcome olarak görülür (fırlatılan TaskCanceledException değil).
        var workerTask = Task.Run(async () => await callback(workerContext, state).ConfigureAwait(context.ContinueOnCapturedContext));

        var delayTask = Task.Delay(timeout, timeProvider, cts.Token);

        var completedTask = await Task.WhenAny(workerTask, delayTask).ConfigureAwait(context.ContinueOnCapturedContext);

        if (completedTask == workerTask)
        {
            cts.Cancel(); // bekleyen gecikme zamanlayıcısı süre sonuna kadar yaşamasın
            var outcome = await workerTask.ConfigureAwait(context.ContinueOnCapturedContext);
            if (outcome.IsSuccess)
            {
                context.MergePropertiesFrom(workerContext);
            }

            return outcome;
        }

        // The linked delay also completes when the caller cancels. Preserve caller
        // cancellation rather than reporting it as a timeout.
        if (context.CancellationToken.IsCancellationRequested)
        {
            return Outcome<TResult>.FromException(new OperationCanceledException(context.CancellationToken));
        }

        cts.Cancel();

        // Terk edilen workerTask'ın sonradan hata fırlatması durumunda UnobservedTaskException oluşmasını engelle
        _ = workerTask.ContinueWith(static t =>
        {
            _ = t.Exception;
        }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

        AegisTelemetry.TimeoutsTotal.Add(1, new KeyValuePair<string, object?>("pipeline", context.PipelineName ?? "default"));

        return await TimedOutAsync<TResult>(context, options, timeout,
            $"İşlem belirlenen kötümser (pessimistic) zaman aşımı süresi olan {timeout.TotalSeconds} saniyeyi aştı.").ConfigureAwait(context.ContinueOnCapturedContext);
    }

    /// <summary>Zaman aşımını bildirir (telemetri olayı + <c>OnTimeout</c>) ve <see cref="AegisTimeoutException"/> sonucunu üretir.</summary>
    private async ValueTask<Outcome<TResult>> TimedOutAsync<TResult>(AegisContext context, TimeoutOptions options, TimeSpan timeout, string message)
    {
        if (Telemetry.IsEnabled)
        {
            Telemetry.Report(AegisEventNames.OnTimeout, AegisEventSeverity.Error, context, arguments: timeout);
        }

        if (options.OnTimeout is { } onTimeout)
        {
            // Bildirim hatası zaman aşımı sonucunu ezmez (diğer olaylarla aynı kural): yutulur ve sayılır.
            await AegisCallbacks.InvokeSafelyAsync(static a => a.OnTimeout(a.Context, a.Timeout), (OnTimeout: onTimeout, Context: context, Timeout: timeout),
                nameof(options.OnTimeout)).ConfigureAwait(context.ContinueOnCapturedContext);
        }

        return Outcome<TResult>.FromException(new AegisTimeoutException(message, timeout) { TelemetrySource = Telemetry.Source });
    }
}
