using System.Threading.RateLimiting;
using Microsoft.Extensions.Time.Testing;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;
using Polly.Retry;
using Polly.Telemetry;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Core.Telemetry;
using Aegis.TortureTests.Harness;

namespace Aegis.TortureTests.Scenarios;

/// <summary>
/// Uç seçenek değerleri: int.MaxValue deneme, TimeSpan.MaxValue gecikme/zaman aşımı/açık kalma, sonsuz zaman aşımı,
/// TimeSpan.MaxValue pencereli hız sınırı. Değişmez: yapılandırma ya kurulumda reddedilir (geçerli savunma) ya da çalışma
/// anında doğru davranır; taşma (Overflow / ArgumentOutOfRange) asla çalışma anında yükselmez.
/// </summary>
public sealed class ExtremeOptionsScenario : TortureScenario
{
    public override string Name => "Uç seçenek değerleri (taşma)";

    public override string Description =>
        "int.MaxValue deneme + 1 gün üstel gecikme + TimeSpan.MaxValue üst sınır; TimeSpan.MaxValue ve sonsuz zaman aşımı; " +
        "TimeSpan.MaxValue açık kalma; TimeSpan.MaxValue pencere. Ya kurulumda reddedilmeli ya doğru çalışmalı; çalışma anında taşma olmamalı.";

    public override IReadOnlyList<Library> Libraries => [Library.Aegis, Library.Polly];

