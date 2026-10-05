// Native AOT duman testi: kütüphanelerin AOT ile derlenmiş bir uygulamada gerçekten çalıştığını doğrular.
// Her senaryo beklenen sonucu kontrol eder; başarısızlıkta çıkış kodu 1 olur.

using System.Net;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Aegis.Resilience.Extensions.Aspire;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.Http;
using Aegis.Resilience.Extensions.Telemetry;
using Aegis.Resilience.RateLimiting;
using Aegis.Resilience.Testing;
using Aegis.Resilience.AspNetCore;
using Aegis.Resilience.Distributed.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

var failures = 0;

async Task Check(string name, Func<Task<bool>> scenario)
{
    try
    {
        var ok = await scenario();
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
        failures += ok ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
        failures++;
    }
}

await Check("Standart zincir + retry + koşul oluşturucu", async () =>
{
    using var pipeline = new AegisPipelineBuilder("aot")
        .AddTimeout(TimeSpan.FromSeconds(5))
        .AddRetry(o =>
        {
            o.MaxRetryAttempts = 2;
            o.Delay = TimeSpan.Zero;
            o.ShouldHandleOutcome = new AegisPredicateBuilder().Handle<InvalidOperationException>().HandleResult<int>(r => r < 0);
        })
        .AddCircuitBreaker()
        .Build();
    var calls = 0;
    var result = await pipeline.ExecuteAsync(_ => ++calls switch
    {
        1 => throw new InvalidOperationException(),
        2 => ValueTask.FromResult(-1),
        _ => ValueTask.FromResult(42)
    });
    return result == 42 && calls == 3;
});

await Check("Tipli pipeline + senkron Execute + TState", () =>
{
    using var typed = new AegisPipelineBuilder("aot-typed").AddRetry().Build<string>();
    var ok = typed.Execute(static (_, s) => s + "!", "merhaba") == "merhaba!";
    return Task.FromResult(ok);
});

await Check("Hedging + fallback + kaos", async () =>
{
    using var pipeline = new AegisPipelineBuilder("aot-hedge")
        .AddFallback(o => o.FallbackAction = _ => ValueTask.FromResult<object?>("yedek"))
        .AddHedging(o => { o.MaxHedgedAttempts = 1; o.HedgingDelay = TimeSpan.FromMilliseconds(10); })
        .AddChaosFault(1.0, () => new TimeoutException())
        .Build();
    return await pipeline.ExecuteAsync(_ => ValueTask.FromResult("asil")) == "yedek";
});

await Check("Açık devre reddi (BrokenCircuitException)", async () =>
{
    using var pipeline = new AegisPipelineBuilder("aot-cb").AddCircuitBreaker(o => { o.MinimumThroughput = 2; o.FailureRatio = 0.5; }).Build();
    for (var i = 0; i < 2; i++)
    {
        try { await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()); } catch (InvalidOperationException) { }
    }

    try
    {
        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1));
        return false;
    }
    catch (BrokenCircuitException)
    {
        return true;
    }
});

await Check("Adaptive Concurrency (senkron hızlı yol, kapasite reddi, izin geri dönüşü)", async () =>
{
    var strategy = new Aegis.Resilience.Core.Strategies.RateLimiter.AdaptiveConcurrencyStrategy(new Aegis.Resilience.Core.Strategies.RateLimiter.AdaptiveConcurrencyOptions
    {
        MinConcurrency = 1, InitialConcurrency = 1, MaxConcurrency = 1, QueueTimeout = TimeSpan.Zero
    });
    using var pipeline = new AegisPipelineBuilder("aot-adaptive").AddStrategy(strategy).Build();

    // Senkron hızlı yol: sonuç döner, izin geri verilir.
    if (await pipeline.ExecuteAsync(_ => ValueTask.FromResult(7)) != 7 || strategy.ActiveExecutions != 0)
    {
        return false;
    }

    // Senkron hata: özgün istisna yükselir ve izin sızmaz.
    try { await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()); return false; }
    catch (InvalidOperationException) { }
    if (strategy.ActiveExecutions != 0)
    {
        return false;
    }

    // Async geri çağrı izni tutar; kapasite doluyken ikinci çağrı reddedilir; bitince izin geri gelir.
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var running = pipeline.ExecuteAsync(async _ => { await gate.Task; return 1; }).AsTask();
    try { await pipeline.ExecuteAsync(_ => ValueTask.FromResult(2)); return false; }
    catch (RateLimitRejectedException) { }

    gate.SetResult();
    return await running == 1
        && strategy.ActiveExecutions == 0
        && await pipeline.ExecuteAsync(_ => ValueTask.FromResult(3)) == 3;
});

