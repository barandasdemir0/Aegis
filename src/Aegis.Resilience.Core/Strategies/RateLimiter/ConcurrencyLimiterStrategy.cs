using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Strategies.RateLimiter;
/// <summary>
/// Sistem kaynaklarının tükenmesini önlemek için aynı anda çalışan işlem sayısını sınırlayan (Bulkhead) stratejisi.
/// <para>
/// İzinler kilitsiz sayılır (<see cref="Interlocked"/>): yüksek eşzamanlılıkta her çağrının semafor kilidini alması
/// çekişme yaratıyordu. Semafor yalnızca kuyrukta bekleyen varken uyandırma sinyali olarak kullanılır. İzin hemen alınır ve
/// geri çağrı eşzamanlı tamamlanırsa async durum makinesi kurulmaz.
/// </para>
/// <para>
/// <see cref="ConcurrencyLimiterOptions.OptionsProvider"/> ile canlı ayar güncellemesi desteklenir: yeni limit yeni
/// girişlere hemen uygulanır; hâlihazırda çalışan işlemler zorla sonlandırılmaz, küçültülmüş limit onlar bittikçe
/// dolar (AEGIS-112).
/// </para>
/// </summary>
public sealed class ConcurrencyLimiterStrategy : AegisStrategy, IDisposable
{
    private readonly ConcurrencyLimiterOptions _staticOptions;

    /// <inheritdoc />
    public override object? Options => _staticOptions;

    // Yalnızca bekleyenleri uyandırır (sayım _inFlight'tadır). Hiç dispose edilmez (bkz. Dispose, AEGIS-128).
    private readonly SemaphoreSlim _waiterSignal = new(0);

    private int _inFlight;
    private int _waiters;
    private int _limit;
    private volatile bool _disposed;

    private ConcurrencyLimiterOptions? _lastGoodOptions;

    /// <summary>Canlı seçenekleri doğrulayarak çözer; geçersizse son geçerli seçeneklerle devam eder (AEGIS-145).</summary>
    private ConcurrencyLimiterOptions ResolveOptions() => DynamicOptionsResolver.Resolve(_staticOptions, _staticOptions.OptionsProvider, static o => o.Validate(), ref _lastGoodOptions);

    public override string Name => "ConcurrencyLimiter";

    /// <summary>Uygulanan eşzamanlılık limiti (canlı ayar değişikliği bir sonraki çağrıda uygulanır).</summary>
    public int CurrentLimit => Volatile.Read(ref _limit);

    public ConcurrencyLimiterStrategy(ConcurrencyLimiterOptions options)
    {
        _staticOptions = options ?? throw new ArgumentNullException(nameof(options));
        _staticOptions.Validate(); // fail-fast: geçersiz yapılandırma kurulum anında bildirilir (AEGIS-130)
        _limit = Math.Max(1, options.MaxConcurrentExecutions);
    }

    protected override ValueTask<Outcome<TResult>> ExecuteCoreAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        if (_disposed)
        {
            return new ValueTask<Outcome<TResult>>(Outcome<TResult>.FromException(new ObjectDisposedException(GetType().FullName)));
        }

        var options = ResolveOptions();
        var limit = ApplyLimit(options);
        if (!TryEnter(limit))
        {
            return options.QueueTimeout > TimeSpan.Zero && TryReserveQueueSlot(options.QueueLimit)
                ? WaitThenExecuteAsync(callback, context, state, options)
                : RejectAsync<TResult>(context, options);
        }

        ValueTask<Outcome<TResult>> pending;
        try
        {
            pending = callback(context, state);
        }
        catch
        {
            Exit();
            throw;
        }

        if (pending.IsCompleted)
        {
            Exit();
            return pending;
        }