    public override async Task RunAsync(Library library, Random random)
    {
        var aegis = library == Library.Aegis;

        // 1) Sonsuza yakın yeniden deneme + devasa gecikme: çağıran iptal edince iptal istisnası (taşma değil) yükselmeli.
        await CaseAsync("retry", () => aegis
                ? new AegisSubject(new AegisPipelineBuilder("x").AddRetry(o =>
                {
                    o.MaxRetryAttempts = int.MaxValue;
                    o.Delay = TimeSpan.FromDays(1);
                    o.MaxDelay = TimeSpan.MaxValue;
                    o.BackoffType = Aegis.Resilience.Core.Strategies.Retry.DelayBackoffType.Exponential;
                }).Build())
                : new PollySubject(new ResiliencePipelineBuilder().AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = int.MaxValue,
                    Delay = TimeSpan.FromDays(1),
                    MaxDelay = TimeSpan.MaxValue,
                    BackoffType = Polly.DelayBackoffType.Exponential
                }).Build()),
            async subject =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(random.Next(20, 120)));
                try
                {
                    await subject.ExecuteAsync<int>(_ => throw new InvalidOperationException("hep başarısız"), cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                }
            }).ConfigureAwait(false);

        // 1b) Zamanlayıcı sınırını (≈ 24,8 gün) aşan sabit gecikme gerçekten beklenir: iptal istisnası (taşma değil) yükselmeli.
        await CaseAsync("retry 100 gün", () => aegis
                ? new AegisSubject(new AegisPipelineBuilder("x").AddRetry(o =>
                {
                    o.Delay = TimeSpan.FromDays(100);
                    o.MaxDelay = TimeSpan.MaxValue;
                    o.BackoffType = Aegis.Resilience.Core.Strategies.Retry.DelayBackoffType.Constant;
                    o.UseJitter = false;
                }).Build())
                : new PollySubject(new ResiliencePipelineBuilder().AddRetry(new RetryStrategyOptions
                {
                    Delay = TimeSpan.FromDays(100),
                    MaxDelay = TimeSpan.MaxValue,
                    BackoffType = Polly.DelayBackoffType.Constant
                }).Build()),
            async subject =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(random.Next(20, 120)));
                try
                {
                    await subject.ExecuteAsync<int>(_ => throw new InvalidOperationException("hep başarısız"), cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                }
            }).ConfigureAwait(false);

        // 2) Zaman aşımı TimeSpan.MaxValue ve sonsuz: hızlı çağrı başarıyla dönmeli.
        foreach (var huge in new[] { TimeSpan.MaxValue, Timeout.InfiniteTimeSpan })
        {
            await CaseAsync($"timeout {huge}", () => aegis
                    ? new AegisSubject(new AegisPipelineBuilder("x").AddTimeout(huge).Build())
                    : new PollySubject(new ResiliencePipelineBuilder().AddTimeout(huge).Build()),
                async subject => Invariant.That(
                    await subject.ExecuteAsync(_ => ValueTask.FromResult(7), CancellationToken.None).ConfigureAwait(false) == 7,
                    "hızlı çağrı yanlış sonuç döndü")).ConfigureAwait(false);
        }

        // 3) Açık kalma TimeSpan.MaxValue: devre açılınca red (taşma değil), önerilen bekleme negatif olmamalı.
        await CaseAsync("break MaxValue", () => aegis
                ? new AegisSubject(new AegisPipelineBuilder("x")
                    .AddCircuitBreaker(o => { o.MinimumThroughput = 2; o.FailureRatio = 0.5; o.BreakDuration = TimeSpan.MaxValue; }).Build())
                : new PollySubject(new ResiliencePipelineBuilder().AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    MinimumThroughput = 2,
                    FailureRatio = 0.5,
                    BreakDuration = TimeSpan.MaxValue
                }).Build()),
            async subject =>
            {
                for (var i = 0; i < 2; i++)
                {
                    await IgnoreAsync<InvalidOperationException>(subject).ConfigureAwait(false);
                }

                try
                {
                    await subject.ExecuteAsync(_ => ValueTask.FromResult(1), CancellationToken.None).ConfigureAwait(false);
                    throw new InvariantViolationException("açık devre çağrıyı geçirdi");
                }
                catch (Aegis.Resilience.Core.Exceptions.BrokenCircuitException ex)
                {
                    Invariant.That(ex.RetryAfter is null || ex.RetryAfter >= TimeSpan.Zero, $"negatif RetryAfter: {ex.RetryAfter}");
                }
                catch (Polly.CircuitBreaker.BrokenCircuitException ex)
                {
                    Invariant.That(ex.RetryAfter is null || ex.RetryAfter >= TimeSpan.Zero, $"negatif RetryAfter: {ex.RetryAfter}");
                }
            }).ConfigureAwait(false);

        // 4) Hız sınırı: int.MaxValue izin, TimeSpan.MaxValue pencere.
        await CaseAsync("rate MaxValue", () => aegis
                ? new AegisSubject(new AegisPipelineBuilder("x").AddRateLimiter(int.MaxValue, TimeSpan.MaxValue).Build())
                : new PollySubject(new ResiliencePipelineBuilder().AddRateLimiter(new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
                {
                    PermitLimit = int.MaxValue,
                    Window = TimeSpan.MaxValue,
                    QueueLimit = 0
                })).Build()),
            async subject =>
            {
                for (var i = 0; i < 100; i++)
                {
                    await subject.ExecuteAsync(_ => ValueTask.FromResult(i), CancellationToken.None).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
    }

    private static async Task IgnoreAsync<TException>(ISubject subject)
        where TException : Exception
    {
        try
        {
            await subject.ExecuteAsync<int>(_ => throw new InvalidOperationException("başarısız"), CancellationToken.None).ConfigureAwait(false);
        }
        catch (TException)
        {
        }
    }

    private static async Task CaseAsync(string name, Func<ISubject> build, Func<ISubject, Task> run)
    {
        ISubject subject;
        try
        {
            subject = build();
        }
        catch (Exception ex) when (Failures.IsConfigurationRejection(ex))
        {
            return; // kurulumda reddetmek geçerli bir savunmadır
        }
        catch (Exception ex)
        {
            throw new InvariantViolationException($"[{name}] kurulumda beklenmeyen {ex.GetType().Name}: {ex.Message}");
        }

        await using (subject.ConfigureAwait(false))
        {
            try
            {
                await run(subject).ConfigureAwait(false);
            }
            catch (InvariantViolationException ex)
            {
                throw new InvariantViolationException($"[{name}] {ex.Message}");
            }
            catch (Exception ex)
            {
                throw new InvariantViolationException($"[{name}] çalışma anında {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// Kullanıcı geri çağrıları kötü davranır: OnRetry, koşul, gecikme üreteci, devre olayları ve telemetri dinleyicisi (her biri ayrı ayrı)
/// fırlatır. Değişmezler: kilitlenme yok; sonuç başarı, özgün hata ya da geri çağrının hatası; en önemlisi boru hattı
/// zehirlenmez: devre takılı kalmaz (sağlam çağrı en geç birkaç açık kalma süresi sonra yine başarılı olur).
/// </summary>
public sealed class MisbehavingCallbacksScenario : TortureScenario
{
    public override string Name => "Fırlatan geri çağrılar (zehirlenme)";

    public override string Description =>
        "OnRetry, ShouldHandle, DelayGenerator, OnOpened/OnHalfOpened/OnClosed ve telemetri dinleyicisi fırlatır. Kilitlenme olmamalı; " +
        "devre takılı kalmamalı: hata kesilince sağlam çağrı birkaç açık kalma süresi içinde yine başarılı olmalı.";

    public override IReadOnlyList<Library> Libraries => [Library.Aegis, Library.Polly];

    private sealed class ThrowingAegisListener : AegisTelemetryListener
    {
        public override void Write(in AegisTelemetryEvent telemetryEvent) => throw new InvalidOperationException("dinleyici");
    }

    private sealed class ThrowingPollyListener : TelemetryListener
    {
        public override void Write<TResult, TArgs>(in TelemetryEventArguments<TResult, TArgs> args) => throw new InvalidOperationException("dinleyici");
    }

    /// <summary>Her biri tek başına fırlatılan geri çağrılar; kalan durumda hangi geri çağrının zehirlediği rapora yazılır.</summary>
    private static readonly string[] Faults = ["OnRetry", "ShouldHandle", "DelayGenerator", "OnOpened", "OnHalfOpened", "OnClosed", "Listener"];

    public override async Task RunAsync(Library library, Random random)
    {
        var poisoned = new List<string>();
        foreach (var fault in Faults.OrderBy(_ => random.Next()))
        {
            if (!await SurvivesAsync(library, fault).ConfigureAwait(false))
            {
                poisoned.Add(fault);
            }
        }

        Invariant.That(poisoned.Count == 0,
            $"fırlatan {string.Join(", ", poisoned)} yüzünden hata kesildikten 5 açık kalma süresi sonra bile sağlam çağrı geçemedi (boru hattı zehirlendi)");
    }

    /// <summary>Hata dönemi + fırlatan geri çağrı; ardından hata kesilince sağlam çağrı birkaç açık kalma süresinde geçmeli.</summary>
    private static async Task<bool> SurvivesAsync(Library library, string fault)
    {
        var clock = new FakeTimeProvider();
        var failing = true;
        var calls = 0;
        await using var subject = Build(library, clock, fault);

        for (var cycle = 0; cycle < 6; cycle++)
        {
            for (var i = 0; i < 6; i++)
            {
                await ExecuteTolerantlyAsync(subject, () => failing && Interlocked.Increment(ref calls) % 2 == 0).ConfigureAwait(false);
            }

            clock.Advance(TimeSpan.FromSeconds(31));
        }

        failing = false;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            clock.Advance(TimeSpan.FromSeconds(31));
            if (await ExecuteTolerantlyAsync(subject, () => false).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Çağrıyı yapar; başarıyı döner. Geri çağrı hataları ve devre reddi kabul edilir, başka tip kabul edilmez.</summary>
    private static async Task<bool> ExecuteTolerantlyAsync(ISubject subject, Func<bool> fail)
    {
        try
        {
            await subject.ExecuteAsync(_ => fail() ? throw new TimeoutException("bağımlılık") : ValueTask.FromResult(1), CancellationToken.None)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException || Failures.IsBrokenCircuit(ex))
        {
            return false;
        }
    }

    private static ISubject Build(Library library, TimeProvider clock, string fault)
    {
        var predicateCalls = 0;
        bool ShouldHandle(Exception ex) => fault == "ShouldHandle" && Interlocked.Increment(ref predicateCalls) % 3 == 0
            ? throw new InvalidOperationException("ShouldHandle")
            : ex is TimeoutException;
        ValueTask Event(string name) => fault == name ? throw new InvalidOperationException(name) : default;

        if (library == Library.Aegis)
        {
            var builder = new AegisPipelineBuilder("poison")
                .AddRetry(o =>
                {
                    o.MaxRetryAttempts = 2;
                    o.Delay = TimeSpan.Zero;
                    o.ShouldHandle = ShouldHandle;
                    o.OnRetry = _ => Event("OnRetry");
                    o.DelayGenerator = _ => fault == "DelayGenerator" ? throw new InvalidOperationException(fault) : ValueTask.FromResult<TimeSpan?>(null);
                })
                .AddCircuitBreaker(o =>
                {
                    o.MinimumThroughput = 2;
                    o.FailureRatio = 0.5;
                    o.SamplingDuration = TimeSpan.FromSeconds(30);
                    o.BreakDuration = TimeSpan.FromSeconds(30);
                    o.ShouldHandle = ShouldHandle;
                    o.OnOpened = _ => Event("OnOpened");
                    o.OnHalfOpened = _ => Event("OnHalfOpened");
                    o.OnClosed = _ => Event("OnClosed");
                })
                .WithTimeProvider(clock);
            if (fault == "Listener")
            {
                builder.WithTelemetry(t => t.Listeners.Add(new ThrowingAegisListener()));
            }

            return new AegisSubject(builder.Build());
        }

        var polly = new ResiliencePipelineBuilder { TimeProvider = clock };
        if (fault == "Listener")
        {
            polly.ConfigureTelemetry(new TelemetryOptions { TelemetryListeners = { new ThrowingPollyListener() } });
        }

        polly
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero,
                ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception is { } ex && ShouldHandle(ex)),
                OnRetry = _ => Event("OnRetry"),
                DelayGenerator = _ => fault == "DelayGenerator" ? throw new InvalidOperationException(fault) : ValueTask.FromResult<TimeSpan?>(null)
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                MinimumThroughput = 2,
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception is { } ex && ShouldHandle(ex)),
                OnOpened = _ => Event("OnOpened"),
                OnHalfOpened = _ => Event("OnHalfOpened"),
                OnClosed = _ => Event("OnClosed")
            });
        return new PollySubject(polly.Build());
    }
}

/// <summary>
/// Boru hattı, üzerinde 32 eşzamanlı çağrı varken ve yeni çağrılar başlarken dispose edilir.
/// Değişmez: yalnızca başarı, ObjectDisposedException ya da iptal; iç durum hatası (NullReference vb.) ve kilitlenme yok.
/// </summary>
public sealed class DisposeRaceScenario : TortureScenario
{
    public override string Name => "Çağrı sürerken dispose yarışı";

    public override string Description =>
        "32 iş parçacığı sürekli çağırırken boru hattı (Polly: kayıt defteri) rastgele anda dispose edilir. Yalnızca başarı, " +
        "ObjectDisposedException ya da iptal yükselmeli; NullReference/InvalidOperation gibi iç hata ve kilitlenme olmamalı.";

    public override IReadOnlyList<Library> Libraries => [Library.Aegis, Library.Polly];

    public override async Task RunAsync(Library library, Random random)
    {
        ISubject subject;
        if (library == Library.Aegis)
        {
            subject = new AegisSubject(Chains.AegisStandard().AddConcurrencyLimiter(16, o => o.QueueTimeout = TimeSpan.FromMilliseconds(5)).Build());
        }
        else
        {
            var registry = new ResiliencePipelineRegistry<string>();
            var pipeline = registry.GetOrAddPipeline("k", b => Chains.PollyStandardInto(b).AddConcurrencyLimiter(16, 16));
            subject = new PollySubject(pipeline, registry);
        }

        using var stop = new CancellationTokenSource();
        var workers = Enumerable.Range(0, 32).Select(w => Task.Run(async () =>
        {
            var rnd = new Random(w);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await subject.ExecuteAsync(async ct =>
                    {
                        await Task.Delay(rnd.Next(0, 3), ct).ConfigureAwait(false);
                        return 1;
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException
                    || Failures.IsTimeout(ex) || Failures.IsBrokenCircuit(ex) || Failures.IsRejected(ex))
                {
                }
            }
        })).ToArray();

        await Task.Delay(random.Next(5, 40)).ConfigureAwait(false);
        await subject.DisposeAsync().ConfigureAwait(false);
        await Task.Delay(50).ConfigureAwait(false);
        stop.Cancel();
        await Task.WhenAll(workers).ConfigureAwait(false);
    }
}

/// <summary>
/// Seçenekler çalışırken sürekli değişir (Aegis: OptionsProvider; Polly: kayıt defteri yeniden yükleme belirteci);
/// arada geçersiz değerler de gelir. Değişmez: çağrılar yalnızca beklenen sonuçları görür, kilitlenme yok.
/// </summary>
public sealed class ReloadStormScenario : TortureScenario
{
    public override string Name => "Yeniden yükleme fırtınası (geçersiz değer dahil)";

    public override string Description =>
        "16 iş parçacığı çağırırken seçenekler her milisaniye değişir (Aegis: OptionsProvider, Polly: AddReloadToken); " +
        "değerlerin %20'si geçersizdir. Çağrılar yalnızca başarı / işlenen hata / devre reddi görmeli; kilitlenme olmamalı.";

    public override IReadOnlyList<Library> Libraries => [Library.Aegis, Library.Polly];

    public override async Task RunAsync(Library library, Random random)
    {
        var generation = 1; // ilk kurulum geçerli; geçersiz değerler yalnızca yeniden yüklemede gelir
        var reloadToken = new CancellationTokenSource();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));

        // Her nesil: %20 geçersiz (FailureRatio > 1), deneme sayısı 1..3.
        double FailureRatio(int g) => g % 5 == 0 ? 2.0 : 0.5;
        int RetryAttempts(int g) => 1 + (g % 3);

        await using var subject = library == Library.Aegis
            ? AegisReloading(() => Volatile.Read(ref generation), FailureRatio, RetryAttempts)
            : PollyReloading(() => Volatile.Read(ref generation), () => Volatile.Read(ref reloadToken).Token, FailureRatio, RetryAttempts);

        var reloader = Task.Run(async () =>
        {
            var rnd = new Random(random.Next());
            while (!stop.IsCancellationRequested)
            {
                Volatile.Write(ref generation, rnd.Next(1000));
                using var previous = Interlocked.Exchange(ref reloadToken, new CancellationTokenSource());
                await previous.CancelAsync().ConfigureAwait(false);
                await Task.Delay(1).ConfigureAwait(false);
            }
        });

        var workers = Enumerable.Range(0, 16).Select(w => Task.Run(async () =>
        {
            var local = new Random(w);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await subject.ExecuteAsync(_ => local.Next(3) == 0
                        ? throw new InvalidOperationException("geçici")
                        : ValueTask.FromResult(1), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException { Message: "geçici" } || Failures.IsBrokenCircuit(ex))
                {
                }
            }
        })).ToArray();

        await Task.WhenAll(workers.Append(reloader)).ConfigureAwait(false);
        reloadToken.Dispose();
    }

    private static ISubject AegisReloading(Func<int> generation, Func<int, double> failureRatio, Func<int, int> retryAttempts) =>
        new AegisSubject(new AegisPipelineBuilder("reload")
            .AddRetry(new RetryOptions
            {
                Delay = TimeSpan.Zero,
                OptionsProvider = () => new RetryOptions { MaxRetryAttempts = retryAttempts(generation()), Delay = TimeSpan.Zero }
            })
            .AddCircuitBreaker(new CircuitBreakerOptions
            {
                MinimumThroughput = 4,
                OptionsProvider = () => new CircuitBreakerOptions
                {
                    MinimumThroughput = 4,
                    FailureRatio = failureRatio(generation()),
                    BreakDuration = TimeSpan.FromMilliseconds(500)
                }
            })
            .Build());

    private static ISubject PollyReloading(
        Func<int> generation, Func<CancellationToken> reloadToken, Func<int, double> failureRatio, Func<int, int> retryAttempts)
    {
        var registry = new ResiliencePipelineRegistry<string>();
        var pipeline = registry.GetOrAddPipeline("k", (builder, context) =>
        {
            context.AddReloadToken(reloadToken());
            var g = generation();
            builder
                .AddRetry(new RetryStrategyOptions { MaxRetryAttempts = retryAttempts(g), Delay = TimeSpan.Zero })
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    MinimumThroughput = 4,
                    FailureRatio = failureRatio(g),
                    BreakDuration = TimeSpan.FromMilliseconds(500)
                });
        });
        return new PollySubject(pipeline, registry);
    }
}

