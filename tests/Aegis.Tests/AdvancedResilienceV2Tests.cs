using System.Net;
using System.Net.Http;
using System.Text;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Chaos;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Core.Strategies.Timeout;
using Aegis.Resilience.Extensions.Http;
using Xunit;

namespace Aegis.Tests;

public class AdvancedResilienceV2Tests
{
    // 1. Simmy Chaos Result Injection (Degraded result without exceptions)
    [Fact]
    public async Task ChaosResult_ShouldReturnDegradedResult_WithoutException()
    {
        var pipeline = new AegisPipelineBuilder("ChaosResultPipeline")
            .AddChaos(opt =>
            {
                opt.Enabled = true;
                opt.InjectionRate = 1.0;
                opt.ResultGenerator = _ => "MOCKED_DEGRADED_VALUE";
            })
            .Build();

        // Asıl fonksiyon hata fırlatacak şekilde yazıldı ama Kaos bunu araya girip degraded sonuçla ezer
        var result = await pipeline.ExecuteAsync<string>(_ =>
        {
            throw new InvalidOperationException("Bu istisna hiç fırlatılmamalı!");
        });

        Assert.Equal("MOCKED_DEGRADED_VALUE", result);
    }

    // 2. Simmy Chaos Behavior Injection (Custom side-effect simulation)
    [Fact]
    public async Task ChaosBehavior_ShouldExecuteCustomDelegate()
    {
        var behaviorExecuted = false;

        var pipeline = new AegisPipelineBuilder("ChaosBehaviorPipeline")
            .AddChaos(opt =>
            {
                opt.Enabled = true;
                opt.InjectionRate = 1.0;
                opt.FaultGenerator = null; // İstisna fırlatmasın
                opt.BehaviorGenerator = _ =>
                {
                    behaviorExecuted = true;
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        var result = await pipeline.ExecuteAsync(_ => ValueTask.FromResult("OK"));

        Assert.Equal("OK", result);
        Assert.True(behaviorExecuted);
    }

    // 3. Resilience4j Slow Call Rate Threshold Circuit Breaker
    [Fact]
    public async Task SlowCallRate_ShouldTripCircuit_WhenCallsAreSlowEvenWithoutErrors()
    {
        var pipeline = new AegisPipelineBuilder("SlowCallCBPipeline")
            .AddCircuitBreaker(opt =>
            {
                opt.MinimumThroughput = 2;
                opt.SlowCallDurationThreshold = TimeSpan.FromMilliseconds(40); // 40ms üstü yavaş çağrı
                opt.SlowCallRateThreshold = 0.5; // %50 yavaş çağrıda devreyi aç
                opt.BreakDuration = TimeSpan.FromSeconds(5);
            })
            .Build();

        // 2 istek de HATA VERMİYOR (başarılı) ama yavaş çalışıyor (60ms)
        await pipeline.ExecuteAsync(async _ =>
        {
            await Task.Delay(60);
            return "ok-1";
        });

        await pipeline.ExecuteAsync(async _ =>
        {
            await Task.Delay(60);
            return "ok-2";
        });

        // Hata oranı %0 olmasına rağmen yavaş çağrı oranı %100 (> %50) olduğu için devre açılmış olmalı!
        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
        {
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult("fast"));
        });
    }

    // 4. Dynamic DelayGenerator for Retry (Polly 8.7.0 Parity)
    [Fact]
    public async Task DynamicGenerators_DelayGenerator_ShouldOverrideStaticDelay()
    {
        var attempts = 0;
        var pipeline = new AegisPipelineBuilder("DynamicDelayPipeline")
            .AddRetry(opt =>
            {
                opt.MaxRetryAttempts = 2;
                opt.Delay = TimeSpan.FromSeconds(30); // Statik gecikme 30 saniye
                // Dinamik olarak 5 milisaniyeye çekiyoruz:
                opt.DelayGenerator = ctx => ValueTask.FromResult<TimeSpan?>(TimeSpan.FromMilliseconds(5));
            })
            .Build();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await pipeline.ExecuteAsync(_ =>
        {
            attempts++;
            if (attempts < 2)
            {
                throw new HttpRequestException("Geçici hata");
            }
            return ValueTask.FromResult("Success");
        });
        sw.Stop();

        Assert.Equal("Success", res);
        Assert.Equal(2, attempts);
        // Statik 30 saniye yerine dinamik 5ms uygulandığı için 1 saniyeden çok daha kısa sürede bitmeli
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Dinamik delay çalışmadı, süre: {sw.ElapsedMilliseconds}ms");
    }

    // 5. Dynamic TimeoutGenerator (Deadline Budget Parity)
    [Fact]
    public async Task DynamicGenerators_TimeoutGenerator_ShouldOverrideStaticTimeout()
    {
        var pipeline = new AegisPipelineBuilder("DynamicTimeoutPipeline")
            .AddTimeout(TimeSpan.FromSeconds(30), opt =>
            {
                opt.TimeoutGenerator = ctx => ctx.Properties.TryGetValue("Budget", out var b) ? (TimeSpan)b! : null;
            })
            .Build();

        var ctx = new AegisContext();
        ctx.Properties["Budget"] = TimeSpan.FromMilliseconds(50); // 30 sn yerine 50ms bütçe tanımlandı

        await Assert.ThrowsAsync<AegisTimeoutException>(async () =>
        {
            await pipeline.ExecuteAsync(async c =>
            {
                await Task.Delay(500, c.CancellationToken);
                return "done";
            }, ctx);
        });
    }

    // 6. Segmented Sliding Window Rate Limiter
    [Fact]
    public async Task SlidingWindowRateLimiter_ShouldTrackAcrossSegments()
    {
        var pipeline = new AegisPipelineBuilder("SlidingWindowPipeline")
            .AddSlidingWindowRateLimiter(permitLimit: 2, window: TimeSpan.FromSeconds(1), segmentsPerWindow: 2)
            .Build();

        // 1 ve 2. istek kabul edilir
        var r1 = await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1));
        var r2 = await pipeline.ExecuteAsync(_ => ValueTask.FromResult(2));
        Assert.Equal(1, r1);
        Assert.Equal(2, r2);

