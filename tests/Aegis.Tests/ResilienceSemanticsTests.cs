using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.Hedging;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Core.Strategies.Timeout;
using Aegis.Resilience.Extensions.Http;

namespace Aegis.Tests;

/// <summary>
/// Dayanıklılık anlamları: Polly belgeleri (strateji başına "anti-patterns"), Microsoft.Extensions.Http.Resilience issue'ları
/// ve topluluk kaynaklarındaki doğru davranış. Her test, hangi kurala dayandığını adıyla söyler.
/// </summary>
public class ResilienceSemanticsTests
{
    private static async Task<int> RetryCalls(Exception toThrow)
    {
        var calls = 0;
        var pipeline = new AegisPipelineBuilder("r").AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.Zero; }).Build();
        try
        {
            await pipeline.ExecuteAsync<int>(_ => { calls++; throw toThrow; }, CancellationToken.None);
        }
        catch (Exception)
        {
            // yalnızca çağrı sayısı ölçülür
        }

        return calls;
    }

    // Polly retry.md: varsayılan ShouldHandle "OperationCanceledException dışındaki her istisna".
    [Fact]
    public async Task Retry_NeverRetries_Cancellation()
    {
        Assert.Equal(1, await RetryCalls(new OperationCanceledException()));
        Assert.Equal(1, await RetryCalls(new TaskCanceledException()));
    }

    // Retry + açık devre / hız sınırı: ret hedefe hiç gidilmediği anlamına gelir; hemen yeniden denemek dönen bir döngüdür
    // (Microsoft standart işleyicisi retleri yeniden denemez; "retry stops before MaxRetryAttempts" topluluk vakası).
    [Fact]
    public async Task Retry_NeverRetries_Rejections()
    {
        Assert.Equal(1, await RetryCalls(new BrokenCircuitException("açık")));
        Assert.Equal(1, await RetryCalls(new IsolatedCircuitException("izole")));
        Assert.Equal(1, await RetryCalls(new RateLimitRejectedException("kota")));
    }

    // Microsoft standart işleyicisi: deneme zaman aşımı (TimeoutRejectedException) geçici hatadır ve yeniden denenir.
    [Fact]
    public async Task Retry_Retries_AttemptTimeout() =>
        Assert.Equal(3, await RetryCalls(new AegisTimeoutException("deneme zaman aşımı", TimeSpan.FromSeconds(1))));

    // Polly retry.md: "Attempt: '0' relates to the original execution attempt"; OnRetry'deki numara 0'dan başlar.
    [Fact]
    public async Task Retry_AttemptNumber_IsZeroBased_LikePolly()
    {
        var numbers = new List<int>();
        var pipeline = new AegisPipelineBuilder("n")
            .AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.Zero; o.OnRetry = a => { numbers.Add(a.AttemptNumber); return default; }; })
            .Build();

        await Assert.ThrowsAsync<IOException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new IOException(), CancellationToken.None));

        Assert.Equal([0, 1], numbers);
    }

    // Gecikme hesabı numaralandırmadan etkilenmez: üstel 100 ms → 100, 200.
    [Fact]
    public async Task Retry_ExponentialDelay_Unchanged_By_Numbering()
    {
        var delays = new List<TimeSpan>();
        var pipeline = new AegisPipelineBuilder("d")
            .AddRetry(o =>
            {
                o.MaxRetryAttempts = 2; o.Delay = TimeSpan.FromMilliseconds(1); o.BackoffType = DelayBackoffType.Exponential; o.UseJitter = false;
                o.OnRetry = a => { delays.Add(a.RetryDelay); return default; };
            })
            .Build();

        await Assert.ThrowsAsync<IOException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new IOException(), CancellationToken.None));

        Assert.Equal([TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2)], delays);
    }

    private static async Task<CircuitState> StateAfterFailures(Func<CancellationToken, Exception> failure, bool callerCancels)
    {
        var state = new CircuitBreakerStateProvider();
        var pipeline = new AegisPipelineBuilder("cb")
            .AddCircuitBreaker(o => { o.MinimumThroughput = 2; o.FailureRatio = 0.5; o.StateProvider = state; })
            .Build();
        for (var i = 0; i < 3; i++)
        {
            using var cts = new CancellationTokenSource();
            if (callerCancels)
            {
                cts.Cancel();
            }

            try
            {
                await pipeline.ExecuteAsync<int>(_ => throw failure(cts.Token), new AegisContext(cts.Token));
            }
            catch (Exception)
            {
                // yalnızca devre durumu ölçülür
            }
        }

        return state.CircuitState;
    }

    // HttpClient.Timeout → TaskCanceledException (çağıran iptal etmedi): bağımlılık yanıt vermiyor; devre AÇILMALI.
    // Polly bunu yok sayar ve devre takılan servise karşı hiç açılmaz (Stack Overflow / Microsoft Q&A'da bilinen tuzak).
    [Fact]
    public async Task CircuitBreaker_Counts_Cancellation_NotRequestedByCaller()
    {
        var state = new CircuitBreakerStateProvider();
        var pipeline = new AegisPipelineBuilder("cb")
            .AddCircuitBreaker(o => { o.MinimumThroughput = 2; o.FailureRatio = 0.5; o.StateProvider = state; })
            .Build();
        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<TaskCanceledException>(async () =>
                await pipeline.ExecuteAsync<int>(_ => throw new TaskCanceledException("HttpClient.Timeout"), CancellationToken.None));
        }

        Assert.Equal(CircuitState.Open, state.CircuitState);
    }

    // Çağıranın kendi iptali hedef hakkında bilgi taşımaz: devre açılmaz (Polly ile aynı).
    [Fact]
    public async Task CircuitBreaker_Ignores_CallerCancellation() =>
        Assert.Equal(CircuitState.Closed, await StateAfterFailures(token => new OperationCanceledException(token), callerCancels: true));

    // Açık devre / hız sınırı retleri devreyi besleyemez (iç içe devrelerde zincirleme açılma olmaz).
    [Fact]
    public async Task CircuitBreaker_Ignores_Rejections()
    {
        Assert.Equal(CircuitState.Closed, await StateAfterFailures(_ => new BrokenCircuitException("iç devre"), callerCancels: false));
        Assert.Equal(CircuitState.Closed, await StateAfterFailures(_ => new RateLimitRejectedException("kota"), callerCancels: false));
    }

    // Polly circuit-breaker.md: "rethrows all exceptions, including those that are handled".
    [Fact]
    public async Task CircuitBreaker_Rethrows_HandledExceptions()
    {
        var pipeline = new AegisPipelineBuilder("cb").AddCircuitBreaker(_ => { }).Build();
        await Assert.ThrowsAsync<IOException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new IOException(), CancellationToken.None));
    }

    // Polly ve Microsoft varsayılanları birbiriyle aynıdır; Aegis de aynı (az trafikte tek tük hata devreyi açmaz).
    [Fact]
    public void Defaults_Match_Polly_And_Microsoft()
    {
        var breaker = new CircuitBreakerOptions();
        Assert.Equal(0.1, breaker.FailureRatio);
        Assert.Equal(100, breaker.MinimumThroughput);
        Assert.Equal(TimeSpan.FromSeconds(30), breaker.SamplingDuration);
        Assert.Equal(TimeSpan.FromSeconds(5), breaker.BreakDuration);
        Assert.Equal(TimeSpan.FromSeconds(30), new TimeoutOptions().Timeout);
        Assert.Equal(TimeSpan.FromSeconds(2), new HedgingOptions().HedgingDelay);
        Assert.Equal(1, new HedgingOptions().MaxHedgedAttempts);

        // Retry: Microsoft standart işleyicisi gibi 3 deneme, 2 sn taban, üstel + jitter (Polly varsayılanı sabit/jittersiz;
        // Polly belgeleri ve AWS "Exponential Backoff and Jitter" üstel + jitter önerir).
        var retry = new RetryOptions();
        Assert.Equal(3, retry.MaxRetryAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), retry.Delay);
        Assert.Equal(DelayBackoffType.Exponential, retry.BackoffType);
        Assert.True(retry.UseJitter);
    }

    // startdebugging.net / Microsoft belgeleri: yeniden denemede her deneme iç işleyiciden geçer. İç işleyici başlık
    // ekliyorsa başlık denemeler arasında ÇOĞALMAMALI (Microsoft aynı isteği yeniden gönderdiği için orada çoğalır).
    [Fact]
    public async Task Http_InnerHandlerHeaders_DoNotAccumulate_AcrossRetries()
    {
        var seen = new List<int>();
        var services = new ServiceCollection();
        services.AddHttpClient("x")
            .ConfigurePrimaryHttpMessageHandler(() => new CountingServer(seen, failures: 2))
            .AddStandardAegisHandler(o => o.Retry.Delay = TimeSpan.FromMilliseconds(1))
            .AddHttpMessageHandler(() => new SigningHandler());
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("x");

        using var response = await client.GetAsync(new Uri("http://x/"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([1, 1, 1], seen);
    }

    // dotnet/extensions ve "Aspire varsayılan işleyicisini geçersiz kılma" yazıları: ikinci standart işleyici sessizce yığılır
    // (16 fiziksel çağrı). Aegis bunu istemci oluşturulurken açık hatayla durdurur.
    [Fact]
    public void Http_StackedStandardHandlers_FailFast()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("y").AddStandardAegisHandler(_ => { }).AddStandardAegisHandler(_ => { });
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IHttpClientFactory>().CreateClient("y"));
        Assert.Contains("RemoveAllAegisHandlers", error.Message);
    }

    // Doğru geçersiz kılma yolu: önce mevcut işleyicileri kaldır, sonra ekle (Microsoft: RemoveAllResilienceHandlers).
    [Fact]
    public void Http_OverridingStandardHandler_AfterRemoveAll_Works()
    {
        var services = new ServiceCollection();
        services.ConfigureHttpClientDefaults(http => http.AddStandardAegisHandler(_ => { }));
        services.AddHttpClient("z").RemoveAllAegisHandlers().AddStandardAegisHandler(o => o.Retry.MaxRetryAttempts = 1);
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IHttpClientFactory>().CreateClient("z"));
    }

    // gRPC A6 retry throttling / Envoy retry_budget: hata oranı yükselince yeniden denemeler durur, sağlık dönünce açılır.
    [Fact]
    public async Task RetryBudget_StopsRetryStorm_AndRecovers()
    {
        var budget = new RetryBudget(maxTokens: 4, tokenRatio: 1);
        var calls = 0;
        var pipeline = new AegisPipelineBuilder("b").AddRetry(o => { o.MaxRetryAttempts = 3; o.Delay = TimeSpan.Zero; o.Budget = budget; }).Build();

        // İlk çağrı: 4 jeton → 1 hata (3) → yeniden deneme yok (3 > 2? evet var) ... jeton yarıya (2) inince durur.
        await Assert.ThrowsAsync<IOException>(async () => await pipeline.ExecuteAsync<int>(_ => { calls++; throw new IOException(); }, CancellationToken.None));
        Assert.Equal(2, calls); // 4→3 (dene), 3→2 (yarıda: durdu)
        Assert.False(budget.CanRetry);

        // Bütçe tükenmişken yeni hata: tek çağrı, yeniden deneme yok (fırtına yok).
        calls = 0;
        await Assert.ThrowsAsync<IOException>(async () => await pipeline.ExecuteAsync<int>(_ => { calls++; throw new IOException(); }, CancellationToken.None));
        Assert.Equal(1, calls);

        // Başarılar bütçeyi doldurur; yeniden deneme geri gelir.
        for (var i = 0; i < 4; i++)
        {
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1), CancellationToken.None);
        }

        Assert.True(budget.CanRetry);
    }

    // Bütçe yokken davranış Polly ve Microsoft ile aynı (sınırsız; yalnızca MaxRetryAttempts).
    [Fact]
    public void RetryBudget_IsOptional_DefaultUnlimited() =>
        Assert.Null(new RetryOptions().Budget);

    // Ele alınmayan hata ve iptal bütçeyi tüketmez (hedefin sağlığı hakkında bilgi taşımaz).
    [Fact]
    public async Task RetryBudget_IgnoresUnhandledFailures()
    {
        var budget = new RetryBudget(maxTokens: 2, tokenRatio: 1);
        var pipeline = new AegisPipelineBuilder("b")
            .AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.Zero; o.Budget = budget; o.ShouldHandle = e => e is IOException; })
            .Build();

        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new ArgumentException(), CancellationToken.None));
        }

        Assert.Equal(2, budget.Tokens);
    }

    // resilience4j maxWaitDurationInHalfOpenState: hiç bitmeyen deneme isteği devreyi sonsuza dek HalfOpen'da tutmamalı.
    [Fact]
    public async Task CircuitBreaker_StuckProbe_IsReplaced_AfterBreakDuration()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var pipeline = new AegisPipelineBuilder("probe")
            .AddCircuitBreaker(o => { o.MinimumThroughput = 1; o.FailureRatio = 1; o.BreakDuration = TimeSpan.FromSeconds(10); })
            .WithTimeProvider(clock)
            .Build();
        await Assert.ThrowsAsync<IOException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new IOException(), CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(10));

        var never = new TaskCompletionSource<int>();
        var stuckProbe = pipeline.ExecuteAsync(async _ => await never.Task, CancellationToken.None).AsTask(); // takılan deneme
        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1), CancellationToken.None));

        clock.Advance(TimeSpan.FromSeconds(10)); // bir açık kalma süresi geçti
        Assert.Equal(7, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(7), CancellationToken.None)); // yeni deneme geçer, devre kapanır
        Assert.Equal(8, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(8), CancellationToken.None));

        never.SetResult(0); // eski deneme sonunda bitse de kapalı devreyi bozmaz
        await stuckProbe;
        Assert.Equal(9, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(9), CancellationToken.None));
    }

    // dotnet/extensions #5699: standart hedging işleyicisi TotalRequestTimeout'a uymuyor, istekler iptal edilmiyordu.
    [Fact]
    public async Task StandardHedging_RespectsTotalTimeout_AndCancelsAttempts()
    {
        var cancelled = 0;
        var services = new ServiceCollection();
        services.AddHttpClient("h")
            .ConfigurePrimaryHttpMessageHandler(() => new HangingServer(() => Interlocked.Increment(ref cancelled)))
            .AddStandardAegisHedgingHandler(o =>
            {
                o.TotalRequestTimeout.Timeout = TimeSpan.FromMilliseconds(300);
                o.EndpointAttemptTimeout.Timeout = TimeSpan.FromMilliseconds(250);
                o.HedgingDelay = TimeSpan.FromMilliseconds(50);
                o.MaxHedgedAttempts = 1;
            });
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("h");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync(new Uri("http://h/")));
        Assert.True(watch.ElapsedMilliseconds < 3000, $"toplam zaman aşımı aşıldı: {watch.ElapsedMilliseconds} ms");

        await Task.Delay(200);
        Assert.Equal(2, Volatile.Read(ref cancelled)); // birincil + yedek iptal edildi
    }

    // gRPC A6: ilk istek her zaman gider; ek hedging denemeleri ancak bütçe izin verirse başlar.
    [Fact]
    public async Task HedgingBudget_StopsAdditionalAttempts_WhenExhausted()
    {
        var budget = new RetryBudget(maxTokens: 2, tokenRatio: 1);
        budget.RecordFailure(); // 2 → 1: yarıda, bütçe tükendi
        var attempts = 0;
        var pipeline = new AegisPipelineBuilder("hb")
            .AddHedging(o => { o.MaxHedgedAttempts = 2; o.HedgingDelay = TimeSpan.FromMilliseconds(20); o.Budget = budget; })
            .Build();

        var result = await pipeline.ExecuteAsync(async ct => { Interlocked.Increment(ref attempts); await Task.Delay(150, ct); return 1; }, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Equal(1, attempts); // yalnızca birincil
    }

    // Bütçe yeni denemeye izin vermezse son denemenin özgün istisnası döner (genel "tamamı başarısız" hatası değil).
    [Fact]
    public async Task HedgingBudget_Exhausted_AfterFailure_ReturnsOriginalException()
    {
        var budget = new RetryBudget(maxTokens: 2, tokenRatio: 1);
        budget.RecordFailure(); // bütçe tükendi
        var pipeline = new AegisPipelineBuilder("hb3")
            .AddHedging(o => { o.MaxHedgedAttempts = 2; o.HedgingDelay = TimeSpan.FromSeconds(5); o.Budget = budget; })
            .Build();

        await Assert.ThrowsAsync<TimeZoneNotFoundException>(async () => await pipeline.ExecuteAsync<int>(async ct =>
        {
            await Task.Yield();
            throw new TimeZoneNotFoundException("özgün");
        }, CancellationToken.None));
    }

    // Hedging'de iptal edilen kaybeden deneme bütçeyi tüketmez; ele alınan hata tüketir.
    [Fact]
    public async Task HedgingBudget_CountsFailures_NotCancelledLosers()
    {
        var budget = new RetryBudget(maxTokens: 10, tokenRatio: 1);
        var attempt = 0;
        var pipeline = new AegisPipelineBuilder("hb2")
            .AddHedging(o => { o.MaxHedgedAttempts = 1; o.HedgingDelay = TimeSpan.FromMilliseconds(20); o.Budget = budget; })
            .Build();

        await pipeline.ExecuteAsync(async ct =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                await Task.Delay(Timeout.Infinite, ct); // kaybeden: iptal edilir
            }

            return 1;
        }, CancellationToken.None);
        await Task.Delay(50);

        Assert.Equal(10, budget.Tokens); // kazanan başarı (+1, üst sınırda), kaybedenin iptali sayılmadı
    }

    // resilience4j permittedNumberOfCallsInHalfOpenState: tek şanslı deneme devreyi kapatmamalı.
    [Fact]
    public async Task CircuitBreaker_HalfOpenSuccessThreshold_RequiresConsecutiveSuccesses()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var state = new CircuitBreakerStateProvider();
        var pipeline = new AegisPipelineBuilder("ho")
            .AddCircuitBreaker(o => { o.MinimumThroughput = 1; o.FailureRatio = 1; o.BreakDuration = TimeSpan.FromSeconds(5); o.HalfOpenSuccessThreshold = 3; o.StateProvider = state; })
            .WithTimeProvider(clock)
            .Build();
        await Assert.ThrowsAsync<IOException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new IOException(), CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(5));

        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1), CancellationToken.None);
        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1), CancellationToken.None);
        Assert.Equal(CircuitState.HalfOpen, state.CircuitState); // 2/3: hâlâ yarı açık
        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1), CancellationToken.None);
        Assert.Equal(CircuitState.Closed, state.CircuitState);
    }

    [Fact]
    public async Task CircuitBreaker_HalfOpenSuccessThreshold_AnyFailureReopens()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var state = new CircuitBreakerStateProvider();
        var pipeline = new AegisPipelineBuilder("ho2")
            .AddCircuitBreaker(o => { o.MinimumThroughput = 1; o.FailureRatio = 1; o.BreakDuration = TimeSpan.FromSeconds(5); o.HalfOpenSuccessThreshold = 3; o.StateProvider = state; })
            .WithTimeProvider(clock)
            .Build();
        await Assert.ThrowsAsync<IOException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new IOException(), CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(5));

        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1), CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new IOException(), CancellationToken.None));

        Assert.Equal(CircuitState.Open, state.CircuitState);
    }

    // resilience4j COUNT_BASED: oran son N çağrıdan hesaplanır, zamandan bağımsız.
    [Fact]
    public async Task CircuitBreaker_CountBasedWindow_UsesLastNCalls()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var state = new CircuitBreakerStateProvider();
        var pipeline = new AegisPipelineBuilder("cnt")
            .AddCircuitBreaker(o => { o.SamplingCount = 4; o.MinimumThroughput = 4; o.FailureRatio = 0.5; o.StateProvider = state; })
            .WithTimeProvider(clock)
            .Build();

        // 1 hata + 1 saat sonra (zaman penceresi olsa düşerdi) 2 başarı + 1 hata: son 4 çağrıda 2/4 = 0,5 → açılır.
        await Assert.ThrowsAsync<IOException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new IOException(), CancellationToken.None));
        clock.Advance(TimeSpan.FromHours(1));
        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1), CancellationToken.None);
        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1), CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new IOException(), CancellationToken.None));

        Assert.Equal(CircuitState.Open, state.CircuitState);
    }

    [Fact]
    public void CircuitBreaker_CountWindow_SmallerThanMinimumThroughput_FailsFast() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AegisPipelineBuilder("x").AddCircuitBreaker(o => { o.SamplingCount = 10; o.MinimumThroughput = 20; }).Build());

    // resilience4j METRICS_ONLY: gölge kipte devre açılır (olay üretir) ama istek reddedilmez.
    [Fact]
    public async Task CircuitBreaker_ShadowMode_NeverRejects_ButStillReportsTransitions()
    {
        var opened = 0;
        var state = new CircuitBreakerStateProvider();
        var pipeline = new AegisPipelineBuilder("shadow")
            .AddCircuitBreaker(o =>
            {
                o.MinimumThroughput = 1; o.FailureRatio = 1; o.Mode = CircuitBreakerMode.Shadow; o.StateProvider = state;
                o.OnOpened = _ => { opened++; return default; };
            })
            .Build();

        await Assert.ThrowsAsync<IOException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new IOException(), CancellationToken.None));

        Assert.Equal(CircuitState.Open, state.CircuitState);
        Assert.Equal(1, opened);
        Assert.Equal(5, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(5), CancellationToken.None)); // reddedilmedi
    }

    [Fact]
    public async Task CircuitBreaker_ShadowMode_StillHonorsManualIsolation()
    {
        var manual = new CircuitBreakerManualControl();
        var pipeline = new AegisPipelineBuilder("shadow-iso")
            .AddCircuitBreaker(o => { o.Mode = CircuitBreakerMode.Shadow; o.ManualControl = manual; })
            .Build();
        await manual.IsolateAsync();

        await Assert.ThrowsAsync<IsolatedCircuitException>(async () => await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1), CancellationToken.None));
    }

    // resilience4j DISABLED: devre geçirgen; hiç açılmaz, olay yok.
    [Fact]
    public async Task CircuitBreaker_DisabledMode_IsTransparent()
    {
        var state = new CircuitBreakerStateProvider();
        var pipeline = new AegisPipelineBuilder("off")
            .AddCircuitBreaker(o => { o.MinimumThroughput = 1; o.FailureRatio = 1; o.Mode = CircuitBreakerMode.Disabled; o.StateProvider = state; })
            .Build();

        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<IOException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new IOException(), CancellationToken.None));
        }

        Assert.Equal(CircuitState.Closed, state.CircuitState);
    }

    private sealed class HangingServer(Action onCancelled) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                onCancelled();
                throw;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class SigningHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("X-Signature", "imza");
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class CountingServer(List<int> seen, int failures) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            seen.Add(request.Headers.TryGetValues("X-Signature", out var values) ? values.Count() : 0);
            return Task.FromResult(new HttpResponseMessage(seen.Count <= failures ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        }
    }
}
