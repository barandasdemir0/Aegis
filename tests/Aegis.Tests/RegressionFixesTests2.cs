using System.Diagnostics;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Core.Strategies.Retry;

namespace Aegis.Tests;

/// <summary>
/// Kullanıcı denetiminde eski örnek proje (ExampleProject, kaldırıldı) üzerinden canlı olarak gözlemlenen iki ek düzeltmenin regresyon testleri.
/// </summary>
public class RegressionFixesTests2
{
    // ---------------------------------------------------------------------
    // AEGIS-122a: Retry, devre AÇIKKEN BrokenCircuitException'ı tekrar denememeli
    // ---------------------------------------------------------------------
    [Fact]
    public async Task Retry_ShouldNotRetry_BrokenCircuitException()
    {
        IAegisPipelineBuilder builder = new AegisPipelineBuilder("retry-cb-pipeline");
        var pipeline = builder
            .AddRetry(opt => { opt.MaxRetryAttempts = 5; opt.Delay = TimeSpan.FromMilliseconds(200); })
            .AddCircuitBreaker(opt =>
            {
                opt.MinimumThroughput = 1;
                opt.FailureRatio = 1.0;
                opt.BreakDuration = TimeSpan.FromSeconds(30);
            })
            .Build();

        // 1. Devreyi aç (MinimumThroughput=1 olduğundan ilk hata devreyi hemen açar;
        //    Retry stratejisi bunu farkedip BrokenCircuitException ile bitirebilir — önemli olan devrenin açılmasıdır)
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await pipeline.ExecuteAsync<string>(_ => throw new HttpRequestException("downstream çöktü")));

        // 2. Devre açıkken tekrar çağır: Retry'ın BrokenCircuitException'ı 5 kez (200ms aralıkla)
        //    denememesi, çağrının ANINDA (< 100ms) dönmesi gerekir. Eski davranışta bu ~1sn sürerdi.
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult("should-not-run")));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 100,
            $"BrokenCircuitException retry edilmemeliydi; süre: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Retry_WithCustomShouldHandle_StillSkipsBrokenCircuitException()
    {
        // Kullanıcı ShouldHandle = ex => true yazsa bile BrokenCircuitException retry edilmemelidir.
        var attempts = 0;
        var options = new RetryOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.Zero,
            ShouldHandle = _ => true
        };

        var strategy = new RetryStrategy(options);
        var context = new AegisContext();

        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
            await strategy.ExecuteAsync<string>(_ =>
            {
                attempts++;
                throw new BrokenCircuitException("devre açık");
            }, context));

        Assert.Equal(1, attempts); // Retry denemedi
    }

    // ---------------------------------------------------------------------
    // AEGIS-122b: AdaptiveConcurrency, düşük RTT'lerdeki normal jitter'ı yavaşlama sanmamalı
    // ---------------------------------------------------------------------
    [Fact]
    public async Task AdaptiveConcurrency_LowRttJitter_ShouldNotShrinkLimit()
    {
        var strategy = new AdaptiveConcurrencyStrategy(new AdaptiveConcurrencyOptions
        {
            InitialConcurrency = 10,
            MinConcurrency = 2,
            MaxConcurrency = 50,
            SmoothingFactor = 0.2
        });

        var pipeline = new AegisPipelineBuilder("AdaptiveJitterPipeline")
            .AddStrategy(strategy)
            .Build();

        // Gerçekçi düşük-RTT jitter: 8-14ms arasında dalgalanan "hızlı" çağrılar.
        // NOT: Bu örnekler algoritmaya DOĞRUDAN beslenir. Gerçek Task.Delay ile ölçüm yapılmaz, çünkü
        // Windows'ta Task.Delay TickCount64 (~15.6ms çözünürlük) tabanlıdır: 12ms istenen bekleme 6.8ms'de
        // erken dönebilir ya da 15.6ms'de geç dönebilir; timer çözünürlüğünün ALTINDAKİ gecikmelerle
        // yapılan zaman ölçümü gürültüden ibarettir ve testi dalgalı (flaky) yapar.
        var pattern = new[] { 8, 12, 9, 14, 8, 11, 13, 9, 10, 12, 8, 14, 9, 11, 10, 8, 13, 9, 12, 10 };
        foreach (var ms in pattern)
        {
            strategy.RecordSampleForTesting(ms);
        }

        // Düzeltmeden önce: sabit 5ms mutlak eşik bu jitter'ı "yavaşlama" sanıp limiti
        // agresif şekilde MinConcurrency'ye (2) kadar düşürüyordu.
        // Düzeltmeden sonra: orantılı tolerans (minRtt'nin %50'si) normal jitter'ı emer,
        // limit başlangıç seviyesinin makul bir yakınında kalmalı veya büyümelidir.
        Assert.True(strategy.CurrentLimit > 5,
            $"Düşük RTT jitter'ı limiti gereksiz yere daraltmamalı; mevcut limit: {strategy.CurrentLimit}");
    }
}