/// <summary>
/// Saat 100 yıl ve 1000 yıl ileri sıçrar (FakeTimeProvider). Değişmezler: açık devre sıçramadan sonra deneme isteği
/// kabul edip kapanır; bekleyen çağrının zaman aşımı sıçramada tetiklenir; hiçbir hesap taşmaz.
/// </summary>
public sealed class TimeJumpScenario : TortureScenario
{
    public override string Name => "Saat sıçraması (100 / 1000 yıl)";

    public override string Description =>
        "Sahte saat 100 ve 1000 yıl ileri alınır. Açık devre sıçramadan sonra kapanabilmeli; bekleyen çağrının zaman aşımı " +
        "tetiklenmeli; süre hesapları taşmamalı.";

    public override IReadOnlyList<Library> Libraries => [Library.Aegis, Library.Polly];

    public override async Task RunAsync(Library library, Random random)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        ISubject subject = library == Library.Aegis
            ? new AegisSubject(new AegisPipelineBuilder("jump")
                .AddCircuitBreaker(o => { o.MinimumThroughput = 2; o.FailureRatio = 0.5; o.BreakDuration = TimeSpan.FromSeconds(30); })
                .AddTimeout(TimeSpan.FromSeconds(10))
                .WithTimeProvider(clock)
                .Build())
            : new PollySubject(new ResiliencePipelineBuilder { TimeProvider = clock }
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions { MinimumThroughput = 2, FailureRatio = 0.5, BreakDuration = TimeSpan.FromSeconds(30) })
                .AddTimeout(TimeSpan.FromSeconds(10))
                .Build());
        await using var _ = subject.ConfigureAwait(false);

        await TripAsync(subject).ConfigureAwait(false);
        clock.Advance(TimeSpan.FromDays(365 * 100));
        Invariant.That(await subject.ExecuteAsync(_ => ValueTask.FromResult(1), CancellationToken.None).ConfigureAwait(false) == 1,
            "100 yıllık sıçramadan sonra deneme isteği geçmedi");

        var pending = subject.ExecuteAsync(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return 1;
        }, CancellationToken.None).AsTask();
        await Task.Delay(random.Next(1, 20)).ConfigureAwait(false);
        clock.Advance(TimeSpan.FromDays(365 * 1000));
        var finished = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        Invariant.That(finished == pending, "1000 yıllık sıçramada bekleyen çağrının zaman aşımı tetiklenmedi");
        try
        {
            await pending.ConfigureAwait(false);
            throw new InvariantViolationException("zaman aşımına düşmesi gereken çağrı başarıyla döndü");
        }
        catch (Exception ex) when (Failures.IsTimeout(ex))
        {
        }

        await TripAsync(subject).ConfigureAwait(false);
    }

    private static async Task TripAsync(ISubject subject)
    {
        for (var i = 0; i < 2; i++)
        {
            try
            {
                await subject.ExecuteAsync<int>(_ => throw new InvalidOperationException("başarısız"), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException || Failures.IsBrokenCircuit(ex))
            {
                // önceki bir hata (ör. zaman aşımı) pencerede sayıldıysa devre ilk denemede açılmış olabilir
            }
        }

        try
        {
            await subject.ExecuteAsync(_ => ValueTask.FromResult(1), CancellationToken.None).ConfigureAwait(false);
            throw new InvariantViolationException("devre açılmadı");
        }
        catch (Exception ex) when (Failures.IsBrokenCircuit(ex))
        {
        }
    }
}

