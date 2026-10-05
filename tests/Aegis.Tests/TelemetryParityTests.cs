using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Fallback;
using Aegis.Resilience.Core.Telemetry;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.DependencyInjection.Telemetry;

namespace Aegis.Tests;

/// <summary>
/// Aşama 2 — Polly telemetri eşitliği: olay dinleyicileri (TelemetryListener), standart etiketli metrikler
/// (resilience.polly.* karşılıkları), zenginleştiriciler, önem sağlayıcı, ILogger günlüğü ve DI'da otomatik günlük.
/// Ayrıca Fallback'in sonuç tabanlı yedek + OnFallback yetenekleri.
/// </summary>
public class TelemetryParityTests
{
    private sealed class CaptureListener : AegisTelemetryListener
    {
        public ConcurrentQueue<AegisTelemetryEvent> Events { get; } = new();

        public override void Write(in AegisTelemetryEvent telemetryEvent) => Events.Enqueue(telemetryEvent);
    }

    private sealed class MeterCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        public ConcurrentQueue<(string Instrument, Dictionary<string, object?> Tags)> Records { get; } = new();

        public MeterCapture(string pipeline)
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == AegisTelemetry.MeterName &&
                    instrument.Name is "aegis.strategy.events" or "aegis.strategy.attempt.duration" or "aegis.pipeline.duration")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<int>((i, _, tags, _) => Add(i, tags, pipeline));
            _listener.SetMeasurementEventCallback<double>((i, _, tags, _) => Add(i, tags, pipeline));
            _listener.Start();
        }

        private void Add(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags, string pipeline)
        {
            var dict = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                dict[tag.Key] = tag.Value;
            }

            if (Equals(dict.GetValueOrDefault(AegisTelemetryTags.PipelineName), pipeline))
            {
                Records.Enqueue((instrument.Name, dict));
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public async Task Retry_EmitsExecutionAttemptsAndOnRetry_ToListener()
    {
        var listener = new CaptureListener();
        using var pipeline = new AegisPipelineBuilder("tel-retry")
            .AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.Zero; })
            .WithTelemetry(t => t.Listeners.Add(listener))
            .Build();
        var calls = 0;

        await pipeline.ExecuteAsync(_ => ++calls < 3 ? throw new InvalidOperationException("x") : ValueTask.FromResult(1),
            new AegisContext { OperationKey = "GetOrder" });

        var events = listener.Events.ToList();
        var attempts = events.Where(e => e.EventName == AegisEventNames.ExecutionAttempt).ToList();
        Assert.Equal([0, 1, 2], attempts.Select(a => a.AttemptNumber));
        Assert.Equal([true, true, false], attempts.Select(a => a.Handled!.Value));
        Assert.All(attempts, a => Assert.NotNull(a.Duration));
        Assert.Equal(2, events.Count(e => e.EventName == AegisEventNames.OnRetry));
        Assert.All(events.Where(e => e.StrategyName is not null), e =>
        {
            Assert.Equal("tel-retry", e.PipelineName);
            Assert.Equal("Retry", e.StrategyName);
            Assert.Equal("GetOrder", e.Context.OperationKey);
        });

        var executed = Assert.Single(events, e => e.EventName == AegisEventNames.PipelineExecuted);
        Assert.Null(executed.Exception);
        Assert.NotNull(executed.Duration);
    }

    [Fact]
    public async Task StandardMetrics_CarryPollyCompatibleTags_AndEnrichers()
    {
        var name = $"tel-metrics-{Guid.NewGuid():N}";
        using var capture = new MeterCapture(name);
        using var pipeline = new AegisPipelineBuilder(name)
            .AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.Zero; })
            .WithTelemetry(t => t.MeteringEnrichers.Add(c => c.Tags.Add(new("tenant", "acme"))))
            .Build();
        var calls = 0;

        await pipeline.ExecuteAsync(_ => ++calls == 1 ? throw new TimeoutException() : ValueTask.FromResult(1),
            new AegisContext { OperationKey = "op-1" });

        var records = capture.Records.ToList();
        var retryEvent = Assert.Single(records, r => r.Instrument == "aegis.strategy.events");
        Assert.Equal(AegisEventNames.OnRetry, retryEvent.Tags[AegisTelemetryTags.EventName]);
        Assert.Equal("Warning", retryEvent.Tags[AegisTelemetryTags.EventSeverity]);
        Assert.Equal("Retry", retryEvent.Tags[AegisTelemetryTags.StrategyName]);
        Assert.Equal("op-1", retryEvent.Tags[AegisTelemetryTags.OperationKey]);
        Assert.Equal(typeof(TimeoutException).FullName, retryEvent.Tags[AegisTelemetryTags.ExceptionType]);
        Assert.Equal("acme", retryEvent.Tags["tenant"]);

        var attempts = records.Where(r => r.Instrument == "aegis.strategy.attempt.duration").ToList();
        Assert.Equal(2, attempts.Count);
        Assert.Equal(0, attempts[0].Tags[AegisTelemetryTags.AttemptNumber]);
        Assert.Equal(true, attempts[0].Tags[AegisTelemetryTags.AttemptHandled]);

        Assert.Single(records, r => r.Instrument == "aegis.pipeline.duration");
    }

    [Fact]
    public async Task SeverityProvider_OverridesSeverity_AndThrowingListenerNeverBreaksCall()
    {
        var listener = new CaptureListener();
        using var pipeline = new AegisPipelineBuilder("tel-severity")
            .AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.Zero; })
            .WithTelemetry(t =>
            {
                t.Listeners.Add(new ThrowingListener());
                t.Listeners.Add(listener);
                t.SeverityProvider = e => e.EventName == AegisEventNames.OnRetry ? AegisEventSeverity.Critical : e.Severity;
            })
            .Build();
        var calls = 0;

        Assert.Equal(2, await pipeline.ExecuteAsync(_ => ++calls == 1 ? throw new InvalidOperationException() : ValueTask.FromResult(calls)));
        Assert.Equal(AegisEventSeverity.Critical, Assert.Single(listener.Events, e => e.EventName == AegisEventNames.OnRetry).Severity);
    }

    private sealed class ThrowingListener : AegisTelemetryListener
    {
        public override void Write(in AegisTelemetryEvent telemetryEvent) => throw new InvalidOperationException("listener patladı");
    }

    [Fact]
    public async Task Strategies_ReportTheirEvents()
    {
        var listener = new CaptureListener();
        using var pipeline = new AegisPipelineBuilder("tel-all")
            .AddFallback(o => o.FallbackHandler = (_, _) => ValueTask.FromResult<object?>(-1))
            .AddCircuitBreaker(o => { o.MinimumThroughput = 2; o.FailureRatio = 0.5; o.BreakDuration = TimeSpan.FromMinutes(1); })
            .AddTimeout(TimeSpan.FromMilliseconds(20))
            .WithTelemetry(t => t.Listeners.Add(listener))
            .Build();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(-1, await pipeline.ExecuteAsync(async ctx => { await Task.Delay(1000, ctx.CancellationToken); return 1; }));
        }

        var names = listener.Events.Select(e => e.EventName).ToList();
        Assert.Contains(AegisEventNames.OnTimeout, names);
        Assert.Contains(AegisEventNames.OnCircuitOpened, names);
        Assert.Contains(AegisEventNames.OnFallback, names);
        Assert.Equal(AegisEventSeverity.Error, listener.Events.First(e => e.EventName == AegisEventNames.OnCircuitOpened).Severity);
    }

    [Fact]
    public async Task RateLimiterRejection_IsReported()
    {
        var listener = new CaptureListener();
        using var pipeline = new AegisPipelineBuilder("tel-limit")
            .AddConcurrencyLimiter(1, o => o.QueueTimeout = TimeSpan.Zero)
            .WithTelemetry(t => t.Listeners.Add(listener))
            .Build();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var holder = pipeline.ExecuteAsync(async _ => { await gate.Task; return 1; }).AsTask();
        await Assert.ThrowsAsync<RateLimitRejectedException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));
        gate.SetResult();
        await holder;

        Assert.Contains(listener.Events, e => e.EventName == AegisEventNames.OnRateLimiterRejected && e.StrategyName == "ConcurrencyLimiter");
    }

    // ---------------------------------------------------------------- ILogger

    private sealed class ListLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, EventId EventId, string Message, Exception? Exception)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new ListLogger(categoryName, this);

        public void Dispose()
        {
        }

        private sealed class ListLogger(string category, ListLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Entries.Enqueue((category, logLevel, eventId, formatter(state, exception), exception));
        }
    }

    [Fact]
    public async Task LoggingListener_WritesPollyStyleMessages()
    {
        var provider = new ListLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));
        using var pipeline = new AegisPipelineBuilder("log-retry")
            .AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.Zero; })
            .WithTelemetry(t => t.Listeners.Add(new AegisLoggingTelemetryListener(factory)))
            .Build();
        var calls = 0;

        await pipeline.ExecuteAsync(_ => ++calls == 1 ? throw new InvalidOperationException("boom") : ValueTask.FromResult(42),
            new AegisContext { OperationKey = "op" });

        var entries = provider.Entries.Where(e => e.Category == AegisLoggingTelemetryListener.CategoryName).ToList();
        var retry = Assert.Single(entries, e => e.Message.Contains("EventName: 'OnRetry'"));
        Assert.Equal(LogLevel.Warning, retry.Level);
        Assert.Contains("Pipeline: 'log-retry'", retry.Message);
        Assert.Contains("OperationKey: 'op'", retry.Message);
        Assert.Contains("Result: 'boom'", retry.Message);
        Assert.IsType<InvalidOperationException>(retry.Exception);

        var attempts = entries.Where(e => e.EventId.Id == 3).ToList();
        Assert.Equal([LogLevel.Warning, LogLevel.Debug], attempts.Select(a => a.Level));
        Assert.Contains("Result: '42'", attempts[1].Message);
        Assert.Single(entries, e => e.EventId.Id == 2 && e.Message.Contains("Resilience pipeline executed"));
    }

    [Fact]
    public async Task DependencyInjection_AttachesLoggerAutomatically_AndCanBeDisabled()
    {
        async Task<int> RunAsync(bool disable)
        {
            var provider = new ListLoggerProvider();
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));
            services.AddAegisPipeline("di-log", b => b.AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.Zero; }));
            if (disable)
            {
                services.ConfigureAegisTelemetry(t => t.EnableLogging = false);
            }

            await using var sp = services.BuildServiceProvider();
            var pipeline = sp.GetRequiredService<IAegisPipelineRegistry>().GetPipeline("di-log");
            var calls = 0;
            await pipeline.ExecuteAsync(_ => ++calls == 1 ? throw new InvalidOperationException() : ValueTask.FromResult(1));
            return provider.Entries.Count(e => e.Category == AegisLoggingTelemetryListener.CategoryName);
        }

        Assert.True(await RunAsync(disable: false) > 0);
        Assert.Equal(0, await RunAsync(disable: true));
    }

    [Fact]
    public async Task NoListeners_SuccessPath_StillAllocatesNothing()
    {
        using var pipeline = new AegisPipelineBuilder("tel-zero")
            .AddTimeout(TimeSpan.FromSeconds(30))
            .AddRetry(o => { o.MaxRetryAttempts = 3; o.Delay = TimeSpan.Zero; })
            .AddCircuitBreaker(o => o.MinimumThroughput = 100)
            .Build();
        static ValueTask<int> Work(AegisContext _) => ValueTask.FromResult(1);

        for (var i = 0; i < 2_000; i++)
        {
            await pipeline.ExecuteAsync(Work);
        }

        const int calls = 10_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < calls; i++)
        {
            _ = SynchronousResult.Of(pipeline.ExecuteAsync(Work));
        }

        var perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)calls;
        Assert.True(perCall < 8, $"Telemetri dinleyicisi yokken çağrı başına {perCall:F1} bayt ayrıldı");
    }

    // ---------------------------------------------------------------- Fallback (Aşama 3 kalemleri)

    [Fact]
    public async Task Fallback_ResultBased_WithFallbackActionAndOnFallback()
    {
        FallbackArguments? notified = null;
        using var pipeline = new AegisPipelineBuilder("fb-result")
            .AddFallback(o =>
            {
                o.ShouldHandleResult = r => r is 503;
                o.FallbackAction = a => ValueTask.FromResult<object?>(a.Result is 503 ? 200 : -1);
                o.OnFallback = a => { notified = a; return ValueTask.CompletedTask; };
            })
            .Build();

        Assert.Equal(200, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(503)));
        Assert.Equal(201, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(201))); // ele alınmayan sonuç aynen döner
        Assert.Equal(503, notified!.Result);
        Assert.Null(notified.Exception);
    }

    [Fact]
    public async Task Fallback_PredicateBuilder_HandlesExceptionAndResult_OldHandlerStillWorks()
    {
        using var pipeline = new AegisPipelineBuilder("fb-pred")
            .AddFallback(o =>
            {
                o.ShouldHandleOutcome = new AegisPredicateBuilder().Handle<TimeoutException>().HandleResult<string>(s => s.Length == 0);
                o.FallbackAction = a => ValueTask.FromResult<object?>(a.Exception is not null ? "timeout-yedek" : "bos-yedek");
            })
            .Build();

        Assert.Equal("timeout-yedek", await pipeline.ExecuteAsync<string>(_ => throw new TimeoutException()));
        Assert.Equal("bos-yedek", await pipeline.ExecuteAsync(_ => ValueTask.FromResult("")));
        await Assert.ThrowsAsync<FormatException>(async () => await pipeline.ExecuteAsync<string>(_ => throw new FormatException()));

        using var legacy = new AegisPipelineBuilder("fb-legacy")
            .AddFallback(o => o.FallbackHandler = (_, ex) => ValueTask.FromResult<object?>(ex.Message))
            .Build();
        Assert.Equal("eski", await legacy.ExecuteAsync<string>(_ => throw new InvalidOperationException("eski")));
    }

    [Fact]
    public async Task Fallback_ResultBasedWithoutAction_ReportsConfigurationError()
    {
        using var pipeline = new AegisPipelineBuilder("fb-misconfig")
            .AddFallback(o => { o.ShouldHandleResult = r => r is 0; o.FallbackHandler = (_, _) => ValueTask.FromResult<object?>(1); })
            .Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(0)));
        Assert.Contains("FallbackAction", ex.Message);
    }
}
