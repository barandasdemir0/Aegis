using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Aegis.Resilience.AspNetCore;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Extensions.Http;
using Aegis.Resilience.Grpc;
using Aegis.Resilience.Grpc.AspNetCore;

namespace Aegis.Tests;

/// <summary>
/// gRPC uçtan uca: gerçek Kestrel (HTTP/2) + gerçek GrpcChannel; durumlar gerçek trailer'larla, deadline'lar gerçek zamanlarla.
/// Kurallar: gRPC A6 (commit, deadline, pushback, grpc-previous-rpc-attempts), Microsoft "gRPC retries" belgesi, gRPC durum kodu
/// rehberi ve HTTP→gRPC eşleme tablosu.
/// </summary>
public sealed class GrpcEndToEndTests : IAsyncLifetime
{
    private readonly TestState _state = new();
    private WebApplication _app = null!;
    private string _address = null!;

    public async Task InitializeAsync() => (_app, _address) = await StartServerAsync(_state);

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private CallInvoker Client(Action<IAegisPipelineBuilder> configure)
    {
        var builder = new AegisPipelineBuilder("grpc-istemci");
        configure(builder);
        var channel = GrpcChannel.ForAddress(_address);
        return channel.Intercept(new AegisGrpcClientInterceptor(builder.Build()));
    }

    private static Action<IAegisPipelineBuilder> Retry(int attempts = 3, int delayMs = 1) =>
        b => b.AddRetry(o => { o.MaxRetryAttempts = attempts; o.Delay = TimeSpan.FromMilliseconds(delayMs); o.ShouldHandle = AegisGrpcTransientErrors.ShouldRetry; });

    private static Task<string> Call(CallInvoker invoker, string request, CallOptions options = default) =>
        invoker.AsyncUnaryCall(TestGrpc.Unary, null, options, request).ResponseAsync;

    [Fact]
    public async Task Unary_RetriesUnavailable_AndSendsPreviousAttemptsHeader()
    {
        Assert.Equal("ok", await Call(Client(Retry()), "unavailable-then-ok:2"));
        Assert.Equal(new string?[] { null, "1", "2" }.AsEnumerable(), _state.PreviousAttemptHeaders("unavailable-then-ok:2").AsEnumerable());
    }

    // gRPC durum kodu rehberi: istemci hataları yeniden denenmez.
    [Fact]
    public async Task Unary_DoesNotRetry_ClientErrors()
    {
        var error = await Assert.ThrowsAsync<RpcException>(() => Call(Client(Retry()), "invalid"));
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        Assert.Equal(1, _state.Calls("invalid"));
    }

    // gRPC A6: negatif pushback = sunucu yeniden denemeyi yasakladı.
    [Fact]
    public async Task Unary_NegativePushback_StopsRetrying()
    {
        var error = await Assert.ThrowsAsync<RpcException>(() => Call(Client(Retry()), "pushback-no-retry"));
        Assert.Equal(StatusCode.Unavailable, error.StatusCode);
        Assert.Equal(1, _state.Calls("pushback-no-retry"));
    }

    // gRPC A6: pozitif pushback = tam bu kadar bekleyip yeniden dene (geri çekilme süresi yerine).
    [Fact]
    public async Task Unary_PositivePushback_DelaysRetry()
    {
        var watch = Stopwatch.StartNew();
        Assert.Equal("ok", await Call(Client(Retry(delayMs: 1)), "pushback-300-then-ok"));
        Assert.True(watch.ElapsedMilliseconds >= 280, $"pushback'e uyulmadı: {watch.ElapsedMilliseconds} ms");
    }