await Check("System.Threading.RateLimiting köprüsü", async () =>
{
    using var pipeline = new AegisPipelineBuilder("aot-rl")
        .AddFixedWindowRateLimiter(new FixedWindowRateLimiterOptions { PermitLimit = 1, Window = TimeSpan.FromMinutes(1) })
        .Build();
    await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1));
    try
    {
        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1));
        return false;
    }
    catch (RateLimitRejectedException ex)
    {
        return ex.RetryAfter is not null;
    }
});

await Check("DI + ILogger telemetrisi + zenginleştirici + tanımlayıcı", async () =>
{
    var services = new ServiceCollection();
    services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
    services.AddAegisResilienceEnricher();
    services.AddAegisPipeline("aot-di", b => b.AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.Zero; }));
    services.AddAegisPipelines<string>((b, tenant, _) => b.AddRetry(o => o.MaxRetryAttempts = tenant == "vip" ? 5 : 1));
    await using var sp = services.BuildServiceProvider();

    var pipeline = sp.GetRequiredService<IAegisPipelineRegistry>().GetPipeline("aot-di");
    var calls = 0;
    await pipeline.ExecuteAsync(_ => ++calls == 1 ? throw new TimeoutException() : ValueTask.FromResult(1), new AegisContext { OperationKey = "aot" });

    var vip = sp.GetRequiredService<IAegisPipelineProvider<string>>().GetPipeline("vip");
    return calls == 2 && vip.GetPipelineDescriptor().GetOptions<RetryOptions>().MaxRetryAttempts == 5;
});

await Check("HTTP standart işleyici (yapılandırmadan, authority başına)", async () =>
{
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Http:Retry:MaxRetryAttempts"] = "2",
            ["Http:Retry:Delay"] = "00:00:00",
            ["Http:Retry:UseJitter"] = "false"
        })
        .Build();
    var responses = 0;
    var services = new ServiceCollection();
    services.AddHttpClient("aot-http")
        .AddStandardAegisHandler(config.GetSection("Http"), o => o.SelectPipelineByAuthority())
        .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(() => ++responses < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
    await using var sp = services.BuildServiceProvider();

    var response = await sp.GetRequiredService<IHttpClientFactory>().CreateClient("aot-http").GetAsync("https://api.local/x");
    return response.StatusCode == HttpStatusCode.OK && responses == 3;
});

await Check("HTTP DI bağlamlı işleyici (seçenek, yeniden yükleme, DisableRetryFor, son yanıt, RemoveAll)", async () =>
{
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Aot:MaxRetryAttempts"] = "1" })
        .Build();
    var responses = 0;
    var services = new ServiceCollection();
    services.Configure<AotRetrySettings>(config.GetSection("Aot"));
    services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(
        () => new StubHandler(() => { responses++; return HttpStatusCode.ServiceUnavailable; })));
    services.AddHttpClient("aot-context").AddAegisResilienceHandler((pipeline, context) =>
    {
        var settings = context.GetOptions<AotRetrySettings>();
        pipeline.AddRetry(o => { o.MaxRetryAttempts = settings.MaxRetryAttempts; o.Delay = TimeSpan.Zero; });
        context.EnableReloads<AotRetrySettings>();
        context.DisableRetryFor(HttpMethod.Delete);
        context.ReturnFinalResponse = true;
    });
    services.AddHttpClient("aot-raw").AddStandardAegisHandler(o => o.Retry.Delay = TimeSpan.Zero).RemoveAllAegisHandlers();
    await using var sp = services.BuildServiceProvider();
    var factory = sp.GetRequiredService<IHttpClientFactory>();

    var get = await factory.CreateClient("aot-context").GetAsync("https://api.local/x");  // 1 + 1 deneme
    config["Aot:MaxRetryAttempts"] = "2";
    config.Reload();
    await factory.CreateClient("aot-context").GetAsync("https://api.local/x");            // yeniden yüklendi: 1 + 2
    await factory.CreateClient("aot-context").DeleteAsync("https://api.local/x");         // DELETE: 1
    await factory.CreateClient("aot-raw").GetAsync("https://api.local/x");                // Aegis yok: 1
    return get.StatusCode == HttpStatusCode.ServiceUnavailable && responses == 2 + 3 + 1 + 1;
});

