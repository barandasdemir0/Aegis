using System.Net;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Chaos;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.Timeout;
using Aegis.Resilience.Distributed.Abstractions;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.DependencyInjection.Aop;
using Aegis.Resilience.Extensions.Http;

namespace Aegis.Tests;

/// <summary>
/// Denetimde tespit edilen hatalar için regresyon testleri.
/// Her test, düzeltmeden ÖNCE başarısız olan somut bir senaryoyu kilitler.
/// </summary>
public class RegressionFixesTests
{
    // ---------------------------------------------------------------------
    // AEGIS-110: Hedging bağlam (Properties / CorrelationId) taşıması
    // ---------------------------------------------------------------------
    [Fact]
    public async Task Hedging_ShouldPropagateContextPropertiesInBothDirections()
    {
        IAegisPipelineBuilder builder = new AegisPipelineBuilder("HedgeContextPipeline");
        var pipeline = builder
            .AddHedging(opt =>
            {
                opt.MaxHedgedAttempts = 1;
                opt.HedgingDelay = TimeSpan.FromMilliseconds(50);
            })
            .Build();

        var ctx = new AegisContext(CancellationToken.None, "HedgeContextPipeline");
        ctx.SetProperty("TenantId", "acme");
        var correlationId = ctx.CorrelationId;

        string? seenTenant = null;
        string? seenCorrelationId = null;

        var result = await pipeline.ExecuteAsync(inner =>
        {
            inner.TryGetProperty<string>("TenantId", out seenTenant);
            seenCorrelationId = inner.CorrelationId;
            inner.SetProperty("Inner.Result", "written");
            return ValueTask.FromResult(42);
        }, ctx);

        Assert.Equal(42, result);
        Assert.Equal("acme", seenTenant);                                  // dışarıdan içeri
        Assert.Equal(correlationId, seenCorrelationId);                    // korelasyon korunur
        Assert.True(ctx.TryGetProperty<string>("Inner.Result", out var written));
        Assert.Equal("written", written);                                  // içeriden dışarı
    }

    [Fact]
    public async Task Hedging_ShouldCancelLosingAttempts_WhenWinnerCompletes()
    {
        IAegisPipelineBuilder builder = new AegisPipelineBuilder("HedgeCancelPipeline");
        var pipeline = builder
            .AddHedging(opt =>
            {
                opt.MaxHedgedAttempts = 1;
                opt.HedgingDelay = TimeSpan.FromMilliseconds(30);
            })
            .Build();

        var attempt = 0;
        var loserCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await pipeline.ExecuteAsync(async ctx =>
        {
            var current = Interlocked.Increment(ref attempt);
            if (current == 1)
            {
                // Birincil deneme asla bitmez; hedging ikinci denemeyi başlatmalı ve sonra bunu iptal etmeli.
                // NOT: İptal sinyali, Register callback'i yerine OCE yakalanarak verilir. Cancel() callback'leri
                // LIFO çalıştırır: Task.Delay'in callback'i önce tetiklenir, bu lambda inline devam edip OCE
                // fırlatır ve "using var registration" kapsamdan çıkarken HENÜZ ÇALIŞMAMIŞ test callback'ini
                // kayıttan siliyordu (CPU açlığı altında 12 turda 5 kez gözlemlendi). Kütüphane token'ı doğru
                // iptal ediyordu; hata testin kendi kayıt yönetimindeydi.
                try
                {
                    await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
                }
                catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested)
                {
                    loserCancelled.TrySetResult();
                    throw;
                }
                return "primary";
            }

            return "hedged";
        });

