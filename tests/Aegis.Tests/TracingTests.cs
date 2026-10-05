using System.Collections.Concurrent;
using System.Diagnostics;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Tests;

/// <summary>
/// İz (trace) desteği (1.5.0): boru hattı yürütmesi başına bir span, strateji olayları span olayı, çağıranın
/// <c>Activity.Current</c>'ı korunur. <c>ActivitySource</c> süreç geneli olduğundan her test benzersiz boru hattı adı kullanır ve
/// dinleyici yalnızca o span adını örnekler: paralel çalışan diğer testler birbirini görmez.
/// </summary>
public sealed class TracingTests : IDisposable
{
    private static readonly ActivitySource OuterSource = new("Aegis.Tests.Outer");
    private static readonly ActivityListener OuterListener = new()
    {
        ShouldListenTo = source => source.Name == "Aegis.Tests.Outer",
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
    };

    private readonly string _name = "trace-" + Guid.NewGuid().ToString("N");
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private readonly ActivityListener _listener;

    static TracingTests() => ActivitySource.AddActivityListener(OuterListener);

    public TracingTests() => _listener = Listen(ActivitySamplingResult.AllDataAndRecorded);

    private ActivityListener Listen(ActivitySamplingResult result)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AegisTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                options.Name == "Aegis " + _name ? result : ActivitySamplingResult.None,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "Aegis " + _name)
                {
                    _stopped.Enqueue(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    public void Dispose() => _listener.Dispose();

    private AegisPipelineBuilder Builder() => new(_name);

    [Fact]
    public void SourceName_IsAegis_SameAsMeter() => Assert.Equal(AegisTelemetry.MeterName, AegisTelemetry.ActivitySourceName);

    [Fact]
    public async Task Execution_ProducesOneSpan_WithPipelineAndOperationKeyTags()
    {
        using var pipeline = Builder().WithInstanceName("tenant-9").AddTimeout(TimeSpan.FromSeconds(5)).Build();
        var context = new AegisContext(CancellationToken.None, _name) { OperationKey = "siparis-olustur" };

        Assert.Equal(7, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(7), context));

        var span = Assert.Single(_stopped);
        Assert.Equal("Aegis " + _name, span.DisplayName);
        Assert.Equal(ActivityKind.Internal, span.Kind);
        Assert.Equal(_name, span.GetTagItem(AegisTelemetryTags.PipelineName));
        Assert.Equal("tenant-9", span.GetTagItem(AegisTelemetryTags.PipelineInstance));
        Assert.Equal("siparis-olustur", span.GetTagItem(AegisTelemetryTags.OperationKey));
        Assert.Equal(ActivityStatusCode.Unset, span.Status);
        Assert.True(span.IsStopped);
    }

    [Fact]
    public async Task Failure_SetsErrorStatus_AndExceptionType_WithoutMessage()
    {
        using var pipeline = Builder().AddTimeout(TimeSpan.FromSeconds(5)).Build();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException("kart numarası 4111-1111")));

        var span = Assert.Single(_stopped);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, span.GetTagItem(AegisTelemetryTags.ExceptionType));
        Assert.Null(span.StatusDescription);                                           // iletide hassas veri olabilir: yazılmaz
        Assert.DoesNotContain(span.TagObjects, t => t.Value?.ToString()?.Contains("4111", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task StrategyEvents_AreRecordedAsSpanEvents()
    {
        using var pipeline = Builder().AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.Zero; o.UseJitter = false; }).Build();
        var calls = 0;

        await pipeline.ExecuteAsync(_ => ++calls < 3 ? throw new InvalidOperationException() : ValueTask.FromResult(1));

        var span = Assert.Single(_stopped);
        var names = span.Events.Select(e => e.Name).ToArray();
        Assert.Equal(2, names.Count(n => n == AegisEventNames.OnRetry));
        Assert.Contains(AegisEventNames.ExecutionAttempt, names);
        Assert.DoesNotContain(AegisEventNames.PipelineExecuted, names);   // span zaten bunu temsil eder
        Assert.DoesNotContain(AegisEventNames.PipelineExecuting, names);

        var attempt = span.Events.First(e => e.Name == AegisEventNames.ExecutionAttempt);
        Assert.Contains(attempt.Tags, t => t.Key == AegisTelemetryTags.AttemptNumber);
        Assert.Contains(attempt.Tags, t => t.Key == AegisTelemetryTags.StrategyName);
    }

    [Fact]
    public async Task ChildSpans_StartedInsideTheCallback_AreNestedUnderThePipelineSpan()
    {
        using var pipeline = Builder().AddTimeout(TimeSpan.FromSeconds(5)).Build();
        string? childParentName = null;
        using var childListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Aegis.Tests.Outer",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };

        await pipeline.ExecuteAsync(async _ =>
        {
            await Task.Yield();                                  // eşzamansız sınır: Activity.Current akışı korunmalı
            using var child = OuterSource.StartActivity("http-cagrisi");
            childParentName = child?.Parent?.OperationName;
            return 1;
        });

        Assert.Equal("Aegis " + _name, childParentName);
    }

    [Fact]
    public async Task CallersActivity_IsRestored_EvenWhenTheCallIsStillRunning()
    {
        using var pipeline = Builder().AddTimeout(TimeSpan.FromSeconds(5)).Build();
        using var outer = OuterSource.StartActivity("istek");
        Assert.NotNull(outer);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = pipeline.ExecuteAsync(async _ => { await gate.Task; return 1; });

        // Çağrı sürüyor ama çağıranın bağlamına Aegis span'ı sızmamalı (metot async olmadığı için StartActivity sızdırırdı).
        Assert.Same(outer, Activity.Current);
        Assert.Empty(_stopped);

        gate.SetResult();
        await running;

        Assert.Same(outer, Activity.Current);
        var span = Assert.Single(_stopped);
        Assert.Equal(outer.Id, span.ParentId);   // span çağıranın span'ının çocuğu
    }

    [Fact]
    public void CallersActivity_IsRestored_AfterSynchronousCompletion()
    {
        using var pipeline = Builder().AddTimeout(TimeSpan.FromSeconds(5)).Build();
        using var outer = OuterSource.StartActivity("istek");

        Assert.Equal(1, pipeline.Execute(() => 1));

        Assert.Same(outer, Activity.Current);
        Assert.Single(_stopped);
    }

    [Fact]
    public async Task NotSampled_CreatesNoSpan_AndTheCallStillWorks()
    {
        _listener.Dispose();
        using var none = Listen(ActivitySamplingResult.None);
        using var pipeline = Builder().AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.Zero; }).Build();
        var calls = 0;
        Activity? seenInside = null;

        var result = await pipeline.ExecuteAsync(_ =>
        {
            seenInside = Activity.Current;
            return ++calls < 2 ? throw new InvalidOperationException() : ValueTask.FromResult(5);
        });

        Assert.Equal(5, result);
        Assert.Empty(_stopped);
        Assert.Null(seenInside);
    }

    [Fact]
    public async Task ConcurrentCalls_ProduceDistinctSpans_AllStopped()
    {
        using var pipeline = Builder().AddTimeout(TimeSpan.FromSeconds(5)).Build();

        await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(async () =>
        {
            for (var n = 0; n < 20; n++)
            {
                if (i % 4 == 0)
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                        await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()));
                }
                else
                {
                    await pipeline.ExecuteAsync(async _ => { await Task.Yield(); return n; });
                }
            }
        })));

        Assert.Equal(64 * 20, _stopped.Count);
        Assert.Equal(64 * 20, _stopped.Select(a => a.Id).Distinct().Count());
        Assert.All(_stopped, a => Assert.True(a.IsStopped));
        Assert.Equal(16 * 20, _stopped.Count(a => a.Status == ActivityStatusCode.Error));
    }

    [Fact]
    public async Task PipelineWithoutAnyListener_DoesNotStartSpans()
    {
        _listener.Dispose();   // bu testin kendi dinleyicisi yok; başka testlerin dinleyicileri yalnızca kendi span adlarını örnekler
        using var pipeline = Builder().AddTimeout(TimeSpan.FromSeconds(5)).Build();
        Activity? seenInside = null;

        await pipeline.ExecuteAsync(_ => { seenInside = Activity.Current; return ValueTask.FromResult(1); });

        Assert.Null(seenInside);
        Assert.Empty(_stopped);
    }
}
