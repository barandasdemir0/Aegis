using Polly;
using Polly.Hedging;
using Aegis.Resilience.Core.Pipeline;
using Aegis.TortureTests.Harness;

namespace Aegis.TortureTests.Scenarios;

/// <summary>
/// 32 iş parçacığı × 400 çağrı; çağrıların bir kısmı rastgele anda çağıran tarafından iptal edilir, bir kısmı fırlatır,
/// bir kısmı zaman aşımına düşer, devre açılıp kapanır.
/// Değişmezler: her çağrı KENDİ sonucunu alır (çapraz karışma yok); çağıran iptal etmediyse iptal istisnası sızmaz;
/// yalnızca beklenen istisna tipleri yükselir; kilitlenme yok.
/// </summary>
public sealed class ThreadStormScenario : TortureScenario
{
    public override string Name => "Thread fırtınası + rastgele iptal";

    public override string Description =>
        "32×400 eşzamanlı çağrı (retry → devre kesici → 50 ms zaman aşımı); %20 fırlatır, %10 yavaş, %10 çağıran rastgele anda iptal eder. " +
        "Her çağrı kendi sonucunu almalı, çağıran iptal etmeden OperationCanceledException sızmamalı, yalnızca beklenen tipler yükselmeli.";

    public override IReadOnlyList<Library> Libraries => [Library.Aegis, Library.Polly];

    public override async Task RunAsync(Library library, Random random)
    {
        await using var subject = Chains.Standard(library);
        var seeds = Enumerable.Range(0, 32).Select(_ => random.Next()).ToArray();
        await Task.WhenAll(seeds.Select(seed => Task.Run(() => WorkerAsync(subject, new Random(seed))))).ConfigureAwait(false);
    }

    private static async Task WorkerAsync(ISubject subject, Random random)
    {
        for (var i = 0; i < 400; i++)
        {
            var id = random.Next();
            var mode = random.Next(100);
            var delay = random.Next(0, 100);
            using var cts = new CancellationTokenSource();
            if (mode >= 90)
            {
                cts.CancelAfter(random.Next(0, 5));
            }

            try
            {
                var result = await subject.ExecuteAsync(async ct =>
                {
                    if (mode < 60)
                    {
                        return id;
                    }

                    if (mode < 80)
                    {
                        throw new InvalidOperationException("geçici");
                    }

                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    return id;
                }, cts.Token).ConfigureAwait(false);

                Invariant.That(result == id, "çağrı başka bir çağrının sonucunu aldı (çapraz karışma)");
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException ex)
            {
                throw new InvariantViolationException($"çağıran iptal etmeden iptal istisnası sızdı: {ex.GetType().Name}");
            }
            catch (InvalidOperationException ex) when (ex.Message == "geçici")
            {
            }
            catch (Exception ex) when (Failures.IsTimeout(ex) || Failures.IsBrokenCircuit(ex))
            {
            }
        }
    }
}

/// <summary>
/// Eşzamanlılık sınırlayıcısı (8 izin) 64 iş parçacığıyla, beklerken ve çalışırken rastgele iptal edilerek dövülür.
/// Değişmezler: aynı anda en fazla 8 çağrı içeride; fırtınadan sonra TAM 8 izin geri alınabilir (izin sızıntısı ya da
/// fazla kabul yok); 9. çağrı içeri giremez.
/// </summary>
public sealed class ConcurrencyLimiterLeakScenario : TortureScenario
{
    private const int Permits = 8;

    public override string Name => "Eşzamanlılık sınırı: izin sızıntısı";

    public override string Description =>
        "8 izinli sınırlayıcı, 64×200 çağrı, bekleme ve yürütme sırasında rastgele iptal. İçerideki çağrı sayısı hiç 8'i aşmamalı; " +
        "fırtına sonrası tam 8 çağrı aynı anda girebilmeli, 9. giremez (sızan ya da fazladan izin yok).";

    public override IReadOnlyList<Library> Libraries => [Library.Aegis, Library.Polly];

