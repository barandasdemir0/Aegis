using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Time.Testing;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Chaos;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Core.Telemetry;
using Aegis.Resilience.Distributed.Abstractions;
using Aegis.Resilience.RateLimiting;

namespace Aegis.Tests;

/// <summary>
/// Polly 8.5.2 → 8.8.0 arasında eklenen genel API ve davranışların Aegis karşılıkları: devre reddinde RetryAfter ve
/// IsolatedCircuitException, HalfOpenAttempts, elle geçiş olayları (IsManual), olayda tetikleyen sonuç, kaos üreticilerinin
/// null/boş davranışı ve LatencyGenerator, boru hattı örnek adı, PipelineExecuting olayı, TelemetrySource, Outcome
/// yardımcıları, boş boru hattı ve hız sınırı red meta verisi.
/// </summary>
public class Polly88ParityTests
{
    private sealed class CaptureListener : AegisTelemetryListener
    {
        public ConcurrentQueue<AegisTelemetryEvent> Events { get; } = new();

        public override void Write(in AegisTelemetryEvent telemetryEvent) => Events.Enqueue(telemetryEvent);
    }

    private static async Task FailAsync(IAegisPipeline pipeline, int times)
    {
        for (var i = 0; i < times; i++)
        {
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException("boom")));
        }
    }

    [Fact]
    public async Task OpenCircuit_Rejection_CarriesRetryAfterAndTelemetrySource()
    {
        var clock = new FakeTimeProvider();
        using var pipeline = new AegisPipelineBuilder("cb-retry-after")
            .AddCircuitBreaker(o => { o.MinimumThroughput = 2; o.FailureRatio = 0.5; o.BreakDuration = TimeSpan.FromSeconds(30); })
            .WithTimeProvider(clock)
            .Build();

        await FailAsync(pipeline, 2);
        clock.Advance(TimeSpan.FromSeconds(10));

        var rejected = await Assert.ThrowsAsync<BrokenCircuitException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));
        Assert.Equal(TimeSpan.FromSeconds(20), rejected.RetryAfter);
        Assert.Equal("cb-retry-after", rejected.TelemetrySource?.PipelineName);
        Assert.Equal("CircuitBreaker", rejected.TelemetrySource?.StrategyName);
    }

    [Fact]
    public async Task IsolatedCircuit_ThrowsIsolatedCircuitException_CatchableAsBroken()
    {
        var control = new CircuitBreakerManualControl(isIsolated: true);
        using var pipeline = new AegisPipelineBuilder("cb-isolated").AddCircuitBreaker(o => o.ManualControl = control).Build();

        var ex = await Assert.ThrowsAsync<IsolatedCircuitException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));
        Assert.IsAssignableFrom<BrokenCircuitException>(ex);
        Assert.Null(ex.RetryAfter);
    }

    [Fact]
    public async Task HalfOpenAttempts_GrowOnFailedProbes_AndResetOnClose()
    {
        var clock = new FakeTimeProvider();
        var seen = new List<int>();
        using var pipeline = new AegisPipelineBuilder("cb-half-open")
            .AddCircuitBreaker(o =>
            {
                o.MinimumThroughput = 2;
                o.FailureRatio = 0.5;
                o.BreakDuration = TimeSpan.FromSeconds(1);
                o.BreakDurationGenerator = e =>
                {
                    seen.Add(e.HalfOpenAttempts);
                    return TimeSpan.FromSeconds(1 << e.HalfOpenAttempts); // üstel geri çekilme
                };
            })
            .WithTimeProvider(clock)
            .Build();

        await FailAsync(pipeline, 2);          // Closed → Open (0)
        clock.Advance(TimeSpan.FromSeconds(1));
        await FailAsync(pipeline, 1);          // HalfOpen deneme başarısız → Open (1), süre 2 sn
        clock.Advance(TimeSpan.FromSeconds(2));
        await FailAsync(pipeline, 1);          // → Open (2), süre 4 sn
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(1, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1))); // deneme başarılı → Closed
        await FailAsync(pipeline, 2);          // yeniden açılış sayaç sıfırdan başlar

        Assert.Equal([0, 1, 2, 0], seen);
    }

    [Fact]
    public async Task CircuitEvents_CarryTriggeringOutcome_AndManualFlag()
    {
        var opened = new ConcurrentQueue<CircuitBreakerEventContext>();
        var closed = new ConcurrentQueue<CircuitBreakerEventContext>();
        var control = new CircuitBreakerManualControl();
        using var pipeline = new AegisPipelineBuilder("cb-events")
            .AddCircuitBreaker(o =>
            {
                o.MinimumThroughput = 2;
                o.FailureRatio = 0.5;
                o.ManualControl = control;
                o.OnOpened = e => { opened.Enqueue(e); return default; };
                o.OnClosed = e => { closed.Enqueue(e); return default; };
            })
            .Build();

        await FailAsync(pipeline, 2);
        var automatic = Assert.Single(opened);
        Assert.False(automatic.IsManual);
        Assert.Equal("boom", Assert.IsType<InvalidOperationException>(automatic.Exception).Message);

        await control.IsolateAsync();
        await control.CloseAsync();
        await control.CloseAsync(); // durum değişmedi → olay yok

        Assert.Equal(2, opened.Count);
        Assert.True(opened.Last().IsManual);
        Assert.Equal(CircuitState.Isolated, opened.Last().NewState);
        var manualClose = Assert.Single(closed);
        Assert.True(manualClose.IsManual);
        Assert.Equal(CircuitState.Isolated, manualClose.OldState);
    }

    [Fact]
    public async Task CircuitEvents_ResultBasedFailure_CarriesResult()
    {
        CircuitBreakerEventContext? opened = null;
        using var pipeline = new AegisPipelineBuilder("cb-result")
            .AddCircuitBreaker(o =>
            {
                o.MinimumThroughput = 2;
                o.FailureRatio = 0.5;
                o.ShouldHandleResult = r => r is -1;
                o.OnOpened = e => { opened = e; return default; };
            })
            .Build();

        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(-1));
        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(-1));

        Assert.NotNull(opened);
        Assert.Equal(-1, opened!.Result);
        Assert.Null(opened.Exception);
    }

    [Fact]
    public async Task DistributedCircuit_IsolatedAndManualEvents_MatchLocalCircuit()
    {
        var events = new ConcurrentQueue<CircuitBreakerEventContext>();
        var control = new CircuitBreakerManualControl();
        using var pipeline = new AegisPipelineBuilder("dcb-manual")
            .AddStrategy(new DistributedCircuitBreakerStrategy(new InMemoryCircuitBreakerStateStore(), new DistributedCircuitBreakerOptions
            {
                ManualControl = control,
                OnOpened = e => { events.Enqueue(e); return default; },
                OnClosed = e => { events.Enqueue(e); return default; }
            }))
            .Build();
        Assert.Equal(1, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1))); // anahtar (boru hattı adı) öğrenilir

        await control.IsolateAsync();
        var ex = await Assert.ThrowsAsync<IsolatedCircuitException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));
        Assert.Equal("dcb-manual", ex.TelemetrySource?.PipelineName);
        await control.CloseAsync();

        Assert.Collection(events,
            e => { Assert.True(e.IsManual); Assert.Equal(CircuitState.Isolated, e.NewState); },
            e => { Assert.True(e.IsManual); Assert.Equal(CircuitState.Closed, e.NewState); });
    }

    [Fact]
    public async Task DistributedCircuit_FailedProbe_ReportsHalfOpenAttemptsAndException()
    {
        var seen = new ConcurrentQueue<CircuitBreakerEventContext>();
        using var pipeline = new AegisPipelineBuilder("dcb-probe")
            .AddStrategy(new DistributedCircuitBreakerStrategy(new InMemoryCircuitBreakerStateStore(), new DistributedCircuitBreakerOptions
            {
                MinimumThroughput = 2,
                FailureRatio = 0.5,
                BreakDuration = TimeSpan.FromMilliseconds(50),
                StateCacheDuration = TimeSpan.Zero,
                OnOpened = e => { seen.Enqueue(e); return default; }
            }))
            .Build();

        await FailAsync(pipeline, 2);
        await Task.Delay(150);
        await FailAsync(pipeline, 1); // HalfOpen denemesi başarısız

        Assert.Equal([0, 1], seen.Select(e => e.HalfOpenAttempts));
        Assert.All(seen, e => Assert.IsType<InvalidOperationException>(e.Exception));
    }

    [Fact]
    public async Task Chaos_FaultGeneratorReturningNull_InjectsNothing()
    {
        using var pipeline = new AegisPipelineBuilder("chaos-null")
            .AddChaos(o => { o.Enabled = true; o.InjectionRate = 1; o.FaultGenerator = () => null!; })
            .Build();

        Assert.Equal(7, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(7)));
    }

    [Fact]
    public async Task Chaos_EmptyOutcomeGenerator_PassesThrough()
    {
        using var pipeline = new AegisPipelineBuilder("chaos-empty")
            .AddChaos(o => { o.Enabled = true; o.InjectionRate = 1; o.OutcomeGenerator = new ChaosOutcomeGenerator(); })
            .Build();

        Assert.Equal(7, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(7)));
    }

    [Fact]
    public async Task Chaos_LatencyGenerator_OverridesFixedLatency()
    {
        var clock = new FakeTimeProvider();
        var calls = 0;
        using var pipeline = new AegisPipelineBuilder("chaos-latency")
            .AddChaos(o =>
            {
                o.Enabled = true;
                o.InjectionRate = 1;
                o.FaultGenerator = null;
                o.Latency = TimeSpan.FromHours(1);
                o.LatencyGenerator = _ => ValueTask.FromResult(Interlocked.Increment(ref calls) == 1 ? TimeSpan.FromSeconds(5) : TimeSpan.Zero);
            })
            .WithTimeProvider(clock)
            .Build();

        var delayed = pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)).AsTask();
        Assert.False(delayed.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, await delayed);

        Assert.Equal(2, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(2))); // sıfır → gecikme yok
    }

    [Fact]
    public async Task InstanceName_FlowsToEventsAndMetricTag_AndPipelineExecutingIsReported()
    {
        var listener = new CaptureListener();
        var instanceTags = new ConcurrentQueue<object?>();
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AegisTelemetry.MeterName && instrument.Name == "aegis.pipeline.duration")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        meter.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            var isOurs = false;
            object? instance = null;
            foreach (var tag in tags)
            {
                isOurs |= tag is { Key: AegisTelemetryTags.PipelineName, Value: "instance-pipeline" };
                instance = tag.Key == AegisTelemetryTags.PipelineInstance ? tag.Value : instance;
            }

            if (isOurs)
            {
                instanceTags.Enqueue(instance);
            }
        });
        meter.Start();

        using var pipeline = (AegisPipeline)new AegisPipelineBuilder("instance-pipeline")
            .WithInstanceName("tenant-42")
            .WithTelemetry(t => t.Listeners.Add(listener))
            .AddTimeout(TimeSpan.FromSeconds(5))
            .Build();

        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1));

        Assert.Equal("tenant-42", pipeline.InstanceName);
        var names = listener.Events.Select(e => e.EventName).ToArray();
        Assert.Equal(AegisEventNames.PipelineExecuting, names[0]);
        Assert.Equal(AegisEventNames.PipelineExecuted, names[^1]);
        Assert.All(listener.Events, e => Assert.Equal("tenant-42", e.PipelineInstance));
        Assert.Equal("tenant-42", Assert.Single(instanceTags)); // PipelineExecuting metrik yazmaz (Polly ile aynı)
    }

    [Fact]
    public async Task TimeoutRejection_CarriesTelemetrySource()
    {
        using var pipeline = new AegisPipelineBuilder("timeout-source").WithInstanceName("i-1").AddTimeout(TimeSpan.FromMilliseconds(20)).Build();

        var ex = await Assert.ThrowsAsync<AegisTimeoutException>(async () =>
            await pipeline.ExecuteAsync(async ctx => { await Task.Delay(Timeout.Infinite, ctx.CancellationToken); return 1; }));

        Assert.Equal("timeout-source", ex.TelemetrySource?.PipelineName);
        Assert.Equal("i-1", ex.TelemetrySource?.PipelineInstanceName);
        Assert.Equal("Timeout", ex.TelemetrySource?.StrategyName);
    }

    [Fact]
    public async Task RateLimiterBridge_Rejection_ExposesLeaseMetadata()
    {
        RateLimiterRejectedArguments? rejected = null;
        using var limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 1,
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0
        });
        using var pipeline = new AegisPipelineBuilder("bridge-metadata")
            .AddRateLimiter(limiter, o => o.OnRejected = a => { rejected = a; return default; })
            .Build();

        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1));
        var ex = await Assert.ThrowsAsync<RateLimitRejectedException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));

        Assert.NotNull(ex.RetryAfter);
        Assert.Equal("bridge-metadata", ex.TelemetrySource?.PipelineName);
        Assert.Contains(rejected!.Metadata!, m => m.Key == MetadataName.RetryAfter.Name);
    }

    [Fact]
    public async Task Hedging_ResultPredicate_SeesRealAttemptNumbers()
    {
        var seen = new ConcurrentQueue<int>();
        var calls = 0;
        using var pipeline = new AegisPipelineBuilder("hedge-attempts")
            .AddHedging(o =>
            {
                o.MaxHedgedAttempts = 2;
                o.HedgingDelay = Timeout.InfiniteTimeSpan; // ardışık: deneme numaraları belirlenimci
                o.ShouldHandleOutcome = AegisPredicate.Create(a => { seen.Enqueue(a.AttemptNumber); return a.Result is -1; });
            })
            .Build();

        var result = await pipeline.ExecuteAsync(_ => ValueTask.FromResult(Interlocked.Increment(ref calls) < 3 ? -1 : 42));

        Assert.Equal(42, result);
        Assert.Equal([0, 1, 2], seen);
    }

    [Fact]
    public async Task Hedging_UnhandledException_EndsHedgingImmediately()
    {
        var calls = 0;
        using var pipeline = new AegisPipelineBuilder("hedge-unhandled")
            .AddHedging(o =>
            {
                o.MaxHedgedAttempts = 2;
                o.HedgingDelay = Timeout.InfiniteTimeSpan;
                o.ShouldHandle = ex => ex is TimeoutException;
            })
            .Build();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await pipeline.ExecuteAsync<int>(_ => { Interlocked.Increment(ref calls); throw new ArgumentException("kalıcı hata"); }));
        Assert.Equal(1, calls);

        calls = 0;
        Assert.Equal(7, await pipeline.ExecuteAsync(_ => Interlocked.Increment(ref calls) == 1
            ? throw new TimeoutException()
            : ValueTask.FromResult(7)));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task OutcomeHelpers_And_EmptyPipeline()
    {
        Assert.Equal(5, Outcome.FromResult(5).GetResultOrThrow());
        Outcome.FromResult("ok").ThrowIfException();
        Assert.Throws<TimeoutException>(() => Outcome.FromException<int>(new TimeoutException()).ThrowIfException());
        Assert.Equal(3, (await Outcome.FromResultAsValueTask(3)).Result);
        Assert.IsType<InvalidOperationException>((await Outcome.FromExceptionAsValueTask<int>(new InvalidOperationException())).Exception);

        Assert.Same(AegisPipeline.Empty, AegisPipeline.Empty);
        Assert.Empty(AegisPipeline.Empty.Strategies);
        Assert.Equal(9, await AegisPipeline.Empty.ExecuteAsync(_ => ValueTask.FromResult(9)));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await AegisPipeline.Empty.ExecuteAsync<int>(_ => throw new InvalidOperationException()));
    }
}