await Check("Aspire ServiceDefaults (AddAegisServiceDefaults: yapılandırma, sağlık kontrolü, parçaları kapatma)", async () =>
{
    var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Aegis:Http:Retry:MaxRetryAttempts"] = "1",
        ["Aegis:Http:Retry:Delay"] = "00:00:00",
        ["Aegis:Http:Retry:UseJitter"] = "false"
    });
    builder.AddAegisServiceDefaults();
    var responses = 0;
    builder.Services.AddHttpClient("aot-aspire").ConfigurePrimaryHttpMessageHandler(
        () => new StubHandler(() => { responses++; return HttpStatusCode.ServiceUnavailable; }));
    using var host = builder.Build();

    // Her HttpClient standart işleyiciyi alır ve ayar yapılandırma bölümünden bağlanır: 1 yeniden deneme = tam 2 çağrı.
    try
    {
        await host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("aot-aspire").GetAsync("https://api.local/x");
        return false;
    }
    catch (HttpRequestException)
    {
    }

    // Sağlık kontrolü kayıtlı ve çalışır (devre açık değil: Healthy).
    var health = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();

    // Parçalar tek tek kapatılabilir: işleyici ve sağlık kontrolü eklenmez.
    var plain = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
    plain.AddAegisServiceDefaults(o => { o.AddHttpResilience = false; o.AddHealthCheck = false; });
    var plainResponses = 0;
    plain.Services.AddHttpClient("aot-plain").ConfigurePrimaryHttpMessageHandler(
        () => new StubHandler(() => { plainResponses++; return HttpStatusCode.ServiceUnavailable; }));
    using var plainHost = plain.Build();
    using var plainResponse = await plainHost.Services.GetRequiredService<IHttpClientFactory>().CreateClient("aot-plain").GetAsync("https://api.local/x");

    return responses == 2
        && health.Status == HealthStatus.Healthy
        && plainResponses == 1 && plainResponse.StatusCode == HttpStatusCode.ServiceUnavailable
        && plainHost.Services.GetService<HealthCheckService>() is null;
});

await Check("OpenTelemetry metrikleri (AddAegisServiceDefaults → MeterProvider → Aegis ölçümleri dışa aktarılır)", async () =>
{
    // Aynı akış iki kez: AddMetrics açık host Aegis ölçümlerini dışa aktarır, kapalı host aktarmaz (AddMeter bağlantısının kanıtı).
    static async Task<List<OpenTelemetry.Metrics.Metric>> RunAsync(bool addMetrics)
    {
        var exported = new List<OpenTelemetry.Metrics.Metric>();
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddInMemoryExporter(exported));
        builder.AddAegisServiceDefaults(o =>
        {
            o.AddMetrics = addMetrics;
            o.AddHealthCheck = false;
            o.ConfigureHttp = http => { http.Retry.Delay = TimeSpan.Zero; http.Retry.UseJitter = false; };
        });
        var responses = 0;
        builder.Services.AddHttpClient("aot-otel").ConfigurePrimaryHttpMessageHandler(
            () => new StubHandler(() => ++responses < 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));

        using var host = builder.Build();
        await host.StartAsync();
        using var response = await host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("aot-otel").GetAsync("https://api.local/x");
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException("istek başarılı olmadı");
        }

        host.Services.GetRequiredService<OpenTelemetry.Metrics.MeterProvider>().ForceFlush();
        await host.StopAsync();
        return exported;
    }

    var withMetrics = await RunAsync(addMetrics: true);
    var withoutMetrics = await RunAsync(addMetrics: false);

    return withMetrics.Any(m => m.MeterName == "Aegis" && m.Name.StartsWith("aegis.", StringComparison.Ordinal))
        && !withoutMetrics.Any(m => m.MeterName == "Aegis");
});

