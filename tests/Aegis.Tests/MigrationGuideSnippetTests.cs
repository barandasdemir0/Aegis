using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Extensions.Http;

namespace Aegis.Tests;

/// <summary>docs/MIGRATION.md içindeki Aegis örnekleri: rehber derlenen ve çalışan kodla eşit kalsın.</summary>
public class MigrationGuideSnippetTests
{
    [Fact]
    public async Task Basic_Pipeline()
    {
        // --- rehber: 2. Temel boru hattı ---
        var pipeline = new AegisPipelineBuilder("odeme")
            .AddTimeout(TimeSpan.FromSeconds(10))
            .AddRetry(o =>
            {
                o.MaxRetryAttempts = 3;
                o.Delay = TimeSpan.FromMilliseconds(200);
                o.BackoffType = DelayBackoffType.Exponential;
                o.UseJitter = true;
                o.ShouldHandleOutcome = new AegisPredicateBuilder()
                    .Handle<HttpRequestException>()
                    .HandleResult<HttpResponseMessage>(r => r.StatusCode == HttpStatusCode.ServiceUnavailable);
                o.OnRetry = args =>
                {
                    Console.WriteLine($"Deneme {args.AttemptNumber}, bekleme {args.RetryDelay}");
                    return default;
                };
            })
            .AddCircuitBreaker(o =>
            {
                o.FailureRatio = 0.5;
                o.SamplingDuration = TimeSpan.FromSeconds(30);
                o.MinimumThroughput = 10;
                o.BreakDuration = TimeSpan.FromSeconds(15);
                o.OnOpened = args => { Console.WriteLine($"Devre açıldı: {args.BreakDuration}"); return default; };
            })
            .Build();

        // --- rehber: 3. Çalıştırma ---
        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        var a = await pipeline.ExecuteAsync(static async ct => { await Task.Yield(); return 1; }, token);
        var b = await pipeline.ExecuteAsync(static (state, ct) => new ValueTask<int>(state + 1), 41, token);
        var outcome = await pipeline.ExecuteOutcomeAsync(ctx => new ValueTask<int>(7));
        var c = pipeline.Execute(() => 3);
        var context = new AegisContext(token);
        var d = await pipeline.ExecuteAsync(ctx => new ValueTask<int>(4), context);

        Assert.Equal(1, a);
        Assert.Equal(42, b);
        Assert.True(outcome.IsSuccess);
        Assert.Equal(3, c);
        Assert.Equal(4, d);
        pipeline.Dispose();
    }

    [Fact]
    public void Exceptions_Map()
    {
        // --- rehber: 6. İstisnalar ---
        Assert.True(typeof(AegisException).IsAssignableFrom(typeof(BrokenCircuitException)));
        Assert.True(typeof(BrokenCircuitException).IsAssignableFrom(typeof(IsolatedCircuitException)));
        _ = typeof(AegisTimeoutException);
        _ = typeof(RateLimitRejectedException);
    }

    [Fact]
    public void Manual_Control_And_Typed_Pipeline()
    {
        // --- rehber: 4. Devre kesici elle kontrol + tipli boru hattı ---
        var manual = new CircuitBreakerManualControl();
        var typed = new AegisPipelineBuilder("tipli")
            .AddCircuitBreaker(o => o.ManualControl = manual)
            .Build<string>();
        Assert.NotNull(typed);
    }

    [Fact]
    public void Di_And_Http()
    {
        // --- rehber: 5. DI ve HttpClient ---
        var services = new ServiceCollection();
        services.AddAegisPipeline("odeme", builder => builder
            .AddRetry(o => o.MaxRetryAttempts = 3)
            .AddTimeout(TimeSpan.FromSeconds(5)));
        services.AddHttpClient("katalog").AddStandardAegisHandler(o =>
        {
            o.Retry.MaxRetryAttempts = 5;
            o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(2);
        });

        using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<IAegisPipelineRegistry>().GetPipeline("odeme");
        Assert.NotNull(pipeline);
    }
}