/// <summary>
/// Hız sınırı (1 saatte 100 izin) 64 iş parçacığından 12.800 çağrıyla dövülür.
/// Değişmez: TAM 100 çağrı kabul edilir (fazla kabul de eksik kabul de hata).
/// </summary>
public sealed class RateLimiterAdmissionScenario : TortureScenario
{
    private const int Permits = 100;

    public override string Name => "Hız sınırı: fazla kabul yok";

    public override string Description => "1 saatlik pencerede 100 izin; 64×200 eşzamanlı çağrı. Tam olarak 100 çağrı kabul edilmeli.";

    public override IReadOnlyList<Library> Libraries => [Library.Aegis, Library.Polly];

    public override async Task RunAsync(Library library, Random random)
    {
        await using ISubject subject = library == Library.Aegis
            ? new AegisSubject(new AegisPipelineBuilder("rate").AddRateLimiter(Permits, TimeSpan.FromHours(1)).Build())
            : new PollySubject(new ResiliencePipelineBuilder().AddRateLimiter(new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = Permits,
                Window = TimeSpan.FromHours(1),
                QueueLimit = 0
            })).Build());

        var admitted = 0;
        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                try
                {
                    await subject.ExecuteAsync(_ => ValueTask.FromResult(Interlocked.Increment(ref admitted)), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (Failures.IsRejected(ex))
                {
                }
            }
        }))).ConfigureAwait(false);

        Invariant.That(admitted == Permits, $"{admitted} çağrı kabul edildi (tam {Permits} bekleniyordu)");
    }
}

