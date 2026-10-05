using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Polly;
using Polly.CircuitBreaker;
using Polly.Hedging;
using Polly.Retry;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;
using AegisBroken = Aegis.Resilience.Core.Exceptions.BrokenCircuitException;
using AegisBackoff = Aegis.Resilience.Core.Strategies.Retry.DelayBackoffType;
using PollyBroken = Polly.CircuitBreaker.BrokenCircuitException;

BenchmarkSwitcher.FromAssembly(typeof(PollyVsAegis).Assembly).Run(args);

/// <summary>
/// Aegis ve Polly v8 (Polly.Core) karşılaştırması. Her kategoride iki kütüphane AYNI yapılandırmayla,
/// AYNI geri çağrıyla ölçülür; Polly referanstır (Ratio = Aegis / Polly). Geri çağrılar statik ya da önbelleğe
/// alınmış temsilcidir, böylece kapanış (closure) tahsisi iki tarafa da eklenmez.
/// Ölçülen şey kütüphanenin KENDİ ek yüküdür (geri çağrı iş yapmaz).
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class PollyVsAegis
{
    private static readonly TimeSpan Long = TimeSpan.FromHours(1);

    private ResiliencePipeline _pollyEmpty = null!, _pollyRetry = null!, _pollyTimeout = null!, _pollyBreaker = null!,
        _pollyStandard = null!, _pollyLimiter = null!, _pollyOpen = null!;
    private ResiliencePipeline<int> _pollyHedging = null!;
    private IAegisPipeline _aegisEmpty = null!, _aegisRetry = null!, _aegisTimeout = null!, _aegisBreaker = null!,
        _aegisStandard = null!, _aegisLimiter = null!, _aegisOpen = null!, _aegisHedging = null!;

    // İlk denemede hata, ikincide başarı: yeniden deneme yolunun (istisna + tekrar) maliyeti
    private int _pollyCalls, _aegisCalls;
    private Func<CancellationToken, ValueTask<int>> _pollyFlaky = null!;
    private Func<Aegis.Resilience.Core.Context.AegisContext, ValueTask<int>> _aegisFlaky = null!;

    [GlobalSetup]
    public void Setup()
    {
        var pollyRetry = new RetryStrategyOptions
        {
            MaxRetryAttempts = 3, Delay = TimeSpan.Zero, BackoffType = DelayBackoffType.Constant, UseJitter = false,
            ShouldHandle = new PredicateBuilder().Handle<InvalidOperationException>()
        };
        var pollyBreaker = new CircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5, MinimumThroughput = 100, SamplingDuration = TimeSpan.FromSeconds(30), BreakDuration = TimeSpan.FromSeconds(30)
        };

        _pollyEmpty = new ResiliencePipelineBuilder().Build();
        _pollyRetry = new ResiliencePipelineBuilder().AddRetry(pollyRetry).Build();
        _pollyTimeout = new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(10)).Build();
        _pollyBreaker = new ResiliencePipelineBuilder().AddCircuitBreaker(pollyBreaker).Build();
        _pollyStandard = new ResiliencePipelineBuilder()
            .AddTimeout(TimeSpan.FromSeconds(30)).AddRetry(pollyRetry).AddCircuitBreaker(pollyBreaker).AddTimeout(TimeSpan.FromSeconds(10)).Build();
        _pollyLimiter = new ResiliencePipelineBuilder().AddConcurrencyLimiter(100, 0).Build();
        _pollyHedging = new ResiliencePipelineBuilder<int>()
            .AddHedging(new HedgingStrategyOptions<int> { MaxHedgedAttempts = 1, Delay = TimeSpan.FromSeconds(1) }).Build();
        _pollyOpen = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions { FailureRatio = 0.5, MinimumThroughput = 2, SamplingDuration = Long, BreakDuration = Long })
            .Build();

        void AegisRetry(Aegis.Resilience.Core.Strategies.Retry.RetryOptions o)
        {
            o.MaxRetryAttempts = 3; o.Delay = TimeSpan.Zero; o.BackoffType = AegisBackoff.Constant; o.UseJitter = false;
            o.ShouldHandle = ex => ex is InvalidOperationException;
        }

        void AegisBreaker(Aegis.Resilience.Core.Strategies.CircuitBreaker.CircuitBreakerOptions o)
        {
            o.FailureRatio = 0.5; o.MinimumThroughput = 100; o.SamplingDuration = TimeSpan.FromSeconds(30); o.BreakDuration = TimeSpan.FromSeconds(30);
        }

        _aegisEmpty = new AegisPipelineBuilder("empty").Build();
        _aegisRetry = new AegisPipelineBuilder("retry").AddRetry(AegisRetry).Build();
        _aegisTimeout = new AegisPipelineBuilder("timeout").AddTimeout(TimeSpan.FromSeconds(10)).Build();
        _aegisBreaker = new AegisPipelineBuilder("breaker").AddCircuitBreaker(AegisBreaker).Build();
        _aegisStandard = new AegisPipelineBuilder("standard")
            .AddTimeout(TimeSpan.FromSeconds(30)).AddRetry(AegisRetry).AddCircuitBreaker(AegisBreaker).AddTimeout(TimeSpan.FromSeconds(10)).Build();
        _aegisLimiter = new AegisPipelineBuilder("limiter").AddConcurrencyLimiter(100).Build();
        _aegisHedging = new AegisPipelineBuilder("hedging").AddHedging(o => { o.MaxHedgedAttempts = 1; o.HedgingDelay = TimeSpan.FromSeconds(1); }).Build();
        _aegisOpen = new AegisPipelineBuilder("open")
            .AddCircuitBreaker(o => { o.FailureRatio = 0.5; o.MinimumThroughput = 2; o.SamplingDuration = Long; o.BreakDuration = Long; })
            .Build();

        // Açık devre senaryosu: iki hatayla devreleri aç
        for (var i = 0; i < 2; i++)
        {
            try { _pollyOpen.Execute(static () => throw new InvalidOperationException()); } catch (InvalidOperationException) { }
            try { _aegisOpen.ExecuteAsync<int>(static _ => throw new InvalidOperationException()).AsTask().GetAwaiter().GetResult(); } catch (InvalidOperationException) { }
        }

        _pollyFlaky = _ => (++_pollyCalls & 1) == 1 ? throw new InvalidOperationException() : ValueTask.FromResult(1);
        _aegisFlaky = _ => (++_aegisCalls & 1) == 1 ? throw new InvalidOperationException() : ValueTask.FromResult(1);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var p in new[] { _aegisEmpty, _aegisRetry, _aegisTimeout, _aegisBreaker, _aegisStandard, _aegisLimiter, _aegisOpen, _aegisHedging })
        {
            p.Dispose();
        }
    }

    // ---------------------------------------------------------------- Boş boru hattı
    [Benchmark(Baseline = true), BenchmarkCategory("1-Bos")]
    public ValueTask<int> Polly_Empty() => _pollyEmpty.ExecuteAsync(static _ => ValueTask.FromResult(1), CancellationToken.None);

    [Benchmark, BenchmarkCategory("1-Bos")]
    public ValueTask<int> Aegis_Empty() => _aegisEmpty.ExecuteAsync(static _ => ValueTask.FromResult(1));

    // ---------------------------------------------------------------- Retry (başarı yolu)
    [Benchmark(Baseline = true), BenchmarkCategory("2-Retry")]
    public ValueTask<int> Polly_Retry() => _pollyRetry.ExecuteAsync(static _ => ValueTask.FromResult(1), CancellationToken.None);

    [Benchmark, BenchmarkCategory("2-Retry")]
    public ValueTask<int> Aegis_Retry() => _aegisRetry.ExecuteAsync(static _ => ValueTask.FromResult(1));

    // ---------------------------------------------------------------- Retry (1 hata + 1 başarı)
    [Benchmark(Baseline = true), BenchmarkCategory("3-Retry-1Hata")]
    public ValueTask<int> Polly_RetryOneFailure() => _pollyRetry.ExecuteAsync(_pollyFlaky, CancellationToken.None);

    [Benchmark, BenchmarkCategory("3-Retry-1Hata")]
    public ValueTask<int> Aegis_RetryOneFailure() => _aegisRetry.ExecuteAsync(_aegisFlaky);

    // ---------------------------------------------------------------- Timeout (başarı yolu)
    [Benchmark(Baseline = true), BenchmarkCategory("4-Timeout")]
    public ValueTask<int> Polly_Timeout() => _pollyTimeout.ExecuteAsync(static _ => ValueTask.FromResult(1), CancellationToken.None);

    [Benchmark, BenchmarkCategory("4-Timeout")]
    public ValueTask<int> Aegis_Timeout() => _aegisTimeout.ExecuteAsync(static _ => ValueTask.FromResult(1));

    // ---------------------------------------------------------------- Circuit Breaker (kapalı)
    [Benchmark(Baseline = true), BenchmarkCategory("5-CircuitBreaker")]
    public ValueTask<int> Polly_CircuitBreaker() => _pollyBreaker.ExecuteAsync(static _ => ValueTask.FromResult(1), CancellationToken.None);

    [Benchmark, BenchmarkCategory("5-CircuitBreaker")]
    public ValueTask<int> Aegis_CircuitBreaker() => _aegisBreaker.ExecuteAsync(static _ => ValueTask.FromResult(1));

    // ---------------------------------------------------------------- Açık devre: hızlı red (istisna dahil)
    [Benchmark(Baseline = true), BenchmarkCategory("6-AcikDevre")]
    public async Task<int> Polly_OpenCircuitRejection()
    {
        try { return await _pollyOpen.ExecuteAsync(static _ => ValueTask.FromResult(1), CancellationToken.None); }
        catch (PollyBroken) { return -1; }
    }

    [Benchmark, BenchmarkCategory("6-AcikDevre")]
    public async Task<int> Aegis_OpenCircuitRejection()
    {
        try { return await _aegisOpen.ExecuteAsync(static _ => ValueTask.FromResult(1)); }
        catch (AegisBroken) { return -1; }
    }

    // ---------------------------------------------------------------- Standart zincir (Timeout+Retry+CB+Timeout)
    [Benchmark(Baseline = true), BenchmarkCategory("7-Standart")]
    public ValueTask<int> Polly_Standard() => _pollyStandard.ExecuteAsync(static _ => ValueTask.FromResult(1), CancellationToken.None);

    [Benchmark, BenchmarkCategory("7-Standart")]
    public ValueTask<int> Aegis_Standard() => _aegisStandard.ExecuteAsync(static _ => ValueTask.FromResult(1));

    // ---------------------------------------------------------------- Eşzamanlılık sınırlayıcı
    [Benchmark(Baseline = true), BenchmarkCategory("8-Eszamanlilik")]
    public ValueTask<int> Polly_ConcurrencyLimiter() => _pollyLimiter.ExecuteAsync(static _ => ValueTask.FromResult(1), CancellationToken.None);

    [Benchmark, BenchmarkCategory("8-Eszamanlilik")]
    public ValueTask<int> Aegis_ConcurrencyLimiter() => _aegisLimiter.ExecuteAsync(static _ => ValueTask.FromResult(1));

    // ---------------------------------------------------------------- Hedging (yedek tetiklenmeden)
    [Benchmark(Baseline = true), BenchmarkCategory("9-Hedging")]
    public ValueTask<int> Polly_Hedging() => _pollyHedging.ExecuteAsync(static _ => ValueTask.FromResult(1), CancellationToken.None);

    [Benchmark, BenchmarkCategory("9-Hedging")]
    public ValueTask<int> Aegis_Hedging() => _aegisHedging.ExecuteAsync(static _ => ValueTask.FromResult(1));
}

