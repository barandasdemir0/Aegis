using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Core.Strategies.Timeout;
using Aegis.Resilience.Extensions.DependencyInjection;
using Aegis.Resilience.Testing;

namespace Aegis.Tests;

/// <summary>
/// Aşama 4 — Polly registry / reload / Polly.Testing eşitliği.
/// </summary>
public class RegistryReloadTestingStage4Tests
{
    private sealed record EndpointKey(string Service, string Version);

    // ---------------------------------------------------------------- Generic / dinamik registry

    [Fact]
    public async Task KeyedRegistry_StaticAndDynamicKeys_EachKeyGetsOwnStrategies()
    {
        using var registry = new AegisPipelineRegistry<EndpointKey>(nameFormatter: k => $"{k.Service}-{k.Version}")
        {
            DynamicBuilder = (b, _) => b.AddCircuitBreaker(o => { o.MinimumThroughput = 2; o.FailureRatio = 0.5; o.BreakDuration = TimeSpan.FromMinutes(1); })
        };
        registry.TryAddBuilder(new EndpointKey("odeme", "v1"), (b, _) => b.AddRetry(o => o.MaxRetryAttempts = 1));

        var payment = registry.GetPipeline(new EndpointKey("odeme", "v1"));
        Assert.Equal("odeme-v1", payment.Name);
        Assert.IsType<RetryStrategy>(Assert.Single(payment.Strategies));
        Assert.Same(payment, registry.GetPipeline(new EndpointKey("odeme", "v1"))); // aynı örnek

        var tenantA = registry.GetPipeline(new EndpointKey("stok", "kiraci-a"));
        var tenantB = registry.GetPipeline(new EndpointKey("stok", "kiraci-b"));
        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await tenantA.ExecuteAsync<int>(_ => throw new InvalidOperationException()));
        }

        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await tenantA.ExecuteAsync(_ => ValueTask.FromResult(1)));
        Assert.Equal(1, await tenantB.ExecuteAsync(_ => ValueTask.FromResult(1))); // kiracı B etkilenmez
    }

    [Fact]
    public void KeyedRegistry_DynamicCardinalityLimit_ProtectsMemory()
    {
        using var registry = new AegisPipelineRegistry<int> { DynamicBuilder = (b, _) => b.AddRetry(), MaxDynamicPipelines = 3 };

        for (var i = 0; i < 3; i++)
        {
            registry.GetPipeline(i);
        }

        Assert.Throws<InvalidOperationException>(() => registry.GetPipeline(99));
        Assert.Throws<KeyNotFoundException>(() => new AegisPipelineRegistry<int>().GetPipeline(1));
    }

    [Fact]
    public async Task DependencyInjection_KeyedAndDynamicPipelines()
    {
        var services = new ServiceCollection();
        services.AddAegisPipeline(new EndpointKey("odeme", "v1"), (b, _, _) => b.AddTimeout(TimeSpan.FromSeconds(5)));
        services.AddAegisPipelines<string>((b, tenant, _) => b.AddRetry(o => o.MaxRetryAttempts = tenant == "vip" ? 5 : 1), maxDynamicPipelines: 100);
        await using var sp = services.BuildServiceProvider();

        var endpoints = sp.GetRequiredService<IAegisPipelineProvider<EndpointKey>>();
        Assert.IsType<TimeoutStrategy>(Assert.Single(endpoints.GetPipeline(new EndpointKey("odeme", "v1")).Strategies));

        var tenants = sp.GetRequiredService<IAegisPipelineProvider<string>>();
        Assert.Equal(5, tenants.GetPipeline("vip").GetPipelineDescriptor().GetOptions<RetryOptions>().MaxRetryAttempts);
        Assert.Equal(1, tenants.GetPipeline("standart").GetPipelineDescriptor().GetOptions<RetryOptions>().MaxRetryAttempts);
    }

    // ---------------------------------------------------------------- Yeniden yükleme

    [Fact]
    public async Task Reload_NewCallsUseNewGeneration_InFlightCallsFinishOnOld_OldDisposedAfter()
    {
        var generation = 0;
        var disposedGenerations = new List<int>();
        using var pipeline = new ReloadableAegisPipeline("reload", () =>
        {
            var current = ++generation;
            return new AegisPipelineBuilder("reload").AddStrategy(new TrackingStrategy(current, disposedGenerations)).Build();
        });

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = pipeline.ExecuteAsync(async ctx => { await gate.Task; return ctx.GetPropertyOrDefault(TrackingStrategy.Key, 0); }).AsTask();

        Assert.True(pipeline.Reload());
        Assert.Equal(2, await pipeline.ExecuteAsync(ctx => ValueTask.FromResult(ctx.GetPropertyOrDefault(TrackingStrategy.Key, 0))));
        Assert.Empty(disposedGenerations); // 1. nesil hâlâ uçuşta: dispose edilmedi

        gate.SetResult();
        Assert.Equal(1, await inFlight); // uçuştaki çağrı eski nesille tamamlandı
        Assert.Equal([1], disposedGenerations);
        Assert.Equal(1, pipeline.ReloadCount);
    }

    [Fact]
    public async Task Reload_FactoryFailure_KeepsOldGeneration()
    {
        var fail = false;
        using var pipeline = new ReloadableAegisPipeline("reload-fail", () =>
            fail ? throw new FormatException("hatalı yapılandırma") : new AegisPipelineBuilder("reload-fail").AddRetry().Build());

        fail = true;
        Assert.False(pipeline.Reload());
        Assert.IsType<FormatException>(pipeline.LastReloadError);
        Assert.Equal(1, await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1)));
    }

    private sealed class ResilienceSettings
    {
        public int Retries { get; set; } = 1;
    }

    private sealed class ManualChangeTokenSource : IOptionsChangeTokenSource<ResilienceSettings>
    {
        private CancellationTokenSource _cts = new();

        public string? Name => Options.DefaultName;

        public IChangeToken GetChangeToken() => new CancellationChangeToken(_cts.Token);

        public void Trigger()
        {
            var previous = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
            previous.Cancel();
        }
    }

    [Fact]
    public async Task DependencyInjection_ReloadsWholePipeline_WhenOptionsChange()
    {
        var settings = new ResilienceSettings();
        var changeSource = new ManualChangeTokenSource();
        var services = new ServiceCollection();
        services.AddSingleton<IOptionsChangeTokenSource<ResilienceSettings>>(changeSource);
        services.Configure<ResilienceSettings>(o => o.Retries = settings.Retries);
        services.AddAegisPipeline<ResilienceSettings>("reload-di", (b, o, _) => b.AddRetry(r => r.MaxRetryAttempts = o.Retries));
        await using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetRequiredService<IAegisPipelineRegistry>().GetPipeline("reload-di");
        var descriptor = pipeline.GetPipelineDescriptor();
        Assert.True(descriptor.IsReloadable);
        Assert.Equal(1, descriptor.GetOptions<RetryOptions>().MaxRetryAttempts);

        settings.Retries = 4;
        changeSource.Trigger();

        Assert.Equal(4, pipeline.GetPipelineDescriptor().GetOptions<RetryOptions>().MaxRetryAttempts);
    }

    // ---------------------------------------------------------------- Testing tanımlayıcıları

    [Fact]
    public void Descriptor_ListsStrategiesInOrder_WithOptions_FlatteningComposition()
    {
        using var shared = new AegisPipelineBuilder("ortak").AddCircuitBreaker(o => o.FailureRatio = 0.3).Build();
        using var pipeline = new AegisPipelineBuilder("tanim")
            .AddTimeout(TimeSpan.FromSeconds(3))
            .AddRetry(o => o.MaxRetryAttempts = 7)
            .AddPipeline(shared)
            .Build<string>();

        var descriptor = pipeline.GetPipelineDescriptor();

        Assert.Equal(["Timeout", "Retry", "CircuitBreaker"], descriptor.Strategies.Select(s => s.Name));
        Assert.Equal("Timeout", descriptor.FirstStrategy.Name);
        Assert.Equal(TimeSpan.FromSeconds(3), descriptor.GetOptions<TimeoutOptions>().Timeout);
        Assert.Equal(7, descriptor.GetOptions<RetryOptions>().MaxRetryAttempts);
        Assert.Equal(0.3, descriptor.GetOptions<CircuitBreakerOptions>().FailureRatio);
        Assert.False(descriptor.IsReloadable);
    }

    /// <summary>Hangi nesilde çalıştığını bağlama yazan ve dispose'unu kaydeden test stratejisi.</summary>
    private sealed class TrackingStrategy(int generation, List<int> disposed) : AegisStrategy, IDisposable
    {
        public static readonly Aegis.Resilience.Core.Context.AegisPropertyKey<int> Key = new("generation");

        public override string Name => "Tracking";

        protected override ValueTask<Outcome<TResult>> ExecuteCoreAsync<TResult, TState>(
            Func<Aegis.Resilience.Core.Context.AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
            Aegis.Resilience.Core.Context.AegisContext context,
            TState state)
        {
            context.SetProperty(Key, generation);
            return callback(context, state);
        }

        public void Dispose()
        {
            lock (disposed)
            {
                disposed.Add(generation);
            }
        }
    }
}