/// <summary>
/// Bellek dayanıklılığı: 1.000.000 yürütme (8 iş parçacığı, %1 hata) aynı boru hattından geçer.
/// Değişmez: canlı bellek artışı 4 MB'ı aşmaz (sızıntı yok). Ayrıca çağrı başına tahsis günlüğe yazılır.
/// </summary>
public sealed class MemorySoakScenario(TextWriter log) : TortureScenario
{
    private const int Calls = 1_000_000;

    public override string Name => "Bellek dayanıklılığı (1M çağrı)";

    public override string Description => "Retry → devre kesici → zaman aşımı zincirinden 1.000.000 çağrı (%1 hata). Canlı bellek 4 MB'tan fazla büyümemeli.";

    public override TimeSpan Budget => TimeSpan.FromSeconds(120);

    public override IReadOnlyList<Library> Libraries => [Library.Aegis, Library.Polly];

    public override async Task RunAsync(Library library, Random random)
    {
        await using var subject = Chains.Standard(library);
        await RunCallsAsync(subject, 10_000).ConfigureAwait(false); // ısınma: JIT, havuzlar

        var before = GC.GetTotalMemory(forceFullCollection: true);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        await RunCallsAsync(subject, Calls).ConfigureAwait(false);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var growth = GC.GetTotalMemory(forceFullCollection: true) - before;

        lock (log)
        {
            log.WriteLine($"    [{library}] çağrı başına tahsis ≈ {allocated / (double)Calls:F0} B, canlı bellek değişimi {growth / 1024.0:F0} KB");
        }

        Invariant.That(growth < 4 * 1024 * 1024, $"canlı bellek {growth / 1024 / 1024} MB büyüdü (sızıntı)");
    }

    private static Task RunCallsAsync(ISubject subject, int calls) =>
        Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            for (var i = w; i < calls; i += 8)
            {
                try
                {
                    await subject.ExecuteAsync(static (ct) => ValueTask.FromResult(1), CancellationToken.None).ConfigureAwait(false);
                    if (i % 100 == 0)
                    {
                        await subject.ExecuteAsync<int>(static _ => throw new InvalidOperationException("geçici"), CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException || Failures.IsBrokenCircuit(ex))
                {
                }
            }
        })));
}