        Assert.Equal("hedged", result);
        await loserCancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    // ---------------------------------------------------------------------
    // AEGIS-111: Pessimistic Timeout'ta bağlam izolasyonu
    // ---------------------------------------------------------------------
    [Fact]
    public async Task PessimisticTimeout_AbandonedWork_ShouldNotMutateCallerContext()
    {
        var pipeline = new AegisPipelineBuilder("PessimisticIsolationPipeline")
            .AddTimeout(TimeSpan.FromMilliseconds(100), opt => opt.Mode = TimeoutStrategyMode.Pessimistic)
            .Build();

        var callerContext = new AegisContext(CancellationToken.None, "PessimisticIsolationPipeline");
        var abandonedWorkWrote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<AegisTimeoutException>(async () =>
        {
            await pipeline.ExecuteAsync<string>(async ctx =>
            {
                // Token'ı yok sayan inatçı kod: zaman aşımından SONRA bağlama yazmayı dener
                await Task.Delay(400, CancellationToken.None);
                ctx.SetProperty("Abandoned.Write", true);
                abandonedWorkWrote.TrySetResult();
                return "late";
            }, callerContext);
        });

        await abandonedWorkWrote.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Terk edilen görev yalnızca kendi alt bağlamını değiştirebilmelidir
        Assert.False(callerContext.TryGetProperty<bool>("Abandoned.Write", out _));
    }

