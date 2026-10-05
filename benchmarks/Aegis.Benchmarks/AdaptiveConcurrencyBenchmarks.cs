using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Polly;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Pipeline;

/// <summary>
/// Adaptive Concurrency (Netflix Gradient2) maliyeti. Polly'de adaptif sınırlayıcı yoktur; bu yüzden referans Polly'nin
/// sabit eşzamanlılık sınırlayıcısıdır (Ratio = Aegis / Polly). Aegis'in sabit sınırlayıcısı da eklenir: adaptif sürümün
/// sabite göre ek maliyeti (RTT ölçümü + kilit altında gradyan hesabı) doğrudan görünür.
/// Sınırlar hiç reddetmeyecek kadar yüksektir; ölçülen şey kütüphanenin başarı yolundaki kendi ek yüküdür.
/// Tek iş parçacığı ve 64 paralel işçi (adaptif strateji çağrı başına bir kilit alır; çekişme burada görünür).
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class AdaptiveConcurrencyBenchmarks
{
    private const int Workers = 64;
    private const int OpsPerWorker = 2_000;
    private const int TotalOps = Workers * OpsPerWorker;

    private static readonly Func<CancellationToken, ValueTask<int>> PollyWork = static _ => ValueTask.FromResult(1);
    private static readonly Func<AegisContext, ValueTask<int>> AegisWork = static _ => ValueTask.FromResult(1);

    private ResiliencePipeline _pollyLimiter = null!;
    private IAegisPipeline _aegisFixed = null!, _aegisAdaptive = null!;

    [GlobalSetup]
    public void Setup()
    {
        _pollyLimiter = new ResiliencePipelineBuilder().AddConcurrencyLimiter(1_000, 0).Build();
        _aegisFixed = new AegisPipelineBuilder("adaptive-fixed")
            .AddConcurrencyLimiter(1_000, o => o.QueueTimeout = TimeSpan.Zero).Build();
        _aegisAdaptive = new AegisPipelineBuilder("adaptive")
            .AddAdaptiveConcurrency(o =>
            {
                o.MinConcurrency = 5;
                o.InitialConcurrency = 1_000;
                o.MaxConcurrency = 1_000;
                o.QueueTimeout = TimeSpan.Zero;
            }).Build();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _aegisFixed.Dispose();
        _aegisAdaptive.Dispose();
    }

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

    // ---------------------------------------------------------------- Tek iş parçacığı
    [Benchmark(Baseline = true), BenchmarkCategory("D1-Tek-Is-Parcacigi")]
    public ValueTask<int> Polly_Limiter_Single() => _pollyLimiter.ExecuteAsync(PollyWork, CancellationToken.None);

    [Benchmark, BenchmarkCategory("D1-Tek-Is-Parcacigi")]
    public ValueTask<int> Aegis_FixedLimiter_Single() => _aegisFixed.ExecuteAsync(AegisWork);

    [Benchmark, BenchmarkCategory("D1-Tek-Is-Parcacigi")]
    public ValueTask<int> Aegis_Adaptive_Single() => _aegisAdaptive.ExecuteAsync(AegisWork);

    // ---------------------------------------------------------------- 64 paralel işçi, aynı boru hattı
    [Benchmark(Baseline = true, OperationsPerInvoke = TotalOps), BenchmarkCategory("D2-64-Is-Parcacigi")]
    public Task Polly_Limiter_Concurrent() => RunAsync(() => _pollyLimiter.ExecuteAsync(PollyWork, CancellationToken.None));

    [Benchmark(OperationsPerInvoke = TotalOps), BenchmarkCategory("D2-64-Is-Parcacigi")]
    public Task Aegis_FixedLimiter_Concurrent() => RunAsync(() => _aegisFixed.ExecuteAsync(AegisWork));

    [Benchmark(OperationsPerInvoke = TotalOps), BenchmarkCategory("D2-64-Is-Parcacigi")]
    public Task Aegis_Adaptive_Concurrent() => RunAsync(() => _aegisAdaptive.ExecuteAsync(AegisWork));
}
