using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Chaos;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Core.Strategies.Retry;

namespace Aegis.Tests;

/// <summary>
/// POLLY PARİTE — Builder (ResiliencePipelineBuilderTests), Telemetry ve Simmy (Chaos) katmanları.
/// </summary>
public class PollyParityBuilderTelemetryTests
{
    // ------------------------------------------------------------------ Builder
    private sealed class OrderStrategy(string name, List<string> log) : IAegisStrategy, IDisposable
    {
        public string Name => name;
        public int Disposed;
        public async ValueTask<T> ExecuteAsync<T>(Func<AegisContext, ValueTask<T>> cb, AegisContext ctx)
        {
            lock (log) log.Add($"{name}:before");
            var r = await cb(ctx);
            lock (log) log.Add($"{name}:after");
            return r;
        }
        public void Dispose() => Disposed++;
    }

    [Fact]
    public void Builder_BuildTwice_OrAddAfterBuild_Throws_PreventsStateSharing()
    {
        var b = new AegisPipelineBuilder("once").AddRetry();
        _ = b.Build();
        Assert.Throws<InvalidOperationException>(() => b.Build());
        Assert.Throws<InvalidOperationException>(() => b.AddRetry());
    }

    [Fact]
    public void Builder_InvalidName_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => new AegisPipelineBuilder(""));
        Assert.ThrowsAny<ArgumentException>(() => new AegisPipelineBuilder("   "));
        Assert.ThrowsAny<ArgumentException>(() => new AegisPipelineBuilder(null!));
    }

    [Fact]
    public async Task Builder_Empty_ExecutesCallbackDirectly()
    {
        using var p = new AegisPipelineBuilder("empty").Build();
        Assert.Equal(42, await p.ExecuteAsync(_ => ValueTask.FromResult(42)));
        Assert.Empty(p.Strategies);
    }

    [Fact]
    public async Task Builder_StrategyOrdering_IsOuterToInner_Nested()
    {
        var log = new List<string>();
        using var p = new AegisPipelineBuilder("order")
            .AddStrategy(new OrderStrategy("A", log))
            .AddStrategy(new OrderStrategy("B", log))
            .AddStrategy(new OrderStrategy("C", log))
            .Build();
        await p.ExecuteAsync(_ => { log.Add("callback"); return ValueTask.FromResult(1); });
        Assert.Equal(new[] { "A:before", "B:before", "C:before", "callback", "C:after", "B:after", "A:after" }, log);
    }

    [Fact]
    public async Task Builder_AddPipeline_Composes_AndDoesNotDisposeInner()
    {
        var log = new List<string>();
        var innerStrategy = new OrderStrategy("inner", log);
        var inner = new AegisPipelineBuilder("inner").AddStrategy(innerStrategy).Build();

        var outerStrategy = new OrderStrategy("outer", log);
        var outer = new AegisPipelineBuilder("outer").AddStrategy(outerStrategy).AddPipeline(inner).Build();

        await outer.ExecuteAsync(_ => { log.Add("cb"); return ValueTask.FromResult(1); });
        Assert.Equal(new[] { "outer:before", "inner:before", "cb", "inner:after", "outer:after" }, log);

        outer.Dispose();
        Assert.Equal(1, outerStrategy.Disposed);
        Assert.Equal(0, innerStrategy.Disposed); // iç boru hattı çağıranın sahipliğinde
        Assert.Equal(1, await inner.ExecuteAsync(_ => ValueTask.FromResult(1))); // hâlâ kullanılabilir
    }

    [Fact]
    public async Task Pipeline_DisposeTwice_Idempotent_AndStrategiesDisposedOnce()
    {
        var log = new List<string>();
        var s = new OrderStrategy("s", log);
        var p = new AegisPipelineBuilder("dispose2").AddStrategy(s).Build();
        await p.ExecuteAsync(_ => ValueTask.FromResult(1));
        p.Dispose(); p.Dispose();
        Assert.Equal(1, s.Disposed);
    }

    // ------------------------------------------------------------------ Telemetry
    private sealed class MeterCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        public readonly ConcurrentBag<(string instrument, double value, string? pipeline)> Records = new();

        public MeterCapture()
        {
            _listener.InstrumentPublished = (inst, l) => { if (inst.Meter.Name == "Aegis") l.EnableMeasurementEvents(inst); };
            _listener.SetMeasurementEventCallback<long>((inst, v, tags, _) => Records.Add((inst.Name, v, Tag(tags))));
            _listener.SetMeasurementEventCallback<double>((inst, v, tags, _) => Records.Add((inst.Name, v, Tag(tags))));
            _listener.Start();
        }

        private static string? Tag(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var t in tags) if (t.Key == "pipeline") return t.Value?.ToString();
            return null;
        }

        public double Sum(string instrument, string pipeline) => Records.Where(r => r.instrument == instrument && r.pipeline == pipeline).Sum(r => r.value);
        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public async Task Telemetry_CountersAndDuration_TaggedWithPipelineName()
    {
        using var capture = new MeterCapture();
        var name = $"telemetry-{Guid.NewGuid():N}";
        using var p = new AegisPipelineBuilder(name)
            .AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.Zero; })
            // 300 ms: eşzamanlılık senaryosundaki tutucu istek (30 ms beklenir) zaman aşımına UĞRAMAMALI. Eskiden 30 ms'ydi;
            // tek çekirdekli Linux konteynerde iki 30 ms'lik süre yarışıp sayaçlar aralıklı kayıyordu.
            .AddTimeout(TimeSpan.FromMilliseconds(300))
            .AddConcurrencyLimiter(1, o => o.QueueTimeout = TimeSpan.Zero)
            .Build();

        // 1) 2 retry sonra başarı
        var n = 0;
        await p.ExecuteAsync(_ => ++n < 3 ? throw new InvalidOperationException() : ValueTask.FromResult(1));
        // 2) zaman aşımı (retry ile 3 kez)
        try { await p.ExecuteAsync(async c => { await Task.Delay(5000, c.CancellationToken); return 1; }); } catch (AegisTimeoutException) { }
        // 3) eşzamanlılık reddi
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = p.ExecuteAsync(async _ => { await gate.Task; return 1; }).AsTask();
        await Task.Delay(30);
        try { await p.ExecuteAsync(_ => ValueTask.FromResult(1)); } catch (RateLimitRejectedException) { }
        gate.SetResult(); await holder;

        await Task.Delay(50);
        var execRecords = capture.Records.Where(r => r.instrument == "aegis.executions.total" && r.pipeline == name).ToList();
        Assert.True(execRecords.Count == 4, "executions kayitlari: " + string.Join(", ", execRecords.Select(r => r.value)) + " (adet " + execRecords.Count + ")");
        Assert.Equal(2 + 2, capture.Sum("aegis.retry.attempts.total", name)); // 2 (başarı) + 2 (zaman aşımı); eşzamanlılık reddi yeniden denenmez (ret hedefe gidilmediği demektir; Microsoft standart işleyicisiyle aynı)
        Assert.Equal(3, capture.Sum("aegis.timeout.total", name));            // 1 ilk + 2 retry
        Assert.True(capture.Sum("aegis.ratelimit.rejections.total", name) >= 1);
        Assert.Equal(4, capture.Records.Count(r => r.instrument == "aegis.execution.duration.ms" && r.pipeline == name));
    }

    [Fact]
    public async Task Telemetry_UserSuppliedContextWithoutName_StrategyMetricsTaggedWithPipelineName()
    {
        // AEGIS-146: adsız kullanıcı bağlamında retry sayacı "default" etiketine gidiyor, executions ise gerçek ada —
        // aynı isteğin metrikleri iki etikete dağılıyordu.
        using var capture = new MeterCapture();
        var name = $"tag-{Guid.NewGuid():N}";
        using var p = new AegisPipelineBuilder(name).AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.Zero; }).Build();
        var n = 0;
        await p.ExecuteAsync(_ => ++n < 2 ? throw new InvalidOperationException() : ValueTask.FromResult(1), new AegisContext(CancellationToken.None));
        await Task.Delay(30);
        Assert.Equal(1, capture.Sum("aegis.retry.attempts.total", name));
        Assert.Equal(0, capture.Sum("aegis.retry.attempts.total", "default"));
    }

    // ------------------------------------------------------------------ Simmy / Chaos
    [Fact]
    public async Task Chaos_ResultGenerator_InjectsResultInsteadOfFault()
    {
        using var p = new AegisPipelineBuilder("chaos-result")
            .AddChaos(o => { o.Enabled = true; o.InjectionRate = 1.0; o.FaultGenerator = null; o.ResultGenerator = _ => "sahte"; })
            .Build();
        var r = await p.ExecuteAsync(_ => ValueTask.FromResult("gerçek"));
        Assert.Equal("sahte", r);
    }

    [Fact]
    public async Task Chaos_ResultGenerator_WrongType_ShouldThrowClear_NotCorrupt()
    {
        using var p = new AegisPipelineBuilder("chaos-badtype")
            .AddChaos(o => { o.Enabled = true; o.InjectionRate = 1.0; o.FaultGenerator = null; o.ResultGenerator = _ => "string"; })
            .Build();
        // int beklenirken string enjekte edilirse: sessizce default(int)=0 DÖNMEMELİ
        var threwOrRanReal = false;
        try
        {
            var r = await p.ExecuteAsync(_ => ValueTask.FromResult(7));
            threwOrRanReal = r == 7; // ya gerçek sonuç
        }
        catch (Exception) { threwOrRanReal = true; } // ya açık hata
        Assert.True(threwOrRanReal, "yanlış tipte kaos sonucu sessizce default değere dönüştü");
    }

    [Fact]
    public async Task Chaos_KillSwitch_ViaOptionsProvider_TogglesAtRuntime()
    {
        var enabled = true;
        using var p = new AegisPipelineBuilder("chaos-kill")
            .AddChaos(o =>
            {
                o.Enabled = true; o.InjectionRate = 1.0;
                o.OptionsProvider = () => new ChaosOptions { Enabled = Volatile.Read(ref enabled), InjectionRate = 1.0 };
            })
            .Build();

        await Assert.ThrowsAsync<ChaosInjectedException>(async () => await p.ExecuteAsync(_ => ValueTask.FromResult(1)));
        Volatile.Write(ref enabled, false);
        Assert.Equal(1, await p.ExecuteAsync(_ => ValueTask.FromResult(1))); // yeniden başlatma olmadan kapandı
    }

    [Fact]
    public async Task Chaos_BehaviorGenerator_RunsBeforeCallback_AndItsExceptionSurfaces()
    {
        var ran = false;
        using var p = new AegisPipelineBuilder("chaos-behavior")
            .AddChaos(o => { o.Enabled = true; o.InjectionRate = 1.0; o.FaultGenerator = null; o.BehaviorGenerator = _ => { ran = true; return ValueTask.CompletedTask; }; })
            .Build();
        Assert.Equal(1, await p.ExecuteAsync(_ => ValueTask.FromResult(1)));
        Assert.True(ran);

        using var p2 = new AegisPipelineBuilder("chaos-behavior-throw")
            .AddChaos(o => { o.Enabled = true; o.InjectionRate = 1.0; o.FaultGenerator = null; o.BehaviorGenerator = _ => throw new ApplicationException("davranış patladı"); })
            .Build();
        await Assert.ThrowsAsync<ApplicationException>(async () => await p2.ExecuteAsync(_ => ValueTask.FromResult(1)));
    }

    [Fact]
    public async Task Chaos_ShouldNeverInject_WhenCallerAlreadyCancelled()
    {
        using var p = new AegisPipelineBuilder("chaos-cancel").AddChaos(o => { o.Enabled = true; o.InjectionRate = 1.0; }).Build();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await p.ExecuteAsync(_ => ValueTask.FromResult(1), new AegisContext(cts.Token)));
    }

    // ------------------------------------------------------------------ Hot reload (Polly ReloadableResiliencePipeline)
    [Fact]
    public async Task HotReload_InvalidDynamicOptions_KeepsLastKnownGood_TrafficNeverBreaks()
    {
        // AEGIS-145: yapılandırma sunucusundan gelen hatalı değer (PermitLimit=0) sessizce yanlış davranışa dönüşmemeli,
        // trafiği de düşürmemeli → son GEÇERLİ seçeneklerle devam (Polly: reload başarısız -> eski pipeline korunur)
        var live = new RateLimiterOptions { PermitLimit = 100, Window = TimeSpan.FromSeconds(10) };
        using var p = new AegisPipelineBuilder("hot-invalid")
            .AddRateLimiter(new RateLimiterOptions { PermitLimit = 100, Window = TimeSpan.FromSeconds(10), OptionsProvider = () => Volatile.Read(ref live) })
            .Build();

        Assert.Equal(1, await p.ExecuteAsync(_ => ValueTask.FromResult(1)));

        Volatile.Write(ref live, new RateLimiterOptions { PermitLimit = 0, Window = TimeSpan.Zero }); // hatalı yayın
        for (var i = 0; i < 20; i++) Assert.Equal(1, await p.ExecuteAsync(_ => ValueTask.FromResult(1))); // trafik akıyor

        Volatile.Write(ref live, new RateLimiterOptions { PermitLimit = 1000, Window = TimeSpan.FromSeconds(10) }); // düzeltilmiş yayın
        Assert.Equal(1, await p.ExecuteAsync(_ => ValueTask.FromResult(1)));
    }

    [Fact]
    public async Task HotReload_ProviderThrows_KeepsLastKnownGood()
    {
        var shouldThrow = false;
        using var p = new AegisPipelineBuilder("hot-throw")
            .AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.Zero; o.OptionsProvider = () => shouldThrow ? throw new ApplicationException("config servisi çöktü") : new RetryOptions { MaxRetryAttempts = 2, Delay = TimeSpan.Zero }; })
            .Build();
        var n = 0;
        Assert.Equal("ok", await p.ExecuteAsync(_ => ++n < 3 ? throw new InvalidOperationException() : ValueTask.FromResult("ok")));
        shouldThrow = true; n = 0;
        Assert.Equal("ok", await p.ExecuteAsync(_ => ++n < 3 ? throw new InvalidOperationException() : ValueTask.FromResult("ok"))); // provider patladı, retry hâlâ 2
    }

    // ------------------------------------------------------------------ RateLimiter (Polly.RateLimiting)
    [Fact]
    public async Task RateLimiter_Rejection_CarriesRetryAfterHint_WhenAvailable()
    {
        using var p = new AegisPipelineBuilder("rl-hint").AddRateLimiter(1, TimeSpan.FromSeconds(30)).Build();
        await p.ExecuteAsync(_ => ValueTask.FromResult(1));
        var ex = await Assert.ThrowsAsync<RateLimitRejectedException>(async () => await p.ExecuteAsync(_ => ValueTask.FromResult(1)));
        Assert.Contains("30", ex.Message); // pencere bilgisi mesajda olmalı ki çağıran Retry-After üretebilsin
    }

    [Fact]
    public async Task RateLimiter_PermitConsumedEvenIfCallbackThrows_NoDoubleRefund()
    {
        using var p = new AegisPipelineBuilder("rl-throw").AddRateLimiter(2, TimeSpan.FromSeconds(30)).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await p.ExecuteAsync<int>(_ => throw new InvalidOperationException()));
        await p.ExecuteAsync(_ => ValueTask.FromResult(1));
        // 2 izin kullanıldı (biri hata ile): 3. reddedilmeli — hata izin iade etmez (Polly lease semantiği)
        await Assert.ThrowsAsync<RateLimitRejectedException>(async () => await p.ExecuteAsync(_ => ValueTask.FromResult(1)));
    }

    [Fact]
    public void RateLimiterStrategies_InsidePipeline_DisposeShouldNotThrow()
    {
        // Token bucket / sliding window / partitioned stratejiler yönetilmeyen kaynak tutmaz; boru hattı dispose'u sorunsuz olmalı
        var p = new AegisPipelineBuilder("rl-dispose")
            .AddRateLimiter(1, TimeSpan.FromSeconds(1))
            .AddSlidingWindowRateLimiter(1, TimeSpan.FromSeconds(1))
            .AddPartitionedRateLimiter(o => o.PartitionKeySelector = ctx => ctx.PipelineName ?? "d")
            .Build();
        p.Dispose(); p.Dispose();
    }
}