    // gRPC A6 / Microsoft: deadline tüm denemeleri kapsar; dolunca kalan yeniden denemeler atlanır, DeadlineExceeded döner.
    [Fact]
    public async Task Unary_Deadline_SpansAllAttempts()
    {
        var watch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<RpcException>(() =>
            Call(Client(Retry(attempts: 50, delayMs: 100)), "always-unavailable", new CallOptions(deadline: DateTime.UtcNow.AddMilliseconds(400))));

        Assert.Equal(StatusCode.DeadlineExceeded, error.StatusCode);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"deadline'da durmadı: {watch.ElapsedMilliseconds} ms");
        Assert.True(_state.Calls("always-unavailable") < 10);
    }

    [Fact]
    public async Task Unary_CallerCancellation_IsCancelled_NotRetried()
    {
        using var cts = new CancellationTokenSource(200);
        var error = await Assert.ThrowsAsync<RpcException>(() =>
            Call(Client(Retry(attempts: 5)), "slow-5000", new CallOptions(cancellationToken: cts.Token)));

        Assert.Equal(StatusCode.Cancelled, error.StatusCode);
        Assert.Equal(1, _state.Calls("slow-5000"));
    }

    // Grpc.Net.Client iptali RpcException(Cancelled) olarak fırlatır; Aegis deneme zaman aşımını yine tanıyıp yeniden dener.
    [Fact]
    public async Task Unary_AttemptTimeout_RetriesHungAttempt()
    {
        var invoker = Client(b =>
        {
            Retry()(b);
            b.AddTimeout(TimeSpan.FromMilliseconds(300));
        });

        Assert.Equal("ok", await Call(invoker, "hang-first-then-ok"));
        Assert.Equal(2, _state.Calls("hang-first-then-ok"));
    }

    // Açık devre gRPC çağıranına RpcException(Unavailable) olarak döner; pushback trailer'ı açık kalma süresini bildirir.
    [Fact]
    public async Task Unary_OpenCircuit_MapsToUnavailable_WithPushback()
    {
        var invoker = Client(b => b.AddCircuitBreaker(o =>
        {
            o.MinimumThroughput = 1; o.FailureRatio = 1; o.BreakDuration = TimeSpan.FromSeconds(30); o.ShouldHandle = AegisGrpcTransientErrors.IsCircuitFailure;
        }));
        await Assert.ThrowsAsync<RpcException>(() => Call(invoker, "always-unavailable"));

        var rejected = await Assert.ThrowsAsync<RpcException>(() => Call(invoker, "ok"));

        Assert.Equal(StatusCode.Unavailable, rejected.StatusCode);
        Assert.True(AegisGrpcTransientErrors.TryGetPushback(rejected.Trailers, out var pushback) && pushback > TimeSpan.Zero);
        Assert.Equal(0, _state.Calls("ok")); // istek sunucuya gitmedi
    }

    // İstemci hataları (InvalidArgument) devreyi açmaz.
    [Fact]
    public async Task CircuitBreaker_Ignores_ClientErrors()
    {
        var invoker = Client(b => b.AddCircuitBreaker(o =>
        {
            o.MinimumThroughput = 1; o.FailureRatio = 1; o.ShouldHandle = AegisGrpcTransientErrors.IsCircuitFailure;
        }));
        await Assert.ThrowsAsync<RpcException>(() => Call(invoker, "invalid"));

        Assert.Equal("ok", await Call(invoker, "ok"));
    }

    [Fact]
    public async Task Unary_Hedging_FastestAttemptWins()
    {
        var invoker = Client(b => b.AddHedging(o => { o.MaxHedgedAttempts = 1; o.HedgingDelay = TimeSpan.FromMilliseconds(50); }));
        var watch = Stopwatch.StartNew();

        Assert.Equal("hızlı", await Call(invoker, "slow-first"));
        Assert.True(watch.ElapsedMilliseconds < 1500, $"yedek deneme kazanmadı: {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void BlockingUnary_IsRetried()
    {
        var invoker = Client(Retry());
        Assert.Equal("ok", invoker.BlockingUnaryCall(TestGrpc.Unary, null, default, "unavailable-then-ok:1"));
    }

    // gRPC A6 commit: ilk mesaja kadar yeniden denenir.
    [Fact]
    public async Task ServerStreaming_RetriesBeforeFirstMessage()
    {
        using var call = Client(Retry()).AsyncServerStreamingCall(TestGrpc.Stream, null, default, "stream-fail-first");
        var messages = new List<string>();
        await foreach (var message in call.ResponseStream.ReadAllAsync())
        {
            messages.Add(message);
        }

        Assert.Equal(["1", "2", "3"], messages);
        Assert.Equal(2, _state.Calls("stream-fail-first"));
    }

    // gRPC A6 commit: ilk mesajdan sonra çağrı commit olur; sonraki hata yeniden denenmez, çağırana yükselir.
    [Fact]
    public async Task ServerStreaming_DoesNotRetry_AfterCommit()
    {
        using var call = Client(Retry()).AsyncServerStreamingCall(TestGrpc.Stream, null, default, "stream-fail-after-first");

        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal("1", call.ResponseStream.Current);
        var error = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(StatusCode.Unavailable, error.StatusCode);
        Assert.Equal(1, _state.Calls("stream-fail-after-first"));
    }

    // Commit sonrası deneme zaman aşımı akışı kesmez (uzun akış, kısa deneme süresi).
    [Fact]
    public async Task ServerStreaming_AttemptTimeout_DoesNotCutCommittedStream()
    {
        var invoker = Client(b => b.AddTimeout(TimeSpan.FromMilliseconds(300)));
        using var call = invoker.AsyncServerStreamingCall(TestGrpc.Stream, null, default, "stream-long");
        var count = 0;
        await foreach (var _ in call.ResponseStream.ReadAllAsync())
        {
            count++;
        }

        Assert.Equal(5, count); // 5 × 150 ms > 300 ms
    }

    // HTTP katmanındaki Aegis işleyicisi gRPC isteğini dokunmadan geçirir: 300 ms toplam süre uzun akışı kesmez.
    [Fact]
    public async Task HttpResilienceHandler_PassesGrpcThrough_Untouched()
    {
        var handler = new AegisResilienceHandler(new AegisPipelineBuilder("http").AddTimeout(TimeSpan.FromMilliseconds(300)).Build())
        {
            InnerHandler = new SocketsHttpHandler()
        };
        var channel = GrpcChannel.ForAddress(_address, new GrpcChannelOptions { HttpHandler = handler });
        using var call = channel.CreateCallInvoker().AsyncServerStreamingCall(TestGrpc.Stream, null, default, "stream-long");
        var count = 0;
        await foreach (var _ in call.ResponseStream.ReadAllAsync())
        {
            count++;
        }

        Assert.Equal(5, count);
    }

    // Grpc.Net.ClientFactory: AddGrpcClient<T>().AddStandardAegisGrpcResilience().
    [Fact]
    public async Task ClientFactory_StandardResilience_RetriesUnavailable()
    {
        var services = new ServiceCollection();
        services.AddGrpcClient<TestClient>(o => o.Address = new Uri(_address))
            .AddStandardAegisGrpcResilience(o => o.Retry.Delay = TimeSpan.FromMilliseconds(1));
        await using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<TestClient>();

        Assert.Equal("ok", await client.CallAsync("unavailable-then-ok:2"));
    }

    // ---------------------------------------------------------------- sunucu

    [Fact]
    public void ServerInterceptor_Rejects_RetryPipeline() =>
        Assert.Throws<InvalidOperationException>(() =>
            new AegisGrpcServerInterceptor(new AegisPipelineBuilder("s").AddRetry().Build()));

    [Fact]
    public async Task ServerInterceptor_ConcurrencyLimit_ReturnsResourceExhausted()
    {
        var pipeline = new AegisPipelineBuilder("sunucu").AddConcurrencyLimiter(1).Build();
        var (app, address) = await StartServerAsync(new TestState(), o => o.Interceptors.Add<AegisGrpcServerInterceptor>(pipeline));
        await using var _ = app;
        var invoker = GrpcChannel.ForAddress(address).CreateCallInvoker();

        var holder = invoker.AsyncUnaryCall(TestGrpc.Unary, null, default, "slow-500").ResponseAsync;
        await Task.Delay(100);
        var rejected = await Assert.ThrowsAsync<RpcException>(() => invoker.AsyncUnaryCall(TestGrpc.Unary, null, default, "ok").ResponseAsync);

        Assert.Equal(StatusCode.ResourceExhausted, rejected.StatusCode);
        Assert.Equal("slow", await holder);
    }

    [Fact]
    public async Task ServerInterceptor_Timeout_ReturnsDeadlineExceeded()
    {
        var pipeline = new AegisPipelineBuilder("sunucu").AddTimeout(TimeSpan.FromMilliseconds(100)).Build();
        var (app, address) = await StartServerAsync(new TestState(), o => o.Interceptors.Add<AegisGrpcServerInterceptor>(pipeline));
        await using var _ = app;

        var error = await Assert.ThrowsAsync<RpcException>(() =>
            GrpcChannel.ForAddress(address).CreateCallInvoker().AsyncUnaryCall(TestGrpc.Unary, null, default, "slow-5000").ResponseAsync);

        Assert.Equal(StatusCode.DeadlineExceeded, error.StatusCode);
    }

    // ASP.NET Core gelen istek hız sınırı gRPC isteğine HTTP 429 değil gRPC yanıtı verir: ResourceExhausted + pushback.
    [Fact]
    public async Task InboundRateLimit_RespondsWithGrpcStatus_AndPushback()
    {
        var (app, address) = await StartServerAsync(new TestState(), configureApp: a => a.UseAegisInboundRateLimiting(),
            configureServices: s => s.AddAegisInboundRateLimiting(o => o.AddRule("*", 1, TimeSpan.FromMinutes(1))));
        await using var _ = app;
        var invoker = GrpcChannel.ForAddress(address).CreateCallInvoker();

        Assert.Equal("ok", await invoker.AsyncUnaryCall(TestGrpc.Unary, null, default, "ok").ResponseAsync);
        var rejected = await Assert.ThrowsAsync<RpcException>(() => invoker.AsyncUnaryCall(TestGrpc.Unary, null, default, "ok").ResponseAsync);

        Assert.Equal(StatusCode.ResourceExhausted, rejected.StatusCode);
        Assert.True(AegisGrpcTransientErrors.TryGetPushback(rejected.Trailers, out var pushback) && pushback > TimeSpan.Zero);
        Assert.Contains("Hız sınırı", rejected.Status.Detail); // yüzde kodlanmış Türkçe mesaj doğru çözülür
    }

    // Uçtan uca Aegis: sunucu hız sınırı ResourceExhausted + pushback döner; istemci bildirilen süre kadar bekleyip yeniden dener.
    [Fact]
    public async Task AegisClient_And_AegisServer_CooperateViaPushback()
    {
        var (app, address) = await StartServerAsync(new TestState(), configureApp: a => a.UseAegisInboundRateLimiting(),
            configureServices: s => s.AddAegisInboundRateLimiting(o => o.AddRule("*", 1, TimeSpan.FromMilliseconds(600))));
        await using var _ = app;
        var builder = new AegisPipelineBuilder("istemci");
        Retry(attempts: 3, delayMs: 1)(builder);
        var invoker = GrpcChannel.ForAddress(address).Intercept(new AegisGrpcClientInterceptor(builder.Build()));

        Assert.Equal("ok", await invoker.AsyncUnaryCall(TestGrpc.Unary, null, default, "ok").ResponseAsync);
        var watch = Stopwatch.StartNew();
        Assert.Equal("ok", await invoker.AsyncUnaryCall(TestGrpc.Unary, null, default, "ok").ResponseAsync); // kota dolu → pushback → geçer

        Assert.True(watch.ElapsedMilliseconds >= 200, $"pushback beklenmedi: {watch.ElapsedMilliseconds} ms");
    }

    // Pushback'siz ResourceExhausted yeniden denenmez (kota hemen dolmayabilir).
    [Fact]
    public void ResourceExhausted_WithoutPushback_IsNotRetried() =>
        Assert.False(AegisGrpcTransientErrors.ShouldRetry(new RpcException(new Status(StatusCode.ResourceExhausted, "kota"))));

    // Envoy outlier detection: Unavailable (trailer) dönen sunucu ayıklanır, trafik sağlıklı sunucuya gider. Grpc.Net.Client'ın
    // yerleşik yük dengeleyici geri bildirimi trailer'daki durumu görmez; Aegis görür.
    [Fact]
    public async Task OutlierDetection_EjectsUnhealthyEndpoint()
    {
        var healthy = new TestState();
        var broken = new TestState { Broken = true };
        var (healthyApp, healthyAddress) = await StartServerAsync(healthy);
        var (brokenApp, brokenAddress) = await StartServerAsync(broken);
        await using var _ = healthyApp;
        await using var __ = brokenApp;

        var services = new ServiceCollection();
        services.AddSingleton<global::Grpc.Net.Client.Balancer.ResolverFactory>(new global::Grpc.Net.Client.Balancer.StaticResolverFactory(_ =>
            [ToBalancerAddress(healthyAddress), ToBalancerAddress(brokenAddress)]));
        services.AddGrpcClient<TestClient>(o => o.Address = new Uri("static:///test"))
            .ConfigureChannel(o => o.Credentials = ChannelCredentials.Insecure)
            .AddAegisGrpcOutlierDetection(o => o.ConsecutiveFailures = 3);
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<TestClient>();

        var outcomes = new List<bool>();
        for (var i = 0; i < 30; i++)
        {
            try
            {
                await client.CallAsync("ok");
                outcomes.Add(true);
            }
            catch (RpcException)
            {
                outcomes.Add(false);
            }
        }

        Assert.Equal(3, broken.TotalCalls); // 3 art arda hatadan sonra ayıklandı, bir daha seçilmedi
        Assert.All(outcomes.Skip(10), Assert.True);
    }

    private static global::Grpc.Net.Client.Balancer.BalancerAddress ToBalancerAddress(string address)
    {
        var uri = new Uri(address);
        return new global::Grpc.Net.Client.Balancer.BalancerAddress(uri.Host, uri.Port);
    }

    // gRPC A6: istemci akışında gönderilen mesajlar tamponlanır ve yeniden denemede eksiksiz, sırasıyla baştan gönderilir.
    [Fact]
    public async Task ClientStreaming_RetriesAndReplaysBufferedMessages()
    {
        using var call = Client(Retry()).AsyncClientStreamingCall(TestGrpc.Collect, null, default);
        foreach (var message in new[] { "fail-once", "a", "b", "c" })
        {
            await call.RequestStream.WriteAsync(message);
        }

        await call.RequestStream.CompleteAsync();

        Assert.Equal("fail-once,a,b,c", await call.ResponseAsync);
        Assert.Equal(2, _state.Calls("collect:fail-once"));
    }

    // Token'lı yazma (önerilen kullanım) desteklenir: Grpc.Core'un varsayılan uygulaması NotSupportedException fırlatıyordu
    // (gerçek proje testinde bulundu). İptal edilmiş token'la mesaj gönderilmez.
    [Fact]
    public async Task ClientStreaming_WriteWithCancellationToken_Supported()
    {
        using var cts = new CancellationTokenSource();
        using var call = Client(Retry()).AsyncClientStreamingCall(TestGrpc.Collect, null, default);
        foreach (var message in new[] { "fail-once", "a", "b" })
        {
            await call.RequestStream.WriteAsync(message, cts.Token);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.RequestStream.WriteAsync("iptal", new CancellationToken(canceled: true)));
        await call.RequestStream.CompleteAsync();

        Assert.Equal("fail-once,a,b", await call.ResponseAsync);
    }

    // Protobuf'ın ürettiği marshaller yalnızca bağlamsal serileştiriciyi destekler; tampon boyutu onunla ölçülmeli
    // (önceden basit Serializer çağrılıyor ve NotImplementedException fırlıyordu — gerçek proje testinde bulundu).
    [Fact]
    public async Task ClientStreaming_ContextualOnlyMarshaller_BuffersAndReplays()
    {
        using var call = Client(Retry()).AsyncClientStreamingCall(TestGrpc.ContextualCollect, null, default);
        foreach (var message in new[] { "fail-once", "x", "y" })
        {
            await call.RequestStream.WriteAsync(message);
        }

        await call.RequestStream.CompleteAsync();

        Assert.Equal("fail-once,x,y", await call.ResponseAsync);
    }

    // Tampon sınırı aşılınca çağrı commit olur: yeniden oynatılamaz, hata çağırana yükselir (Grpc.Net.Client ile aynı).
    [Fact]
    public async Task ClientStreaming_BufferOverflow_Commits_NoRetry()
    {
        var builder = new AegisPipelineBuilder("küçük-tampon");
        Retry()(builder);
        var invoker = GrpcChannel.ForAddress(_address)
            .Intercept(new AegisGrpcClientInterceptor(builder.Build(), new AegisGrpcClientOptions { MaxRetryBufferBytes = 4 }));
        using var call = invoker.AsyncClientStreamingCall(TestGrpc.Collect, null, default);
        await call.RequestStream.WriteAsync("fail-once"); // 9 bayt > 4: commit
        await call.RequestStream.CompleteAsync();

        var error = await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync);
        Assert.Equal(StatusCode.Unavailable, error.StatusCode);
        Assert.Equal(1, _state.Calls("collect:fail-once"));
    }

    // Çift yönlü akış ilk yanıta kadar yeniden denenir; tampondaki mesajlar yeni denemeye baştan gönderilir.
    [Fact]
    public async Task Duplex_RetriesBeforeFirstResponse()
    {
        using var call = Client(Retry()).AsyncDuplexStreamingCall(TestGrpc.Chat, null, default);
        await call.RequestStream.WriteAsync("fail-once");

        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal("yankı:fail-once", call.ResponseStream.Current);
        await call.RequestStream.CompleteAsync();
        Assert.False(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(2, _state.Calls("chat:fail-once"));
    }

    // İlk yanıtla commit: sonraki hata yeniden denenmez.
    [Fact]
    public async Task Duplex_DoesNotRetry_AfterCommit()
    {
        using var call = Client(Retry()).AsyncDuplexStreamingCall(TestGrpc.Chat, null, default);
        await call.RequestStream.WriteAsync("fail-after-first");

        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        var error = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(StatusCode.Unavailable, error.StatusCode);
        Assert.Equal(1, _state.Calls("chat:fail-after-first"));
    }

    // Commit sonrası canlı mesajlar akmaya devam eder (etkileşimli çift yönlü akış).
    [Fact]
    public async Task Duplex_LiveMessagesFlow_AfterCommit()
    {
        using var call = Client(Retry()).AsyncDuplexStreamingCall(TestGrpc.Chat, null, default);
        foreach (var message in new[] { "merhaba", "dünya", "son" })
        {
            await call.RequestStream.WriteAsync(message);
            Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
            Assert.Equal("yankı:" + message, call.ResponseStream.Current);
        }

        await call.RequestStream.CompleteAsync();
        Assert.False(await call.ResponseStream.MoveNext(CancellationToken.None));
    }

    // ---------------------------------------------------------------- altyapı

    private static async Task<(WebApplication App, string Address)> StartServerAsync(
        TestState state,
        Action<global::Grpc.AspNetCore.Server.GrpcServiceOptions>? configureGrpc = null,
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configureApp = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc(o => { o.EnableDetailedErrors = true; configureGrpc?.Invoke(o); });
        builder.Services.AddSingleton(state);
        configureServices?.Invoke(builder.Services);
        var app = builder.Build();
        configureApp?.Invoke(app);
        app.MapGrpcService<TestService>();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, address);
    }

    private static Task<(WebApplication App, string Address)> StartServerAsync(
        TestState state, Action<global::Grpc.AspNetCore.Server.GrpcServiceOptions> configureGrpc) =>
        StartServerAsync(state, configureGrpc, configureServices: null, configureApp: null);

    public sealed class TestState
    {
        private readonly ConcurrentDictionary<string, int> _calls = new();
        private readonly ConcurrentDictionary<string, ConcurrentQueue<string?>> _headers = new();

        /// <summary>Sunucu bozuk: her unary çağrıya Unavailable (trailer) döner.</summary>
        public bool Broken { get; set; }

        public int TotalCalls => _calls.Values.Sum();

        public int Calls(string request) => _calls.GetValueOrDefault(request);

        public string?[] PreviousAttemptHeaders(string request) => _headers.TryGetValue(request, out var q) ? [.. q] : [];

        public int Record(string request, ServerCallContext context)
        {
            _headers.GetOrAdd(request, _ => new ConcurrentQueue<string?>())
                .Enqueue(context.RequestHeaders.GetValue(AegisGrpcMetadata.PreviousAttempts));
            return _calls.AddOrUpdate(request, 1, (_, n) => n + 1);
        }
    }

    public static class TestGrpc
    {
        private static readonly Marshaller<string> Text = Marshallers.Create(s => Encoding.UTF8.GetBytes(s), b => Encoding.UTF8.GetString(b));

        public static readonly Method<string, string> Unary = new(MethodType.Unary, "aegis.Test", nameof(TestService.Unary), Text, Text);

        public static readonly Method<string, string> Stream = new(MethodType.ServerStreaming, "aegis.Test", nameof(TestService.Stream), Text, Text);

        public static readonly Method<string, string> Collect = new(MethodType.ClientStreaming, "aegis.Test", nameof(TestService.Collect), Text, Text);

        public static readonly Method<string, string> Chat = new(MethodType.DuplexStreaming, "aegis.Test", nameof(TestService.Chat), Text, Text);

        // Grpc.Tools'un ürettiği protobuf kodu gibi YALNIZCA bağlamsal serileştirici: basit Serializer NotImplementedException fırlatır.
        private static readonly Marshaller<string> ContextualText = new(
            (s, ctx) =>
            {
                var bytes = Encoding.UTF8.GetBytes(s);
                ctx.SetPayloadLength(bytes.Length);
                var writer = ctx.GetBufferWriter();
                bytes.CopyTo(writer.GetSpan(bytes.Length));
                writer.Advance(bytes.Length);
                ctx.Complete();
            },
            ctx => Encoding.UTF8.GetString(System.Buffers.BuffersExtensions.ToArray(ctx.PayloadAsReadOnlySequence())));

        public static readonly Method<string, string> ContextualCollect = new(MethodType.ClientStreaming, "aegis.Test", nameof(TestService.Collect), ContextualText, ContextualText);
    }

    public sealed class TestClient(CallInvoker invoker) : ClientBase<TestClient>(invoker)
    {
        public Task<string> CallAsync(string request) => CallInvoker.AsyncUnaryCall(TestGrpc.Unary, null, default, request).ResponseAsync;

        protected override TestClient NewInstance(ClientBaseConfiguration configuration) => throw new NotSupportedException();
    }

    // Kod üretiminin kurduğu yapı: BindServiceMethod soyut tabana işaret eder, metotlar sanaldır, servis tabandan türer.
    [BindServiceMethod(typeof(TestServiceBase), nameof(BindService))]
    public abstract class TestServiceBase
    {
        public static void BindService(ServiceBinderBase binder, TestServiceBase? service)
        {
            binder.AddMethod(TestGrpc.Unary, service is null ? null! : new UnaryServerMethod<string, string>(service.Unary));
            binder.AddMethod(TestGrpc.Stream, service is null ? null! : new ServerStreamingServerMethod<string, string>(service.Stream));
            binder.AddMethod(TestGrpc.Collect, service is null ? null! : new ClientStreamingServerMethod<string, string>(service.Collect));
            binder.AddMethod(TestGrpc.Chat, service is null ? null! : new DuplexStreamingServerMethod<string, string>(service.Chat));
        }
    
        public abstract Task<string> Unary(string request, ServerCallContext context);
    
        public abstract Task Stream(string request, IServerStreamWriter<string> responses, ServerCallContext context);

    public abstract Task<string> Collect(IAsyncStreamReader<string> requests, ServerCallContext context);

    public abstract Task Chat(IAsyncStreamReader<string> requests, IServerStreamWriter<string> responses, ServerCallContext context);
    }
    
    public sealed class TestService(TestState state) : TestServiceBase
    {

        public override async Task<string> Unary(string request, ServerCallContext context)
        {
            var call = state.Record(request, context);
            if (state.Broken)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "bozuk sunucu"));
            }

            switch (request)
            {
                case "ok":
                    return "ok";
                case "invalid":
                    throw new RpcException(new Status(StatusCode.InvalidArgument, "geçersiz"));
                case "always-unavailable":
                    throw new RpcException(new Status(StatusCode.Unavailable, "yok"));
                case "pushback-no-retry":
                    throw new RpcException(new Status(StatusCode.Unavailable, "yok"), new Metadata { { AegisGrpcMetadata.RetryPushback, "-1" } });
                case "pushback-300-then-ok":
                    return call == 1
                        ? throw new RpcException(new Status(StatusCode.Unavailable, "bekle"), new Metadata { { AegisGrpcMetadata.RetryPushback, "300" } })
                        : "ok";
                case "hang-first-then-ok":
                    if (call == 1)
                    {
                        await Task.Delay(Timeout.Infinite, context.CancellationToken);
                    }

                    return "ok";
                case "slow-first":
                    if (call == 1)
                    {
                        await Task.Delay(5000, context.CancellationToken);
                        return "yavaş";
                    }

                    return "hızlı";
                case "slow-500":
                    await Task.Delay(500, context.CancellationToken);
                    return "slow";
                case "slow-5000":
                    await Task.Delay(5000, context.CancellationToken);
                    return "slow";
            }

            if (request.StartsWith("unavailable-then-ok:", StringComparison.Ordinal))
            {
                var failures = int.Parse(request["unavailable-then-ok:".Length..], System.Globalization.CultureInfo.InvariantCulture);
                return call <= failures ? throw new RpcException(new Status(StatusCode.Unavailable, "geçici")) : "ok";
            }

            throw new RpcException(new Status(StatusCode.Unimplemented, request));
        }

        // İstemci akışı: tüm mesajları toplar; ilk mesaj "fail-once" ise ilk denemede hepsini okuduktan sonra Unavailable döner.
        public override async Task<string> Collect(IAsyncStreamReader<string> requests, ServerCallContext context)
        {
            var messages = new List<string>();
            await foreach (var message in requests.ReadAllAsync(context.CancellationToken))
            {
                messages.Add(message);
            }

            var call = state.Record("collect:" + messages.FirstOrDefault(), context);
            return messages.FirstOrDefault() == "fail-once" && call == 1
                ? throw new RpcException(new Status(StatusCode.Unavailable, "geçici"))
                : string.Join(",", messages);
        }

        // Çift yönlü akış: her mesajı yansıtır. İlk mesaj "fail-once": ilk denemede yanıt vermeden Unavailable;
        // "fail-after-first": bir yanıt verip Unavailable (commit sonrası hata).
        public override async Task Chat(IAsyncStreamReader<string> requests, IServerStreamWriter<string> responses, ServerCallContext context)
        {
            if (!await requests.MoveNext(context.CancellationToken))
            {
                return;
            }

            var first = requests.Current;
            var call = state.Record("chat:" + first, context);
            if (first == "fail-once" && call == 1)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "geçici"));
            }

            await responses.WriteAsync("yankı:" + first);
            if (first == "fail-after-first")
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "commit sonrası"));
            }

            await foreach (var message in requests.ReadAllAsync(context.CancellationToken))
            {
                await responses.WriteAsync("yankı:" + message);
            }
        }

        public override async Task Stream(string request, IServerStreamWriter<string> responses, ServerCallContext context)
        {
            var call = state.Record(request, context);
            switch (request)
            {
                case "stream-fail-first" when call == 1:
                    throw new RpcException(new Status(StatusCode.Unavailable, "geçici"));
                case "stream-fail-after-first":
                    await responses.WriteAsync("1");
                    throw new RpcException(new Status(StatusCode.Unavailable, "commit sonrası"));
                case "stream-long":
                    for (var i = 1; i <= 5; i++)
                    {
                        await responses.WriteAsync(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        await Task.Delay(150, context.CancellationToken);
                    }

                    return;
            }

            foreach (var message in new[] { "1", "2", "3" })
            {
                await responses.WriteAsync(message);
            }
        }
    }
}