        return ExitWhenCompletedAsync(pending, context);
    }

    // Limit yalnızca değiştiğinde yazılır: her çağrıda yazmak yüksek eşzamanlılıkta önbellek satırını gereksiz yere dolaştırır.
    private int ApplyLimit(ConcurrencyLimiterOptions options)
    {
        var limit = Math.Max(1, options.MaxConcurrentExecutions);
        if (Volatile.Read(ref _limit) != limit)
        {
            Volatile.Write(ref _limit, limit);
        }

        return limit;
    }

    private bool TryEnter(int limit)
    {
        var current = Volatile.Read(ref _inFlight);
        while (current < limit)
        {
            var observed = Interlocked.CompareExchange(ref _inFlight, current + 1, current);
            if (observed == current)
            {
                return true;
            }

            current = observed;
        }

        return false;
    }

    // Kuyruk sınırlıdır: yer yoksa çağrı beklemeden reddedilir. Ayrılan yer WaitThenExecuteAsync'te bırakılır.
    private bool TryReserveQueueSlot(int queueLimit)
    {
        var current = Volatile.Read(ref _waiters);
        while (current < queueLimit)
        {
            var observed = Interlocked.CompareExchange(ref _waiters, current + 1, current);
            if (observed == current)
            {
                return true;
            }

            current = observed;
        }

        return false;
    }

    // Bekleyen varsa biri uyandırılır; uyanan yeniden izin dener (başkası kaptıysa yeniden bekler).
    private void Exit()
    {
        Interlocked.Decrement(ref _inFlight);
        if (Volatile.Read(ref _waiters) > 0)
        {
            _waiterSignal.Release();
        }
    }

    private async ValueTask<Outcome<TResult>> ExitWhenCompletedAsync<TResult>(ValueTask<Outcome<TResult>> pending, AegisContext context)
    {
        try
        {
            return await pending.ConfigureAwait(context.ContinueOnCapturedContext);
        }
        finally
        {
            Exit();
        }
    }

    /// <summary>
    /// Kuyrukta bekler. Bekleyen sayısı izin denemesinden ÖNCE (<see cref="TryReserveQueueSlot"/>) artırılmıştır: böylece
    /// arada izin bırakan çağrı bekleyeni görür ve uyandırır (kayıp uyandırma olmaz). Süre, semaforun kendisi gibi gerçek saatle ölçülür.
    /// </summary>
    private async ValueTask<Outcome<TResult>> WaitThenExecuteAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state,
        ConcurrencyLimiterOptions options)
    {
        var acquired = false;
        var waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            while (!(acquired = TryEnter(CurrentLimit)))
            {
                var remaining = options.QueueTimeout - System.Diagnostics.Stopwatch.GetElapsedTime(waitStart);
                if (remaining <= TimeSpan.Zero ||
                    !await _waiterSignal.WaitAsync(AegisTimers.Normalize(remaining), context.CancellationToken).ConfigureAwait(context.ContinueOnCapturedContext))
                {
                    acquired = TryEnter(CurrentLimit); // süre dolarken bırakılan izin kaçırılmasın
                    break;
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _waiters);
        }

        if (!acquired)
        {
            return await RejectAsync<TResult>(context, options).ConfigureAwait(context.ContinueOnCapturedContext);
        }

        try
        {
            return await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
        }
        finally
        {
            Exit();
        }
    }

    private ValueTask<Outcome<TResult>> RejectAsync<TResult>(AegisContext context, ConcurrencyLimiterOptions options) =>
        RateLimiterRejection.RejectAsync<TResult>(Telemetry, context, Name, options.OnRejected,
            $"Eşzamanlılık sınırı ({CurrentLimit}) aşıldı; kuyruk dolu ya da bekleme süresi doldu.",
            retryAfter: null);

    /// <summary>
    /// Stratejiyi kapatır. Yeni çağrılar <see cref="ObjectDisposedException"/> alır; uçuştaki çağrılar
    /// etkilenmeden tamamlanır.
    /// <para>
    /// AEGIS-128: Önceki sürüm doğrudan <c>SemaphoreSlim.Dispose()</c> çağırıyordu. SemaphoreSlim, dispose
    /// edildiğinde kuyrukta bekleyen <c>WaitAsync</c> çağrılarını ASLA uyandırmaz — o istekler sonsuza kadar
    /// askıda kalır (dispose yarış testinde gerçekten gözlemlendi). Ayrıca <c>WaitAsync</c> hiçbir yönetilmeyen
    /// kaynak tutmaz (SemaphoreSlim yalnızca <c>AvailableWaitHandle</c> kullanıldığında bir olay nesnesi
    /// ayırır), dolayısıyla dispose etmemek sızıntı yaratmaz. Bu yüzden semafor kasıtlı olarak dispose edilmez.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
    }
}