/// <summary>
/// Polly v8 ile eşdeğer çalıştırma biçimleri (1.0.10): TState, CancellationToken, senkron Execute ve fırlatmayan
/// ExecuteOutcomeAsync. Hepsi standart zincirde (Timeout + Retry + CB + Timeout) ölçülür; Polly referanstır.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ExecutionApiBenchmarks
{
    private ResiliencePipeline _polly = null!, _pollyOutcome = null!;
    private IAegisPipeline _aegis = null!, _aegisOutcome = null!;
    private static readonly FormatException Failure = new("beklenen");

    [GlobalSetup]
    public void Setup()
    {
        var pollyRetry = new RetryStrategyOptions
        {
            MaxRetryAttempts = 3, Delay = TimeSpan.Zero, BackoffType = DelayBackoffType.Constant, UseJitter = false,
            ShouldHandle = new PredicateBuilder().Handle<InvalidOperationException>()
        };
        var pollyBreaker = new CircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5, MinimumThroughput = 100, SamplingDuration = TimeSpan.FromSeconds(30), BreakDuration = TimeSpan.FromSeconds(30)
        };
        _polly = new ResiliencePipelineBuilder()
            .AddTimeout(TimeSpan.FromSeconds(30)).AddRetry(pollyRetry).AddCircuitBreaker(pollyBreaker).AddTimeout(TimeSpan.FromSeconds(10)).Build();
        _pollyOutcome = new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(30)).AddRetry(pollyRetry).Build();

        void AegisRetry(Aegis.Resilience.Core.Strategies.Retry.RetryOptions o)
        {
            o.MaxRetryAttempts = 3; o.Delay = TimeSpan.Zero; o.BackoffType = AegisBackoff.Constant; o.UseJitter = false;
            o.ShouldHandle = ex => ex is InvalidOperationException;
        }

        _aegis = new AegisPipelineBuilder("api").AddTimeout(TimeSpan.FromSeconds(30)).AddRetry(AegisRetry)
            .AddCircuitBreaker(o => { o.FailureRatio = 0.5; o.MinimumThroughput = 100; o.SamplingDuration = TimeSpan.FromSeconds(30); o.BreakDuration = TimeSpan.FromSeconds(30); })
            .AddTimeout(TimeSpan.FromSeconds(10)).Build();
        _aegisOutcome = new AegisPipelineBuilder("api-outcome").AddTimeout(TimeSpan.FromSeconds(30)).AddRetry(AegisRetry).Build();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _aegis.Dispose();
        _aegisOutcome.Dispose();
    }

    // ---------------------------------------------------------------- Async + TState (closure'suz)
    [Benchmark(Baseline = true), BenchmarkCategory("A1-TState")]
    public ValueTask<int> Polly_State() => _polly.ExecuteAsync(static (s, _) => ValueTask.FromResult(s + 1), 41, CancellationToken.None);

    [Benchmark, BenchmarkCategory("A1-TState")]
    public ValueTask<int> Aegis_State() => _aegis.ExecuteAsync(static (_, s) => ValueTask.FromResult(s + 1), 41);

    // ---------------------------------------------------------------- Async + CancellationToken
    [Benchmark(Baseline = true), BenchmarkCategory("A2-Token")]
    public ValueTask<int> Polly_Token() => _polly.ExecuteAsync(static _ => ValueTask.FromResult(1), CancellationToken.None);

    [Benchmark, BenchmarkCategory("A2-Token")]
    public ValueTask<int> Aegis_Token() => _aegis.ExecuteAsync(static _ => ValueTask.FromResult(1), CancellationToken.None);

    // ---------------------------------------------------------------- Senkron Execute
    [Benchmark(Baseline = true), BenchmarkCategory("A3-Senkron")]
    public int Polly_Sync() => _polly.Execute(static () => 1);

    [Benchmark, BenchmarkCategory("A3-Senkron")]
    public int Aegis_Sync() => _aegis.Execute(static () => 1);

    // ---------------------------------------------------------------- Senkron Execute + TState
    [Benchmark(Baseline = true), BenchmarkCategory("A4-Senkron-TState")]
    public int Polly_SyncState() => _polly.Execute(static s => s + 1, 41);

    [Benchmark, BenchmarkCategory("A4-Senkron-TState")]
    public int Aegis_SyncState() => _aegis.Execute(static (_, s) => s + 1, 41);

    // ---------------------------------------------------------------- Fırlatmayan hata yolu (Outcome)
    [Benchmark(Baseline = true), BenchmarkCategory("A5-Outcome-Hata")]
    public async ValueTask<bool> Polly_OutcomeFailure()
    {
        var context = ResilienceContextPool.Shared.Get(CancellationToken.None);
        try
        {
            var outcome = await _pollyOutcome.ExecuteOutcomeAsync(
                static (_, _) => Polly.Outcome.FromExceptionAsValueTask<int>(Failure), context, 0).ConfigureAwait(false);
            return outcome.Exception is null;
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    [Benchmark, BenchmarkCategory("A5-Outcome-Hata")]
    public async ValueTask<bool> Aegis_OutcomeFailure()
    {
        var outcome = await _aegisOutcome.ExecuteOutcomeAsync(
            static (_, _) => ValueTask.FromResult(Aegis.Resilience.Core.Abstractions.Outcome<int>.FromException(Failure)), 0).ConfigureAwait(false);
        return outcome.IsSuccess;
    }
}