await Check("OpenTelemetry izleri (AddAegisServiceDefaults AddTracing → TracerProvider → Aegis span'ı, span olayları)", async () =>
{
    // Aynı akış iki kez: AddTracing açık host Aegis span'ını dışa aktarır, kapalı host aktarmaz (AddSource bağlantısının kanıtı).
    static async Task<(string Token, List<System.Diagnostics.Activity> Spans)> RunAsync(bool addTracing)
    {
        var token = $"aot-trace-{Guid.NewGuid():N}";
        var exported = new List<System.Diagnostics.Activity>();
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddInMemoryExporter(exported));
        builder.AddAegisServiceDefaults(o => { o.AddTracing = addTracing; o.AddMetrics = false; o.AddHealthCheck = false; o.AddHttpResilience = false; });
        builder.Services.AddAegisPipeline(token, b => b.AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.Zero; o.UseJitter = false; }));

        using var host = builder.Build();
        await host.StartAsync();
        var pipeline = host.Services.GetRequiredService<IAegisPipelineRegistry>().GetPipeline(token);
        var failed = false; // bir kez hata verir, bir yeniden denemeyle başarılı olur
        await pipeline.ExecuteAsync(_ =>
        {
            if (!failed)
            {
                failed = true;
                throw new InvalidOperationException("geçici");
            }

            return ValueTask.FromResult(1);
        }, new AegisContext(CancellationToken.None, token) { OperationKey = "aot-islem" });

        host.Services.GetRequiredService<TracerProvider>().ForceFlush();
        await host.StopAsync();
        return (token, exported);
    }

    var (token, spans) = await RunAsync(addTracing: true);
    var (_, noSpans) = await RunAsync(addTracing: false);

    var span = spans.SingleOrDefault(s => s.OperationName == "Aegis " + token);
    return span is not null
        && span.Kind == System.Diagnostics.ActivityKind.Internal
        && Equals(span.GetTagItem("pipeline.name"), token)
        && Equals(span.GetTagItem("operation.key"), "aot-islem")
        && span.Status == System.Diagnostics.ActivityStatusCode.Unset            // yeniden denemeyle başarılı oldu
        && span.Events.Count(e => e.Name == "OnRetry") == 1                      // strateji olayı span olayı olarak geldi
        && !noSpans.Any(s => s.Source.Name == "Aegis");
});

await Check("OTLP dışa aktarıcı (Aegis ölçümleri HTTP/protobuf ile yerel alıcıya gerçekten ulaşır)", async () =>
{
    // Yerel OTLP alıcısı: gelen gövdeleri ve içerik türünü kaydeder (gerçek bir toplayıcının yerine geçer).
    var received = new System.Collections.Concurrent.ConcurrentQueue<(string Path, string? ContentType, byte[] Body)>();
    var receiverBuilder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
    receiverBuilder.WebHost.UseUrls("http://127.0.0.1:0");
    receiverBuilder.Logging.ClearProviders();
    await using var receiver = receiverBuilder.Build();
    receiver.MapPost("/v1/metrics", async (Microsoft.AspNetCore.Http.HttpContext c) =>
    {
        using var body = new MemoryStream();
        await c.Request.Body.CopyToAsync(body);
        received.Enqueue((c.Request.Path.Value ?? "", c.Request.ContentType, body.ToArray()));
        c.Response.ContentType = "application/x-protobuf";
        c.Response.StatusCode = 200;
    });
    await receiver.StartAsync();
    var endpoint = new Uri(new Uri(receiver.Urls.First()), "/v1/metrics");

    var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
    builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddOtlpExporter(o =>
    {
        o.Endpoint = endpoint;
        o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
    }));
    builder.AddAegisServiceDefaults(o =>
    {
        o.AddHealthCheck = false;
        o.ConfigureHttp = http => { http.Retry.Delay = TimeSpan.Zero; http.Retry.UseJitter = false; };
    });
    var responses = 0;
    builder.Services.AddHttpClient("aot-otlp").ConfigurePrimaryHttpMessageHandler(
        () => new StubHandler(() => ++responses < 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));

    using var host = builder.Build();
    await host.StartAsync();
    using var response = await host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("aot-otlp").GetAsync("https://api.local/x");
    if (response.StatusCode != HttpStatusCode.OK)
    {
        return false;
    }

    host.Services.GetRequiredService<OpenTelemetry.Metrics.MeterProvider>().ForceFlush(10_000);
    await host.StopAsync();

    // Alıcıya protobuf gövdeli bir POST ulaştı ve içinde Aegis meter'ının ölçüm adları var (protobuf dizeleri düz ASCII'dir).
    var marker = System.Text.Encoding.ASCII.GetBytes("aegis.");
    return received.Any(r => r.Path == "/v1/metrics"
        && r.ContentType is not null && r.ContentType.Contains("protobuf", StringComparison.OrdinalIgnoreCase)
        && r.Body.AsSpan().IndexOf(marker) >= 0);
});

