using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Pipeline;
using AegisBackoff = Aegis.Resilience.Core.Strategies.Retry.DelayBackoffType;

/// <summary>
/// Eşzamanlı yük: 64 paralel işçi AYNI boru hattı örneğini paylaşır (sunucudaki gerçek durum). Tek iş parçacıklı
/// ölçümlerde görünmeyen kilit / atomik işlem çekişmesi burada ortaya çıkar. Süre, çağrı başına ortalama (OperationsPerInvoke);
/// Polly referanstır. Makine: 28 mantıksal çekirdek.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ConcurrentLoadBenchmarks
{
    private const int Workers = 64;
    private const int OpsPerWorker = 2_000;
    private const int TotalOps = Workers * OpsPerWorker;

    private ResiliencePipeline _pollyStandard = null!, _pollyBreaker = null!, _pollyLimiter = null!, _pollyRetry = null!;
    private IAegisPipeline _aegisStandard = null!, _aegisBreaker = null!, _aegisLimiter = null!, _aegisRetry = null!;

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

        _pollyStandard = new ResiliencePipelineBuilder()
            .AddTimeout(TimeSpan.FromSeconds(30)).AddRetry(pollyRetry).AddCircuitBreaker(pollyBreaker).AddTimeout(TimeSpan.FromSeconds(10)).Build();
        _pollyBreaker = new ResiliencePipelineBuilder().AddCircuitBreaker(pollyBreaker).Build();
        _pollyLimiter = new ResiliencePipelineBuilder().AddConcurrencyLimiter(1_000, 0).Build();
        _pollyRetry = new ResiliencePipelineBuilder().AddRetry(pollyRetry).Build();

        void AegisRetry(Aegis.Resilience.Core.Strategies.Retry.RetryOptions o)
        {
            o.MaxRetryAttempts = 3; o.Delay = TimeSpan.Zero; o.BackoffType = AegisBackoff.Constant; o.UseJitter = false;
            o.ShouldHandle = ex => ex is InvalidOperationException;
        }

        void AegisBreaker(Aegis.Resilience.Core.Strategies.CircuitBreaker.CircuitBreakerOptions o)
        {
            o.FailureRatio = 0.5; o.MinimumThroughput = 100; o.SamplingDuration = TimeSpan.FromSeconds(30); o.BreakDuration = TimeSpan.FromSeconds(30);
        }

        _aegisStandard = new AegisPipelineBuilder("c-standard")
            .AddTimeout(TimeSpan.FromSeconds(30)).AddRetry(AegisRetry).AddCircuitBreaker(AegisBreaker).AddTimeout(TimeSpan.FromSeconds(10)).Build();
        _aegisBreaker = new AegisPipelineBuilder("c-breaker").AddCircuitBreaker(AegisBreaker).Build();
        _aegisLimiter = new AegisPipelineBuilder("c-limiter").AddConcurrencyLimiter(1_000, o => o.QueueTimeout = TimeSpan.Zero).Build();
        _aegisRetry = new AegisPipelineBuilder("c-retry").AddRetry(AegisRetry).Build();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _aegisStandard.Dispose();
        _aegisBreaker.Dispose();
        _aegisLimiter.Dispose();
        _aegisRetry.Dispose();
    }

    private static readonly Func<CancellationToken, ValueTask<int>> PollySync = static _ => ValueTask.FromResult(1);
    private static readonly Func<AegisContext, ValueTask<int>> AegisSync = static _ => ValueTask.FromResult(1);

    private static readonly Func<CancellationToken, ValueTask<int>> PollyAsync = static async _ =>
    {
        await Task.Yield();
        return 1;
    };

    private static readonly Func<AegisContext, ValueTask<int>> AegisAsync = static async _ =>
    {
        await Task.Yield();
        return 1;
    };

    /// <summary><see cref="Workers"/> işçiyi aynı anda başlatır; her biri <see cref="OpsPerWorker"/> çağrı yapar.</summary>
    private static Task RunAsync(Func<ValueTask<int>> operation)
    {
        var workers = new Task[Workers];
        for (var i = 0; i < Workers; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                for (var j = 0; j < OpsPerWorker; j++)
                {
                    await operation().ConfigureAwait(false);
                }
            });
        }

        return Task.WhenAll(workers);
    }

    // ---------------------------------------------------------------- Standart zincir, senkron geri çağrı
    [Benchmark(Baseline = true, OperationsPerInvoke = TotalOps), BenchmarkCategory("C1-Standart-Senkron")]
    public Task Polly_Standard_Sync() => RunAsync(() => _pollyStandard.ExecuteAsync(PollySync, CancellationToken.None));

    [Benchmark(OperationsPerInvoke = TotalOps), BenchmarkCategory("C1-Standart-Senkron")]
    public Task Aegis_Standard_Sync() => RunAsync(() => _aegisStandard.ExecuteAsync(AegisSync));

    // ---------------------------------------------------------------- Standart zincir, gerçekten asenkron geri çağrı
    [Benchmark(Baseline = true, OperationsPerInvoke = TotalOps), BenchmarkCategory("C2-Standart-Async")]
    public Task Polly_Standard_Async() => RunAsync(() => _pollyStandard.ExecuteAsync(PollyAsync, CancellationToken.None));

    [Benchmark(OperationsPerInvoke = TotalOps), BenchmarkCategory("C2-Standart-Async")]
    public Task Aegis_Standard_Async() => RunAsync(() => _aegisStandard.ExecuteAsync(AegisAsync));

    // ---------------------------------------------------------------- Devre kesici (kilit çekişmesi)
    [Benchmark(Baseline = true, OperationsPerInvoke = TotalOps), BenchmarkCategory("C3-CircuitBreaker")]
    public Task Polly_Breaker() => RunAsync(() => _pollyBreaker.ExecuteAsync(PollySync, CancellationToken.None));

    [Benchmark(OperationsPerInvoke = TotalOps), BenchmarkCategory("C3-CircuitBreaker")]
    public Task Aegis_Breaker() => RunAsync(() => _aegisBreaker.ExecuteAsync(AegisSync));

    // ---------------------------------------------------------------- Eşzamanlılık sınırlayıcı (atomik çekişme)
    [Benchmark(Baseline = true, OperationsPerInvoke = TotalOps), BenchmarkCategory("C4-Eszamanlilik")]
    public Task Polly_Limiter() => RunAsync(() => _pollyLimiter.ExecuteAsync(PollySync, CancellationToken.None));

    [Benchmark(OperationsPerInvoke = TotalOps), BenchmarkCategory("C4-Eszamanlilik")]
    public Task Aegis_Limiter() => RunAsync(() => _aegisLimiter.ExecuteAsync(AegisSync));

    // ---------------------------------------------------------------- Retry (başarı yolu)
    [Benchmark(Baseline = true, OperationsPerInvoke = TotalOps), BenchmarkCategory("C5-Retry")]
    public Task Polly_Retry() => RunAsync(() => _pollyRetry.ExecuteAsync(PollySync, CancellationToken.None));

    [Benchmark(OperationsPerInvoke = TotalOps), BenchmarkCategory("C5-Retry")]
    public Task Aegis_Retry() => RunAsync(() => _aegisRetry.ExecuteAsync(AegisSync));
}
