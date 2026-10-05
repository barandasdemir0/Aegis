using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.Http;

namespace Aegis.Tests;

/// <summary>
/// POLLY / Microsoft.Extensions.Http.Resilience PARİTE TESTLERİ — HTTP işleyici ve Registry katmanı.
///   • Http.Resilience: gövde tekrar oynatma, idempotency, geçici durum kodları, Retry-After, iptal yayılımı,
///     başarısız yanıtların dispose edilmesi, büyük gövde, akış (stream) gövdeler
///   • Polly Registry: eşzamanlı GetOrAdd tek örnek, Dispose sonrası kullanım, dış sahiplik, zehirli configurator
/// </summary>
public class PollyParityHttpAndRegistryTests
{
    // ------------------------------------------------------------------ yardımcılar
    private sealed class ScriptedHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> script) : HttpMessageHandler
    {
        public int Calls;
        public readonly ConcurrentBag<string> Bodies = new();
        public readonly ConcurrentBag<CancellationToken> Tokens = new();
        public int MaxConcurrent; private int _current;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var c = Interlocked.Increment(ref _current);
            InterlockedMax(ref MaxConcurrent, c);
            try
            {
                var n = Interlocked.Increment(ref Calls);
                Tokens.Add(ct);
                if (request.Content != null) Bodies.Add(await request.Content.ReadAsStringAsync(ct));
                return await script(request, n, ct);
            }
            finally { Interlocked.Decrement(ref _current); }
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int cur;
        while ((cur = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, cur) != cur) { }
    }

    /// <summary>Dispose edildiğini izleyen içerik — yanıt sızıntısı tespiti için.</summary>
    private sealed class TrackedContent(string text) : StringContent(text)
    {
        public static int LiveCount;
        public TrackedContent Register() { Interlocked.Increment(ref LiveCount); return this; }
        protected override void Dispose(bool disposing) { if (disposing) Interlocked.Decrement(ref LiveCount); base.Dispose(disposing); }
    }

    private static HttpClient Client(IAegisPipeline pipeline, HttpMessageHandler inner, bool allowNonIdempotent = false)
        => new(new AegisResilienceHandler(pipeline, allowNonIdempotentRetry: allowNonIdempotent) { InnerHandler = inner }) { Timeout = Timeout.InfiniteTimeSpan };

    private static IAegisPipeline RetryPipeline(int attempts = 3) =>
        new AegisPipelineBuilder("http-retry").AddRetry(o => { o.MaxRetryAttempts = attempts; o.Delay = TimeSpan.FromMilliseconds(5); o.UseJitter = false; }).Build();

    // =====================================================================
    // HTTP 1 — Gövdeli POST + Idempotency-Key: her denemede AYNI gövde gider (stream tükenmez)
    // =====================================================================
    [Fact]
    public async Task Post_WithIdempotencyKey_BodyReplayedIdenticallyOnEveryAttempt()
    {
        var inner = new ScriptedHandler((_, n, _) => Task.FromResult(new HttpResponseMessage(n < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)));
        using var client = Client(RetryPipeline(), inner);

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://x/pay") { Content = new StringContent("{\"amount\":100}") };
        req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(3, inner.Calls);
        Assert.Equal(3, inner.Bodies.Count);
        Assert.All(inner.Bodies, b => Assert.Equal("{\"amount\":100}", b));
    }

    [Fact]
    public async Task Post_NonSeekableStreamBody_IsBufferedAndReplayed()
    {
        var inner = new ScriptedHandler((_, n, _) => Task.FromResult(new HttpResponseMessage(n < 2 ? HttpStatusCode.BadGateway : HttpStatusCode.OK)));
        using var client = Client(RetryPipeline(), inner);

        var bytes = System.Text.Encoding.UTF8.GetBytes("akış-gövdesi");
        using var req = new HttpRequestMessage(HttpMethod.Put, "https://x/upload") { Content = new StreamContent(new NonSeekableStream(bytes)) };

        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, inner.Bodies.Count);
        Assert.All(inner.Bodies, b => Assert.Equal("akış-gövdesi", b));
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data) { public override bool CanSeek => false; }

    // =====================================================================
    // HTTP 2 — Idempotency-Key OLMAYAN POST asla yeniden denenmez; yanıt olduğu gibi döner
    // =====================================================================
    [Fact]
    public async Task Post_WithoutIdempotencyKey_NeverRetried_ResponseReturnedAsIs()
    {
        var inner = new ScriptedHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var client = Client(RetryPipeline(), inner);

        using var resp = await client.PostAsync("https://x/pay", new StringContent("x"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode); // istisna değil, ham yanıt
        Assert.Equal(1, inner.Calls);                                     // ÇİFT ÖDEME YOK
    }

    // =====================================================================
    // HTTP 3 — Geçici durum kodları yeniden denenir, kalıcılar denenmez
    // =====================================================================
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.NotImplemented, true)] // tüm 5xx geçicidir (Polly HandleTransientHttpError + Microsoft ile aynı; 1.3.0)
    public async Task Get_StatusCode_RetriedOnlyIfTransient(HttpStatusCode code, bool shouldRetry)
    {
        var inner = new ScriptedHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(code)));
        using var client = Client(RetryPipeline(2), inner);

        if (shouldRetry)
        {
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://x/"));
            Assert.Equal(code, ex.StatusCode);
            Assert.Equal(3, inner.Calls);
        }
        else
        {
            using var resp = await client.GetAsync("https://x/");
            Assert.Equal(code, resp.StatusCode);
            Assert.Equal(1, inner.Calls);
        }
    }

    // =====================================================================
    // HTTP 4 — Retry-After başlığı Retry gecikmesini yönetir (delta-saniye ve HTTP tarihi)
    // =====================================================================
    [Fact]
    public async Task RetryAfterHeader_DeltaSeconds_OverridesConfiguredDelay()
    {
        var inner = new ScriptedHandler((_, n, _) =>
        {
            var r = new HttpResponseMessage(n == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
            if (n == 1) r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return Task.FromResult(r);
        });
        using var client = Client(RetryPipeline(), inner);

        var sw = Stopwatch.StartNew();
        using var resp = await client.GetAsync("https://x/");
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, inner.Calls);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(900), $"Retry-After 1sn yok sayıldı: {sw.Elapsed}");
    }

    [Fact]
    public async Task RetryAfterHeader_HttpDate_ParsedCorrectly()
    {
        Assert.True(HttpRetryAfterHelper.TryParseRaw("2", out var d1)); Assert.Equal(TimeSpan.FromSeconds(2), d1);
        Assert.True(HttpRetryAfterHelper.TryParseRaw(DateTimeOffset.UtcNow.AddSeconds(30).ToString("R"), out var d2));
        Assert.InRange(d2.TotalSeconds, 25, 31);
        Assert.False(HttpRetryAfterHelper.TryParseRaw("saçma", out _));
        Assert.False(HttpRetryAfterHelper.TryParseRaw(null, out _));
        // Geçmiş tarih -> negatif gecikme üretmemeli
        if (HttpRetryAfterHelper.TryParseRaw(DateTimeOffset.UtcNow.AddSeconds(-30).ToString("R"), out var past)) Assert.True(past >= TimeSpan.Zero);
        await Task.CompletedTask;
    }

    // =====================================================================
    // HTTP 5 — Başarısız (yeniden denenen) yanıtlar dispose edilir; son yanıt edilmez
    // =====================================================================
    [Fact]
    public async Task FailedAttemptResponses_AreDisposed_FinalIsNot()
    {
        TrackedContent.LiveCount = 0;
        var inner = new ScriptedHandler((_, n, _) => Task.FromResult(new HttpResponseMessage(n < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
        {
            Content = new TrackedContent($"yanıt-{n}").Register()
        }));
        using var client = Client(RetryPipeline(), inner);

        var resp = await client.GetAsync("https://x/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, TrackedContent.LiveCount); // yalnızca döndürülen yanıt canlı
        resp.Dispose();
        Assert.Equal(0, TrackedContent.LiveCount);
    }

    // =====================================================================
    // HTTP 6 — Çağıranın iptali iç işleyiciye ulaşır ve retry'ı durdurur
    // =====================================================================
    [Fact]
    public async Task CallerCancellation_PropagatesToInnerHandler_AndStopsRetries()
    {
        using var cts = new CancellationTokenSource();
        var inner = new ScriptedHandler(async (_, n, ct) =>
        {
            if (n == 1) cts.Cancel();
            await Task.Delay(50, ct); // iptal buraya ulaşmalı
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var client = Client(RetryPipeline(5), inner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("https://x/", cts.Token));
        Assert.Equal(1, inner.Calls);
        Assert.All(inner.Tokens, t => Assert.True(t.CanBeCanceled));
    }

    // =====================================================================
    // HTTP 7 — Ağ istisnası (HttpRequestException) yeniden denenir; tükendiğinde asıl istisna yükselir
    // =====================================================================
    [Fact]
    public async Task NetworkException_Retried_ThenOriginalSurfaces()
    {
        var inner = new ScriptedHandler((_, _, _) => throw new HttpRequestException("bağlantı reddedildi"));
        using var client = Client(RetryPipeline(2), inner);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://x/"));
        Assert.Equal("bağlantı reddedildi", ex.Message);
        Assert.Equal(3, inner.Calls);
    }

    // =====================================================================
    // HTTP 8 — Yük: 500 paralel istek, ConcurrencyLimiter(8) iç işleyiciye YANSIMALI
    // =====================================================================
    [Fact]
    public async Task ParallelLoad_ConcurrencyLimiterEnforcedAtInnerHandler()
    {
        var inner = new ScriptedHandler(async (_, _, ct) => { await Task.Delay(5, ct); return new HttpResponseMessage(HttpStatusCode.OK); });
        var pipeline = new AegisPipelineBuilder("http-load").AddConcurrencyLimiter(8, o => { o.QueueLimit = int.MaxValue; o.QueueTimeout = TimeSpan.FromSeconds(30); }).AddRetry(o => o.MaxRetryAttempts = 1).Build();
        using var client = Client(pipeline, inner);

        var results = await Task.WhenAll(Enumerable.Range(0, 500).Select(_ => client.GetAsync("https://x/")));
        Assert.All(results, r => { Assert.Equal(HttpStatusCode.OK, r.StatusCode); r.Dispose(); });
        Assert.Equal(500, inner.Calls);
        Assert.True(inner.MaxConcurrent <= 8, $"bulkhead delindi: {inner.MaxConcurrent} eşzamanlı");
    }

    // =====================================================================
    // HTTP 9 — Bellek sınırını aşan gövde yeniden denenmez (OOM koruması), ilk deneme yine yapılır
    // =====================================================================
    [Fact]
    public async Task OversizedBody_FirstAttemptSent_NoRetry_RealResponseReturned()
    {
        // 1.0.5: Tampon sınırını aşan gövde yeniden gönderilemez; Retry ek deneme ÜRETMEZ ve ilk denemenin gerçek
        // yanıtı döner. (Eskiden 2. deneme yapay InvalidOperationException fırlatıp gerçek sonucu gizliyordu.)
        var inner = new ScriptedHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var handler = new AegisResilienceHandler(RetryPipeline(), allowNonIdempotentRetry: true, maxRequestBodySize: 16) { InnerHandler = inner };
        using var client = new HttpClient(handler);

        using var response = await client.PostAsync("https://x/", new StringContent(new string('x', 1000)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    // Uzunluğu bilinmeyen (akış) gövde sınırı aşarsa istek düşmez: tek deneme, tüm gövde iletilir.
    [Fact]
    public async Task OversizedBody_UnknownLength_FirstAttemptSent_WithFullBody()
    {
        long received = -1;
        var inner = new ScriptedHandler(async (req, _, _) =>
        {
            received = (await req.Content!.ReadAsByteArrayAsync()).Length;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var handler = new AegisResilienceHandler(RetryPipeline(), allowNonIdempotentRetry: true, maxRequestBodySize: 16) { InnerHandler = inner };
        using var client = new HttpClient(handler);

        var content = new StreamContent(new NonSeekableStream(new byte[1000]));
        Assert.Null(content.Headers.ContentLength);
        using var response = await client.PostAsync("https://x/", content);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, inner.Calls);
        Assert.Equal(1000, received);
    }

    // =====================================================================
    // HTTP 10 — Bağlam korelasyonu: aynı istek içindeki tüm denemeler aynı CorrelationId'yi taşır
    // =====================================================================
    [Fact]
    public async Task AllAttempts_ShareSameCorrelationId_ViaRequestContext()
    {
        var pipeline = new AegisPipelineBuilder("http-corr").AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.Zero; }).Build();
        var seen = new ConcurrentBag<string>();
        var inner = new ScriptedHandler((req, n, _) =>
        {
            seen.Add(req.GetOrCreateAegisContext("http-corr").CorrelationId);
            return Task.FromResult(new HttpResponseMessage(n < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        });
        using var client = Client(pipeline, inner);
        using var _ = await client.GetAsync("https://x/");
        Assert.Equal(3, seen.Count);
        Assert.Single(seen.Distinct());
    }

    // =====================================================================
    // REGISTRY 1 — Eşzamanlı GetPipeline: tek örnek, configurator tam 1 kez
    // =====================================================================
    [Fact]
    public async Task Registry_ConcurrentGet_ReturnsSameInstance_ConfiguresOnce()
    {
        var configured = 0;
        var services = new ServiceCollection();
        services.AddAegisPipeline("ortak", b => { Interlocked.Increment(ref configured); Thread.Sleep(20); b.AddRetry(); });
        using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IAegisPipelineRegistry>();

        var pipelines = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => registry.GetPipeline("ORTAK"))));
        Assert.Single(pipelines.Distinct());
        Assert.Equal(1, configured);
    }

    // =====================================================================
    // REGISTRY 2 — Dispose: registry'nin kurduğu pipeline'lar dispose edilir, dışarıdan verilen edilmez
    // =====================================================================
    private sealed class TrackDisposeStrategy : IAegisStrategy, IDisposable
    {
        public int Disposed; public string Name => "track";
        public ValueTask<T> ExecuteAsync<T>(Func<AegisContext, ValueTask<T>> cb, AegisContext ctx) => cb(ctx);
        public void Dispose() => Disposed++;
    }

    [Fact]
    public void Registry_Dispose_DisposesOwnedPipelines_NotExternal_ThenThrows()
    {
        var owned = new TrackDisposeStrategy();
        var external = new TrackDisposeStrategy();
        var services = new ServiceCollection();
        services.AddAegisPipeline("sahipli", b => b.AddStrategy(owned));
        var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IAegisPipelineRegistry>();

        _ = registry.GetPipeline("sahipli");
        registry.RegisterPipeline("dış", new AegisPipelineBuilder("dış").AddStrategy(external).Build());

        sp.Dispose(); // konteyner kapanıyor

        Assert.Equal(1, owned.Disposed);
        Assert.Equal(0, external.Disposed); // çağıranın sahipliğinde
        Assert.Throws<ObjectDisposedException>(() => registry.GetPipeline("sahipli"));
    }

    // =====================================================================
    // REGISTRY 3 — Zehirli configurator: hata yükselir ama cache'lenmez; düzelince çalışır
    // =====================================================================
    [Fact]
    public void Registry_ConfiguratorThrows_NotPoisoned_RecoversOnNextCall()
    {
        var attempts = 0;
        var services = new ServiceCollection();
        services.AddAegisPipeline("kırılgan", b => { if (++attempts == 1) throw new ApplicationException("ilk kurulum patladı"); b.AddRetry(); });
        using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IAegisPipelineRegistry>();

        Assert.Throws<ApplicationException>(() => registry.GetPipeline("kırılgan"));
        var p = registry.GetPipeline("kırılgan"); // 2. deneme başarılı olmalı
        Assert.NotNull(p);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void Registry_UnknownName_ThrowsClear_AndTryGetReturnsFalse()
    {
        var services = new ServiceCollection(); services.AddAegis();
        using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IAegisPipelineRegistry>();
        Assert.False(registry.TryGetPipeline("yok", out var p)); Assert.Null(p);
        var ex = Assert.ThrowsAny<Exception>(() => registry.GetPipeline("yok"));
        Assert.Contains("yok", ex.Message);
    }
}