// Gerçek OpenTelemetry Collector senaryoları (isteğe bağlı): AEGIS_SMOKE_COLLECTOR_HOST verilirse çalışır, verilmezse atlanır.
// Bkz. docs/TEST-INFRASTRUCTURE.md bölüm 6: Collector'ı başlatın, ardından imajı bu değişkenle koşun.
var collectorHost = Environment.GetEnvironmentVariable("AEGIS_SMOKE_COLLECTOR_HOST");
if (collectorHost is null)
{
    Console.WriteLine("SKIP  Gerçek Collector senaryoları (AEGIS_SMOKE_COLLECTOR_HOST verilmedi)");
}
else
{
    // Collector günlükleri buraya OTLP/HTTP ile yeniden iletir (tests/docker/otel-collector.yaml → otlphttp/applogs).
    var logPort = Environment.GetEnvironmentVariable("AEGIS_SMOKE_LOG_PORT") ?? "5555";
    var logBodies = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
    var logReceiverBuilder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
    logReceiverBuilder.WebHost.UseUrls($"http://0.0.0.0:{logPort}");
    logReceiverBuilder.Logging.ClearProviders();
    await using var logReceiver = logReceiverBuilder.Build();
    logReceiver.MapPost("/v1/logs", async (Microsoft.AspNetCore.Http.HttpContext c) =>
    {
        using var body = new MemoryStream();
        await c.Request.Body.CopyToAsync(body);
        logBodies.Enqueue(body.ToArray());
        c.Response.ContentType = "application/x-protobuf";
        c.Response.StatusCode = 200;
    });
    var traceBodies = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
    logReceiver.MapPost("/v1/traces", async (Microsoft.AspNetCore.Http.HttpContext c) =>
    {
        using var body = new MemoryStream();
        await c.Request.Body.CopyToAsync(body);
        traceBodies.Enqueue(body.ToArray());
        c.Response.ContentType = "application/x-protobuf";
        c.Response.StatusCode = 200;
    });
    await logReceiver.StartAsync();

    var promUrl = $"http://{collectorHost}:8889/metrics";
    var protocols = new (string Name, OpenTelemetry.Exporter.OtlpExportProtocol Protocol, string Metrics, string Logs, string Traces)[]
    {
        ("gRPC", OpenTelemetry.Exporter.OtlpExportProtocol.Grpc, $"http://{collectorHost}:4317", $"http://{collectorHost}:4317", $"http://{collectorHost}:4317"),
        ("HTTP/protobuf", OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf, $"http://{collectorHost}:4318/v1/metrics",
            $"http://{collectorHost}:4318/v1/logs", $"http://{collectorHost}:4318/v1/traces")
    };

    foreach (var (protocolName, protocol, metricsEndpoint, logsEndpoint, tracesEndpoint) in protocols)
    {
        await Check($"Gerçek Collector, {protocolName}: ölçüm adı + değer + etiket (Prometheus kazıması), günlük ve iz", async () =>
        {
            // Her protokol kendi benzersiz boru hattı adını kullanır: ölçüm etiketi ve günlük gövdesi tek başına ona ait olur.
            var token = $"aot-{(protocol == OpenTelemetry.Exporter.OtlpExportProtocol.Grpc ? "grpc" : "http")}-{Guid.NewGuid():N}";
            const int calls = 7;

            var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
            builder.Logging.SetMinimumLevel(LogLevel.Information);
            builder.Services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService("aegis-aot-smoke"))
                .WithMetrics(metrics => metrics.AddOtlpExporter(o => { o.Endpoint = new Uri(metricsEndpoint); o.Protocol = protocol; }))
                .WithTracing(tracing => tracing.AddOtlpExporter(o => { o.Endpoint = new Uri(tracesEndpoint); o.Protocol = protocol; }))
                .WithLogging(
                    logging => logging.AddOtlpExporter(o => { o.Endpoint = new Uri(logsEndpoint); o.Protocol = protocol; }),
                    options => options.IncludeFormattedMessage = true);
            builder.AddAegisServiceDefaults(o => { o.AddHttpResilience = false; o.AddHealthCheck = false; });
            builder.Services.AddAegisPipeline(token, b => b.AddRetry(o => { o.MaxRetryAttempts = 1; o.Delay = TimeSpan.Zero; o.UseJitter = false; }));

            using var host = builder.Build();
            await host.StartAsync();
            var pipeline = host.Services.GetRequiredService<IAegisPipelineRegistry>().GetPipeline(token);
            for (var i = 0; i < calls; i++)
            {
                var failed = false; // her çağrı bir kez hata verir, bir yeniden denemeyle başarılı olur
                await pipeline.ExecuteAsync(_ =>
                {
                    if (!failed)
                    {
                        failed = true;
                        throw new InvalidOperationException("geçici");
                    }

                    return ValueTask.FromResult(1);
                });
            }

            host.Services.GetRequiredService<OpenTelemetry.Metrics.MeterProvider>().ForceFlush(10_000);
            host.Services.GetRequiredService<OpenTelemetry.Logs.LoggerProvider>().ForceFlush(10_000);
            host.Services.GetRequiredService<TracerProvider>().ForceFlush(10_000);
            await host.StopAsync();

            // Ölçümler: Collector'ın Prometheus uç noktasını kazı, "aegis_executions_total" ve "aegis_retry_attempts_total"
            // için bu boru hattının etiketli satırını bul, değerini ayrıştır.
            double? executions = null, retries = null;
            using var http = new HttpClient();
            for (var attempt = 0; attempt < 20 && (executions is null || retries is null); attempt++)
            {
                var text = await http.GetStringAsync(promUrl);
                foreach (var line in text.Split('\n'))
                {
                    if (!line.Contains($"pipeline=\"{token}\"", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var value = double.Parse(line[(line.LastIndexOf(' ') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                    if (line.StartsWith("aegis_executions_total{", StringComparison.Ordinal))
                    {
                        executions = value;
                    }
                    else if (line.StartsWith("aegis_retry_attempts_total{", StringComparison.Ordinal))
                    {
                        retries = value;
                    }
                }

                if (executions is null || retries is null)
                {
                    await Task.Delay(500);
                }
            }

            // Günlükler: Collector'ın yeniden ilettiği gövdelerden birinde boru hattı adı ve PipelineExecuted iletisi var mı.
            var tokenBytes = System.Text.Encoding.ASCII.GetBytes(token);
            var messageBytes = System.Text.Encoding.ASCII.GetBytes("Resilience pipeline executed");
            var logFound = false;
            for (var attempt = 0; attempt < 20 && !logFound; attempt++)
            {
                logFound = logBodies.Any(b => b.AsSpan().IndexOf(tokenBytes) >= 0 && b.AsSpan().IndexOf(messageBytes) >= 0);
                if (!logFound)
                {
                    await Task.Delay(500);
                }
            }

            // İzler: Collector'ın yeniden ilettiği gövdelerden birinde span adı ("Aegis <ad>") ve OnRetry span olayı var mı.
            var retryEventBytes = System.Text.Encoding.ASCII.GetBytes("OnRetry");
            var traceFound = false;
            for (var attempt = 0; attempt < 20 && !traceFound; attempt++)
            {
                traceFound = traceBodies.Any(b => b.AsSpan().IndexOf(tokenBytes) >= 0 && b.AsSpan().IndexOf(retryEventBytes) >= 0);
                if (!traceFound)
                {
                    await Task.Delay(500);
                }
            }

            Console.WriteLine($"      {protocolName}: executions={executions}, retries={retries}, günlük={logFound}, iz={traceFound}");
            return executions == calls && retries == calls && logFound && traceFound;
        });
    }
}

await Check("Dağıtık cache (kaynak üreticili JSON) + dağıtık hız sınırlayıcı + art arda hata devresi", async () =>
{
    var cache = new Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache(
        Microsoft.Extensions.Options.Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryDistributedCacheOptions()));
    using var pipeline = new AegisPipelineBuilder("aot-ekosistem")
        .AddCache(o =>
        {
            o.KeySelector = _ => "k";
            o.Store = new Aegis.Resilience.Extensions.Caching.DistributedCacheStore(
                cache, new Aegis.Resilience.Extensions.Caching.SystemTextJsonCacheSerializer(AotJsonContext.Default.Options));
        })
        .AddDistributedRateLimiter(new Aegis.Resilience.Distributed.Abstractions.InMemoryDistributedRateLimitStore(), o => o.PermitLimit = 1)
        .AddCircuitBreaker(o => o.ConsecutiveFailureThreshold = 3)
        .Build();
    var calls = 0;

    var first = await pipeline.ExecuteAsync(_ => new ValueTask<AotUrun>(new AotUrun { Ad = "kalem", Adet = ++calls }));
    var second = await pipeline.ExecuteAsync(_ => new ValueTask<AotUrun>(new AotUrun { Ad = "silgi", Adet = ++calls }));
    return first.Ad == "kalem" && second.Ad == "kalem" && calls == 1; // ikinci çağrı önbellekten: sınırlayıcıya hiç ulaşmadı
});

await Check("ASP.NET Core: gelen istek hız sınırı + uç nokta boru hattı (Kestrel)", async () =>
{
    var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
    builder.Services.AddAegisInboundRateLimiting(o => o.AddRule("GET:/api/*", 2, TimeSpan.FromMinutes(1)));
    builder.Services.AddAegisPipeline("rapor", b => b.AddTimeout(TimeSpan.FromMilliseconds(100)));
    await using var app = builder.Build();
    app.UseAegisInboundRateLimiting();
    app.UseRouting();
    app.UseAegisInboundPipelines();
    app.MapGet("/api/urun", () => "urun");
    app.MapGet("/rapor", async (Microsoft.AspNetCore.Http.HttpContext c) => { await Task.Delay(5000, c.RequestAborted); return "gec"; })
        .RequireAegisPipeline("rapor");
    await app.StartAsync();

    var address = app.Urls.First();
    using var client = new HttpClient { BaseAddress = new Uri(address) };
    var statuses = new List<HttpStatusCode>();
    for (var i = 0; i < 3; i++)
    {
        statuses.Add((await client.GetAsync("/api/urun")).StatusCode);
    }

    var timeout = (await client.GetAsync("/rapor")).StatusCode;
    await app.StopAsync();
    return statuses.SequenceEqual([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]) &&
           timeout == HttpStatusCode.GatewayTimeout;
});

Console.WriteLine(failures == 0 ? "AOT DUMAN TESTI: TUMU GECTI" : $"AOT DUMAN TESTI: {failures} BASARISIZ");
return failures == 0 ? 0 : 1;

internal sealed class StubHandler(Func<HttpStatusCode> status) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(status()));
}

internal sealed class AotRetrySettings
{
    public int MaxRetryAttempts { get; set; }
}

internal sealed class AotUrun
{
    public string Ad { get; set; } = "";

    public int Adet { get; set; }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(AotUrun))]
internal sealed partial class AotJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