    // ---------------------------------------------------------------------
    // AEGIS-115: MultiEndpointHedgingHandler hızlı birincil yanıt (InvalidCastException)
    // ---------------------------------------------------------------------
    private sealed class ScriptedEndpointHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public ScriptedEndpointHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }

    private static HttpMessageInvoker CreateInvoker(DelegatingHandler handler, HttpMessageHandler inner)
    {
        handler.InnerHandler = inner;
        return new HttpMessageInvoker(handler);
    }

    [Fact]
    public async Task MultiEndpointHedging_FastFailingPrimary_ShouldFallOverToSecondary()
    {
        var options = new MultiEndpointHedgingOptions
        {
            Endpoints = new List<Uri>
            {
                new("https://primary.api.com/v1/test"),
                new("https://secondary.api.com/v1/test")
            },
            // Birincil, hedging gecikmesinden ÇOK ÖNCE yanıt döner: eski kodda InvalidCastException oluşuyordu
            HedgingDelay = TimeSpan.FromMilliseconds(500),
            MaxHedgedAttempts = 1
        };

        var backend = new ScriptedEndpointHandler(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "primary.api.com")
            {
                await Task.Delay(5, ct);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            await Task.Delay(10, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { ReasonPhrase = "Secondary" };
        });

        using var invoker = CreateInvoker(new MultiEndpointHedgingHandler(options), backend);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://primary.api.com/v1/test");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Secondary", response.ReasonPhrase);
    }

    // Idempotency-Key'siz POST birden fazla uç noktaya gönderilmez (çift ödeme koruması); yalnızca birincil denenir.
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task MultiEndpointHedging_NonIdempotentPost_SentOnce_UnlessAllowed(bool allow, int expectedCalls)
    {
        var options = new MultiEndpointHedgingOptions
        {
            Endpoints = [new("https://primary.api.com/"), new("https://secondary.api.com/")],
            HedgingDelay = TimeSpan.FromMilliseconds(20),
            AllowNonIdempotentHedging = allow
        };
        var calls = 0;
        var backend = new ScriptedEndpointHandler(async (_, ct) =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(100, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var invoker = CreateInvoker(new MultiEndpointHedgingHandler(options), backend);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://primary.api.com/pay") { Content = new StringContent("{}") };
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedCalls, Volatile.Read(ref calls));
    }

    // ---------------------------------------------------------------------
    // AEGIS-103: Dinamik işleyicide idempotency koruması ve istek klonlama
    // ---------------------------------------------------------------------
    [Fact]
    public async Task DynamicHandler_NonIdempotentPost_ShouldNotBeRetried()
    {
        var registry = new AegisPipelineRegistry();
        IAegisPipelineBuilder builder = new AegisPipelineBuilder("dyn-post");
        registry.RegisterPipeline("dyn-post", builder
            .AddRetry(opt =>
            {
                opt.MaxRetryAttempts = 3;
                opt.Delay = TimeSpan.Zero;
            })
            .Build());

        var calls = 0;
        var backend = new ScriptedEndpointHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });

        using var invoker = CreateInvoker(new AegisDynamicResilienceHandler(registry, _ => "dyn-post"), backend);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.test/charge")
        {
            Content = new StringContent("{\"amount\":100}")
        };

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        // Idempotency-Key olmayan POST asla ikinci kez gönderilmez (çift işlem koruması)
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DynamicHandler_IdempotentGet_ShouldRetryWithClonedRequest()
    {
        var registry = new AegisPipelineRegistry();
        IAegisPipelineBuilder builder = new AegisPipelineBuilder("dyn-get");
        registry.RegisterPipeline("dyn-get", builder
            .AddRetry(opt =>
            {
                opt.MaxRetryAttempts = 2;
                opt.Delay = TimeSpan.Zero;
            })
            .Build());

        var calls = 0;
        var sentRequests = new List<HttpRequestMessage>();
        var backend = new ScriptedEndpointHandler((req, _) =>
        {
            lock (sentRequests)
            {
                sentRequests.Add(req);
            }

            var current = Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(
                current < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        });

        using var invoker = CreateInvoker(new AegisDynamicResilienceHandler(registry, _ => "dyn-get"), backend);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.test/items");

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, calls);

        // Her yeniden denemede yeni bir HttpRequestMessage örneği kullanılmalıdır
        Assert.Equal(3, sentRequests.Count);
        Assert.Equal(3, sentRequests.Distinct().Count());
    }

    // ---------------------------------------------------------------------
    // AEGIS-113: Dağıtık Circuit Breaker durum deposu üzerinden paylaşım
    // ---------------------------------------------------------------------
    [Fact]
    public async Task DistributedCircuitBreaker_ShouldShareOpenState_AcrossPipelines()
    {
        // Aynı durum deposunu paylaşan iki ayrı "pod"
        var sharedStore = new InMemoryCircuitBreakerStateStore();

        IAegisPipeline BuildPod(string name)
        {
            IAegisPipelineBuilder builder = new AegisPipelineBuilder(name);
            return builder
                .AddDistributedCircuitBreaker(sharedStore, opt =>
                {
                    opt.CircuitKey = "payments";
                    opt.MinimumThroughput = 2;
                    opt.FailureRatio = 1.0;
                    opt.BreakDuration = TimeSpan.FromSeconds(30);
                    opt.StateCacheDuration = TimeSpan.Zero;
                })
                .Build();
        }

        using var podA = BuildPod("pod-a");
        using var podB = BuildPod("pod-b");

        // Pod A üzerinde devreyi aç
        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<HttpRequestException>(async () =>
                await podA.ExecuteAsync<string>(_ => throw new HttpRequestException("down")));
        }

        // Pod B, hiç hata görmemiş olmasına rağmen paylaşılan duruma uyarak fail-fast yapmalı
        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
            await podB.ExecuteAsync(_ => ValueTask.FromResult("should-not-run")));
    }

    // ---------------------------------------------------------------------
    // AEGIS-118: AOP proxy istisnalarını TargetInvocationException ile sarmalamamalı
    // ---------------------------------------------------------------------
    public interface IPaymentService
    {
        Task<string> ChargeAsync();
    }

    private sealed class FailingPaymentService : IPaymentService
    {
        public int CallCount { get; private set; }

        public Task<string> ChargeAsync()
        {
            CallCount++;
            if (CallCount < 3)
            {
                throw new HttpRequestException("geçici ağ hatası");
            }

            return Task.FromResult("charged");
        }
    }

    [Fact]
    public async Task AopProxy_ShouldSurfaceOriginalException_SoShouldHandlePredicatesMatch()
    {
        var registry = new AegisPipelineRegistry();
        IAegisPipelineBuilder builder = new AegisPipelineBuilder("aop-pipeline");
        registry.RegisterPipeline("aop-pipeline", builder
            .AddRetry(opt =>
            {
                opt.MaxRetryAttempts = 3;
                opt.Delay = TimeSpan.Zero;
                // Yalnızca HttpRequestException yeniden denenir:
                // sarmalanmış TargetInvocationException gelseydi bu koşul asla eşleşmezdi
                opt.ShouldHandle = ex => ex is HttpRequestException;
            })
            .Build());

        var target = new FailingPaymentService();
        var proxy = AegisDispatchProxy<IPaymentService>.Create(target, registry, "aop-pipeline");

        var result = await proxy.ChargeAsync();

        Assert.Equal("charged", result);
        Assert.Equal(3, target.CallCount);
    }

    public interface ISyncCounter
    {
        int Next();
    }

    private sealed class FlakySyncCounter : ISyncCounter
    {
        public int Calls;
        public List<int> Threads { get; } = [];

        public int Next()
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            return ++Calls < 3 ? throw new TimeZoneNotFoundException("geçici") : Calls;
        }
    }

    // Senkron proxy çağrısı çağıranın iş parçacığında çalışır, yeniden dener ve özgün istisnayı yükseltir.
    [Fact]
    public void AopProxy_SyncMethod_RunsOnCallerThread_RetriesAndSurfacesOriginalException()
    {
        var registry = new AegisPipelineRegistry();
        registry.RegisterPipeline("aop-sync", new AegisPipelineBuilder("aop-sync")
            .AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.Zero; }).Build());

        var target = new FlakySyncCounter();
        var proxy = AegisDispatchProxy<ISyncCounter>.Create(target, registry, "aop-sync");

        Assert.Equal(3, proxy.Next());
        Assert.All(target.Threads, id => Assert.Equal(Environment.CurrentManagedThreadId, id));

        target.Calls = -10; // tüm denemeler başarısız
        Assert.Throws<TimeZoneNotFoundException>(() => proxy.Next());
    }

    // ---------------------------------------------------------------------
    // AEGIS-119: Registry aynı boru hattını iki kez kurmamalı ve sahiplendiğini dispose etmeli
    // ---------------------------------------------------------------------
    [Fact]
    public void Registry_ShouldReturnSameInstance_AndDisposeOwnedPipelines()
    {
        var buildCount = 0;
        var configurator = new DelegateAegisPipelineConfigurator("cached", (b, _) =>
        {
            Interlocked.Increment(ref buildCount);
            b.AddCache(TimeSpan.FromMinutes(5));
        });

        var registry = new AegisPipelineRegistry(
            new EmptyServiceProvider(),
            new IAegisPipelineConfigurator[] { configurator });

        var first = registry.GetPipeline("cached");
        var second = registry.GetPipeline("cached");

        Assert.Same(first, second);
        Assert.Equal(1, buildCount);

        // Dispose: sahiplenilen boru hatlarının kaynakları (Timer vb.) serbest bırakılır
        registry.Dispose();
        registry.Dispose(); // idempotent olmalı
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    // ---------------------------------------------------------------------
    // Chaos: Yanlış tipte üretilen sonuç anlaşılır bir hata vermeli (kör cast değil)
    // ---------------------------------------------------------------------
    [Fact]
    public async Task Chaos_ResultGeneratorWithWrongType_ShouldThrowDescriptiveException()
    {
        var pipeline = new AegisPipelineBuilder("ChaosTypePipeline")
            .AddChaos(opt =>
            {
                opt.Enabled = true;
                opt.InjectionRate = 1.0;
                opt.ResultGenerator = _ => 42; // int, ancak boru hattı string bekliyor
            })
            .Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult("beklenen")));

        Assert.Contains("ResultGenerator", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // CircuitBreaker: OnHalfOpened olayı tetiklenmeli
    // ---------------------------------------------------------------------
    [Fact]
    public async Task CircuitBreaker_ShouldRaiseOnHalfOpened_WhenBreakDurationElapses()
    {
        var halfOpenedRaised = new TaskCompletionSource<CircuitState>(TaskCreationOptions.RunContinuationsAsynchronously);

        var pipeline = new AegisPipelineBuilder("HalfOpenEventPipeline")
            .AddCircuitBreaker(opt =>
            {
                opt.MinimumThroughput = 1;
                opt.FailureRatio = 1.0;
                opt.BreakDuration = TimeSpan.FromMilliseconds(50);
                opt.OnHalfOpened = evt =>
                {
                    halfOpenedRaised.TrySetResult(evt.NewState);
                    return ValueTask.CompletedTask;
                };
            })
            .Build();

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await pipeline.ExecuteAsync<string>(_ => throw new HttpRequestException("fail")));

        await Task.Delay(120);

        var result = await pipeline.ExecuteAsync(_ => ValueTask.FromResult("probe-ok"));
        Assert.Equal("probe-ok", result);

        var state = await halfOpenedRaised.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(CircuitState.HalfOpen, state);
    }
}
