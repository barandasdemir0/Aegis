using System.Net;
using Aegis.Resilience.AspNetCore;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Distributed.Abstractions;
using Aegis.Resilience.Distributed.Redis;
using Aegis.Resilience.Extensions.Aspire;
using Aegis.Resilience.Extensions.Dashboard;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.HealthChecks;
using Aegis.Resilience.Extensions.Http;
using Aegis.Resilience.Grpc;
using Aegis.Resilience.Grpc.AspNetCore;
using Aegis.Resilience.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Aegis.Tests;

/// <summary>
/// README.md'deki kod örnekleri: README derlenen ve çalışan kodla eşit kalsın (yanlış API adı ya da parametre adı derlemeyi kırar).
/// Her örnek README'dekiyle aynıdır; yalnızca dış servisler (ödeme, Redis adresi) test eşdeğerleriyle değiştirilmiştir.
/// </summary>
public sealed class ReadmeSnippetTests
{
    // --- README: tanıtım örneği
    [Fact]
    public async Task Intro()
    {
        var pipeline = new AegisPipelineBuilder("odeme")
            .AddTimeout(TimeSpan.FromSeconds(10))
            .AddRetry(o => o.MaxRetryAttempts = 3)
            .AddCircuitBreaker()
            .Build();

        var sonuc = await pipeline.ExecuteAsync(ct => ValueTask.FromResult("ok"), CancellationToken.None);
        Assert.Equal("ok", sonuc);
    }

    // --- README: Hızlı başlangıç 1
    [Fact]
    public async Task QuickStart_Pipeline()
    {
        var pipeline = new AegisPipelineBuilder("siparis")
            .AddTimeout(TimeSpan.FromSeconds(30))
            .AddConcurrencyLimiter(maxConcurrent: 50)
            .AddRetry(o =>
            {
                o.MaxRetryAttempts = 3;
                o.BackoffType = DelayBackoffType.DecorrelatedJitter;
                o.Delay = TimeSpan.FromMilliseconds(200);
            })
            .AddCircuitBreaker(o => { o.FailureRatio = 0.5; o.MinimumThroughput = 10; o.BreakDuration = TimeSpan.FromSeconds(15); })
            .AddTimeout(TimeSpan.FromSeconds(5))
            .Build();

        Assert.Equal(42, await pipeline.ExecuteAsync(ct => ValueTask.FromResult(42), CancellationToken.None));
        Assert.Equal(["Timeout", "ConcurrencyLimiter", "Retry", "CircuitBreaker", "Timeout"], pipeline.GetPipelineDescriptor().Strategies.Select(s => s.Name));
    }

    // --- README: Hızlı başlangıç 2-4, sağlık kontrolü, sunucu tarafı koruma, pano: gerçek uygulama olarak başlatılır
    [Fact]
    public async Task QuickStart_DiHttpHealthDashboardAspire_AppStartsAndServes()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.AddAegisServiceDefaults();                                            // 4. Aspire
        builder.Services.AddAegisPipeline("stok", p => p.AddRetry().AddCircuitBreaker()); // 2. DI
        builder.Services.AddHttpClient("odeme", c => c.BaseAddress = new Uri("https://odeme"))
            .AddStandardAegisHandler(o =>
            {
                o.Retry.MaxRetryAttempts = 3;
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);
            });

        // Sağlık kontrolü bölümü (canlılık + hazır olma); Aspire varsayılan kontrolü zaten ekledi.
        builder.Services.AddHealthChecks()
            .AddAegisCheck("aegis_ready", tags: ["ready"],
                configureOptions: o => o.OpenCircuitStatus = HealthStatus.Unhealthy);

        // Sunucu tarafı koruma bölümü
        builder.Services.AddAegisInboundRateLimiting(o => o.PartitionByHeader("X-ClientId"));
        builder.Services.AddAegisPipeline("rapor", p => p.AddConcurrencyLimiter(20).AddTimeout(TimeSpan.FromSeconds(10)));

        await using var app = builder.Build();
        app.UseAegisInboundRateLimiting();
        app.UseRouting();
        app.UseAegisInboundPipelines();
        app.MapGet("/rapor/yillik", () => "rapor").RequireAegisPipeline("rapor");
        app.MapHealthChecks("/health", new() { Predicate = r => r.Name != "aegis_ready" });
        app.MapHealthChecks("/health/ready", new() { Predicate = r => r.Name == "aegis_ready" });
        app.MapAegisDashboard("/aegis");                                              // 3. pano (politikasız: yalnız yerelden müdahale)
        app.MapAegisStatus("/ops/aegis-status");
        await app.StartAsync();

        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()) };
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/rapor/yillik")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Contains("\"pipelines\"", await client.GetStringAsync("/aegis/status"));
        Assert.Contains("\"totalPipelines\"", await client.GetStringAsync("/ops/aegis-status"));
        Assert.Contains("Aegis Resilience Dashboard", await client.GetStringAsync("/aegis"));
        await app.StopAsync();
    }

    // --- README: gRPC ve Dağıtık durum bölümleri (kayıt ve çözümleme; Redis bağlantısı arka planda kurulur, beklenmez)
    [Fact]
    public void GrpcAndRedis_Registrations_Resolve()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAegis();
        services.AddGrpcClient<GrpcEndToEndTests.TestClient>(o => o.Address = new Uri("https://stok"))
            .AddStandardAegisGrpcResilience(o => o.Retry.Budget = new RetryBudget())
            .AddAegisGrpcOutlierDetection();
        services.AddGrpc(o => o.AddAegisResilience(p => p.AddConcurrencyLimiter(200).AddTimeout(TimeSpan.FromSeconds(5))));

        services.AddAegisRedisStateStore("127.0.0.1:1");
        services.AddAegisRedisRateLimitStore("127.0.0.1:1");
        services.AddAegisPipeline("odeme", (p, sp) => p
            .AddDistributedRateLimiter(sp.GetRequiredService<IDistributedRateLimitStore>(), o => { o.LimiterKey = "stripe"; o.PermitLimit = 50; })
            .AddDistributedCircuitBreaker(sp.GetRequiredService<ICircuitBreakerStateStore>(), o => o.CircuitKey = "odeme"));

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<GrpcEndToEndTests.TestClient>());
        Assert.Equal(["DistributedRateLimiter", "DistributedCircuitBreaker"],
            provider.GetRequiredService<IAegisPipelineRegistry>().GetPipeline("odeme").GetPipelineDescriptor().Strategies.Select(s => s.Name));
    }

    // --- README: Test yazma bölümü
    [Fact]
    public async Task Testing_FakeClock()
    {
        var saat = new FakeTimeProvider();
        var p = new AegisPipelineBuilder("test").WithTimeProvider(saat)
            .AddCircuitBreaker(o => { o.ConsecutiveFailureThreshold = 2; o.BreakDuration = TimeSpan.FromHours(1); })
            .Build();

        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => p.ExecuteAsync<int>(_ => throw new InvalidOperationException()).AsTask());
        }

        await Assert.ThrowsAsync<Aegis.Resilience.Core.Exceptions.BrokenCircuitException>(() => p.ExecuteAsync(_ => ValueTask.FromResult(1)).AsTask());
        saat.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await p.ExecuteAsync(_ => ValueTask.FromResult(1)));
    }
}
