using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Telemetry;

/// <summary>
/// İz (trace) maliyeti: aynı standart zincir (retry + devre kesici + zaman aşımı) üç durumda. Polly'de iz yoktur; referans
/// Aegis'in dinleyicisiz hâlidir (Ratio = durum / dinleyicisiz). Dinleyicisiz durumda iz kodunun maliyeti tek bir
/// <c>ActivitySource.HasListeners()</c> denetimidir; "örneklenmiyor" dinleyici bağlı ama span istemez (OpenTelemetry'de
/// örnekleme oranı 0); "kayıt" her çağrıda span + etiket + olaylar üretir.
/// Her benchmark ayrı süreçte koşar; dinleyici yalnızca ilgili benchmark'ın kurulumunda eklenir.
/// </summary>
[MemoryDiagnoser]
public class TracingBenchmarks
{
    private static readonly Func<AegisContext, ValueTask<int>> Work = static _ => ValueTask.FromResult(1);

    private IAegisPipeline _pipeline = null!;
    private ActivityListener? _listener;

    private void Build() => _pipeline = new AegisPipelineBuilder("tracing-bench")
        .AddRetry(o => { o.MaxRetryAttempts = 3; o.Delay = TimeSpan.Zero; o.UseJitter = false; })
        .AddCircuitBreaker(o => { o.FailureRatio = 0.5; o.MinimumThroughput = 100; o.SamplingDuration = TimeSpan.FromSeconds(30); })
        .AddTimeout(TimeSpan.FromSeconds(10))
        .Build();

    private void Listen(ActivitySamplingResult result)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AegisTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => result
        };
        ActivitySource.AddActivityListener(_listener);
    }

    [GlobalSetup(Target = nameof(Standard_NoListener))]
    public void SetupNoListener() => Build();

    [GlobalSetup(Target = nameof(Standard_ListenerNotSampled))]
    public void SetupNotSampled()
    {
        Build();
        Listen(ActivitySamplingResult.None);
    }

    [GlobalSetup(Target = nameof(Standard_ListenerRecording))]
    public void SetupRecording()
    {
        Build();
        Listen(ActivitySamplingResult.AllDataAndRecorded);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _listener?.Dispose();
        _pipeline.Dispose();
    }

    [Benchmark(Baseline = true)]
    public ValueTask<int> Standard_NoListener() => _pipeline.ExecuteAsync(Work);

    [Benchmark]
    public ValueTask<int> Standard_ListenerNotSampled() => _pipeline.ExecuteAsync(Work);

    [Benchmark]
    public ValueTask<int> Standard_ListenerRecording() => _pipeline.ExecuteAsync(Work);
}