    public override async Task RunAsync(Library library, Random random)
    {
        await using ISubject subject = library == Library.Aegis
            ? new AegisSubject(new AegisPipelineBuilder("limiter")
                .AddConcurrencyLimiter(Permits, o => o.QueueTimeout = TimeSpan.FromMilliseconds(random.Next(0, 20)))
                .Build())
            : new PollySubject(new ResiliencePipelineBuilder().AddConcurrencyLimiter(Permits, queueLimit: 16).Build());

        var inside = 0;
        var maxInside = 0;
        var seeds = Enumerable.Range(0, 64).Select(_ => random.Next()).ToArray();
        await Task.WhenAll(seeds.Select(seed => Task.Run(async () =>
        {
            var rnd = new Random(seed);
            for (var i = 0; i < 200; i++)
            {
                using var cts = new CancellationTokenSource();
                if (rnd.Next(100) < 30)
                {
                    cts.CancelAfter(rnd.Next(0, 3));
                }

                var work = rnd.Next(0, 3);
                try
                {
                    await subject.ExecuteAsync(async ct =>
                    {
                        var now = Interlocked.Increment(ref inside);
                        InterlockedMax(ref maxInside, now);
                        try
                        {
                            await Task.Delay(work, ct).ConfigureAwait(false);
                            return 0;
                        }
                        finally
                        {
                            Interlocked.Decrement(ref inside);
                        }
                    }, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                }
                catch (Exception ex) when (Failures.IsRejected(ex))
                {
                }
            }
        }))).ConfigureAwait(false);

        Invariant.That(maxInside <= Permits, $"aynı anda {maxInside} çağrı içeride (sınır {Permits})");
        await AssertExactCapacityAsync(subject).ConfigureAwait(false);
    }

    private static async Task AssertExactCapacityAsync(ISubject subject)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var blockers = Enumerable.Range(0, Permits).Select(_ => subject.ExecuteAsync(async _ =>
        {
            Interlocked.Increment(ref entered);
            await gate.Task.ConfigureAwait(false);
            return 0;
        }, CancellationToken.None).AsTask()).ToArray();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref entered) < Permits && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        var enteredBefore = Volatile.Read(ref entered);
        using var extraCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var extraEntered = false;
        try
        {
            await subject.ExecuteAsync(_ => { extraEntered = true; return ValueTask.FromResult(0); }, extraCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException || Failures.IsRejected(ex))
        {
        }

        gate.SetResult();
        await Task.WhenAll(blockers).ConfigureAwait(false);

        Invariant.That(enteredBefore == Permits, $"fırtınadan sonra yalnızca {enteredBefore}/{Permits} izin alınabildi (izin sızdı)");
        Invariant.That(!extraEntered, "sınır doluyken 9. çağrı içeri girdi (fazladan izin)");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}

/// <summary>
/// Tek iş parçacıklı eşitleme bağlamında (UI / eski ASP.NET) async yürütmeyi senkron bekleme ve senkron Execute.
/// Kütüphane içeride yakalanan bağlama dönerse kilitlenir.
/// </summary>
public sealed class SyncOverAsyncScenario : TortureScenario
{
    public override string Name => "Tek iş parçacıklı bağlamda sync-over-async";

    public override string Description =>
        "UI benzeri tek iş parçacıklı SynchronizationContext içinde ExecuteAsync(...).GetAwaiter().GetResult() ve senkron Execute; " +
        "retry (1 ms bekleme) + zaman aşımı. Kütüphane yakalanan bağlama dönerse kilitlenir.";

    public override TimeSpan Budget => TimeSpan.FromSeconds(20);

    public override IReadOnlyList<Library> Libraries => [Library.Aegis, Library.Polly];

    public override async Task RunAsync(Library library, Random random)
    {
        await using ISubject subject = library == Library.Aegis
            ? new AegisSubject(new AegisPipelineBuilder("sync")
                .AddRetry(o => { o.MaxRetryAttempts = 3; o.Delay = TimeSpan.FromMilliseconds(1); o.UseJitter = false; })
                .AddTimeout(TimeSpan.FromSeconds(2))
                .Build())
            : new PollySubject(new ResiliencePipelineBuilder()
                .AddRetry(new Polly.Retry.RetryStrategyOptions { MaxRetryAttempts = 3, Delay = TimeSpan.FromMilliseconds(1), UseJitter = false })
                .AddTimeout(TimeSpan.FromSeconds(2))
                .Build());

        using var context = new SingleThreadSynchronizationContext();
        await context.RunAsync(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                var asyncCalls = 0;
                var asyncResult = subject.ExecuteAsync(async ct =>
                {
                    await Task.Delay(1, ct).ConfigureAwait(false);
                    return ++asyncCalls < 3 ? throw new InvalidOperationException("geçici") : asyncCalls;
                }, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                Invariant.That(asyncResult == 3, $"async yürütme {asyncResult}. denemede bitti (3 bekleniyordu)");

                var syncCalls = 0;
                var syncResult = subject.Execute(_ => ++syncCalls < 3 ? throw new InvalidOperationException("geçici") : syncCalls, CancellationToken.None);
                Invariant.That(syncResult == 3, $"senkron yürütme {syncResult}. denemede bitti (3 bekleniyordu)");
            }
        }).ConfigureAwait(false);
    }
}