        // 3. istek pencere dolduğu için reddedilir
        await Assert.ThrowsAsync<RateLimitRejectedException>(async () =>
        {
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult(3));
        });
    }

    // 7. Automatic HTTP Retry-After Header Parsing
    [Fact]
    public void HttpRetryAfter_ShouldParseDeltaAndHttpDate()
    {
        // 1. Delta-Seconds testi (Örn: Retry-After: 45)
        var parsedDelta = HttpRetryAfterHelper.TryParseRaw("45", out var delayDelta);
        Assert.True(parsedDelta);
        Assert.Equal(TimeSpan.FromSeconds(45), delayDelta);

        // 2. HTTP-Date testi (Gelecekteki bir RFC 1123 tarihi)
        var futureDate = DateTimeOffset.UtcNow.AddMinutes(5).ToString("R");
        var parsedDate = HttpRetryAfterHelper.TryParseRaw(futureDate, out var delayDate);
        Assert.True(parsedDate);
        Assert.True(delayDate > TimeSpan.FromMinutes(4) && delayDate <= TimeSpan.FromMinutes(6));

        // 3. HttpResponseMessage nesnesi ile test
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(15));

        var parsedObj = HttpRetryAfterHelper.TryParse(response, out var delayObj);
        Assert.True(parsedObj);
        Assert.Equal(TimeSpan.FromSeconds(15), delayObj);
    }

    // 8. HTTP Request Replay & Body Buffering
    [Fact]
    public async Task HttpRequestReplay_ShouldAllowMultipleRetriesWithBody()
    {
        using var originalRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.com/v1/orders")
        {
            Content = new StringContent("{\"symbol\":\"THYAO\",\"qty\":100}", Encoding.UTF8, "application/json")
        };
        originalRequest.Headers.Add("X-Test-Header", "AegisCloner");

        // Clone 1
        var clone1 = await HttpRequestReplayHandler.CloneRequestAsync(originalRequest);
        var body1 = await clone1.Content!.ReadAsStringAsync();

        // Clone 2 (orijinal içerik tüketildikten sonra bile yeni klon oluşturulabilmeli)
        var clone2 = await HttpRequestReplayHandler.CloneRequestAsync(originalRequest);
        var body2 = await clone2.Content!.ReadAsStringAsync();

        Assert.Equal(HttpMethod.Post, clone1.Method);
        Assert.Equal(HttpMethod.Post, clone2.Method);
        Assert.Equal("{\"symbol\":\"THYAO\",\"qty\":100}", body1);
        Assert.Equal("{\"symbol\":\"THYAO\",\"qty\":100}", body2);
        Assert.Equal("AegisCloner", clone1.Headers.GetValues("X-Test-Header").First());
        Assert.Equal("AegisCloner", clone2.Headers.GetValues("X-Test-Header").First());
    }
}