/// <summary>
/// Çağıran, zaman aşımı veya hedging içinde beklenen çağrıyı iptal eder. Polly 8.8'de düzeltilen hata: sızan iptal
/// istisnası iç (stratejinin) token'ını taşıyordu. Değişmez: istisnanın token'ı çağıranın token'ıdır; hedging'de
/// başlatılan tüm denemeler iptali görür (yetim deneme kalmaz).
/// </summary>
public sealed class CallerTokenScenario : TortureScenario
{
    public override string Name => "Çağıran iptali: token yayılımı";

    public override string Description =>
        "Zaman aşımı (10 sn) ve hedging (3 deneme, 5 ms) içinde bekleyen çağrı rastgele anda iptal edilir. Yükselen " +
        "OperationCanceledException.CancellationToken çağıranın token'ı olmalı; hedging'de başlatılan her deneme iptali görmeli.";

    public override IReadOnlyList<Library> Libraries => [Library.Aegis, Library.Polly];

    public override async Task RunAsync(Library library, Random random)
    {
        await using ISubject timeout = library == Library.Aegis
            ? new AegisSubject(new AegisPipelineBuilder("token-timeout").AddTimeout(TimeSpan.FromSeconds(10)).Build())
            : new PollySubject(new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(10)).Build());
        var hedging = HedgingSubject(library);

        for (var i = 0; i < 30; i++)
        {
            await AssertCallerTokenAsync(timeout, random.Next(1, 30), expectedAttempts: 1).ConfigureAwait(false);
            await AssertCallerTokenAsync(hedging, random.Next(12, 40), expectedAttempts: 3).ConfigureAwait(false);
        }
    }

    private static Func<Func<CancellationToken, ValueTask<int>>, CancellationToken, ValueTask<int>> HedgingSubject(Library library)
    {
        if (library == Library.Aegis)
        {
            var aegis = new AegisPipelineBuilder("token-hedging")
                .AddHedging(o => { o.MaxHedgedAttempts = 2; o.HedgingDelay = TimeSpan.FromMilliseconds(5); })
                .Build();
            return (callback, ct) => aegis.ExecuteAsync(callback, ct);
        }

        var polly = new ResiliencePipelineBuilder<int>()
            .AddHedging(new HedgingStrategyOptions<int> { MaxHedgedAttempts = 2, Delay = TimeSpan.FromMilliseconds(5) })
            .Build();
        return (callback, ct) => polly.ExecuteAsync(callback, ct);
    }

    private static Task AssertCallerTokenAsync(ISubject subject, int cancelAfterMs, int expectedAttempts) =>
        AssertCallerTokenAsync((callback, ct) => subject.ExecuteAsync(callback, ct), cancelAfterMs, expectedAttempts);

    private static async Task AssertCallerTokenAsync(
        Func<Func<CancellationToken, ValueTask<int>>, CancellationToken, ValueTask<int>> execute, int cancelAfterMs, int expectedAttempts)
    {
        using var caller = new CancellationTokenSource();
        var started = 0;
        var observedCancel = 0;
        caller.CancelAfter(cancelAfterMs);
        try
        {
            await execute(async ct =>
            {
                Interlocked.Increment(ref started);
                try
                {
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Increment(ref observedCancel);
                }

                return 0;
            }, caller.Token).ConfigureAwait(false);
            throw new InvariantViolationException("iptal edilen çağrı başarıyla döndü");
        }
        catch (OperationCanceledException ex)
        {
            Invariant.That(ex.CancellationToken == caller.Token, "iptal istisnası çağıranın değil stratejinin token'ını taşıyor");
        }

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Volatile.Read(ref observedCancel) < Volatile.Read(ref started) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5).ConfigureAwait(false);
        }

        Invariant.That(observedCancel == started, $"{started - observedCancel} deneme iptali görmedi (yetim deneme çalışıyor)");
        Invariant.That(started <= expectedAttempts, $"{started} deneme başladı (en fazla {expectedAttempts})");
    }
}
