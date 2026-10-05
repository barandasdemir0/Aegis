using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Extensions.DependencyInjection;

namespace Aegis.Tests;

/// <summary>
/// Polly <c>AddResiliencePipeline(key, (builder, context) =&gt; ...)</c> eşdeğeri: <see cref="AegisPipelineContext"/> ile
/// GetOptions, EnableReloads (tip ve izleyici örneği), AddReloadToken, OnPipelineDisposed; kayıt defteri <c>await using</c>.
/// </summary>
public class PipelineContextTests
{
    private sealed class RetrySettings
    {
        public int Attempts { get; set; } = 1;
    }

    private static int CountAttempts(IAegisPipeline pipeline)
    {
        var calls = 0;
        try
        {
            pipeline.Execute(() => { calls++; throw new InvalidOperationException(); });
        }
        catch (InvalidOperationException)
        {
        }

        return calls;
    }

    [Fact]
    public void WithoutReloadSources_ReturnsPlainPipeline_NoWrapperCost()
    {
        var services = new ServiceCollection();
        services.AddAegisPipelineWithContext("plain", (b, ctx) =>
        {
            Assert.Equal("plain", ctx.PipelineName);
            Assert.NotNull(ctx.ServiceProvider);
            b.AddTimeout(TimeSpan.FromSeconds(1));
        });
        using var provider = services.BuildServiceProvider();

        Assert.IsType<AegisPipeline>(provider.GetRequiredService<IAegisPipelineRegistry>().GetPipeline("plain"));
    }

    [Fact]
    public void EnableReloads_RebuildsWhenConfigurationChanges()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Retry:Attempts"] = "1" }).Build();
        var services = new ServiceCollection();
        services.Configure<RetrySettings>(config.GetSection("Retry"));
        services.AddAegisPipelineWithContext("reload", (b, ctx) =>
        {
            var settings = ctx.GetOptions<RetrySettings>();
            ctx.EnableReloads<RetrySettings>();
            b.AddRetry(o => { o.MaxRetryAttempts = settings.Attempts; o.Delay = TimeSpan.Zero; });
        });
        using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<IAegisPipelineRegistry>().GetPipeline("reload");
        Assert.Equal(2, CountAttempts(pipeline));

        config["Retry:Attempts"] = "3";
        config.Reload();

        Assert.Equal(4, CountAttempts(pipeline));
    }

    private sealed class ManualMonitor<T>(T value) : IOptionsMonitor<T>
    {
        private Action<T, string?>? _listener;

        public T CurrentValue { get; private set; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener)
        {
            _listener += listener;
            return new Subscription(() => _listener -= listener);
        }

        public void Set(T value)
        {
            CurrentValue = value;
            _listener?.Invoke(value, Options.DefaultName);
        }

        public int Listeners => _listener?.GetInvocationList().Length ?? 0;

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    [Fact]
    public void EnableReloads_WithMonitorInstance_RebuildsAndReleasesOldSubscription()
    {
        var monitor = new ManualMonitor<RetrySettings>(new RetrySettings { Attempts = 1 });
        var disposedGenerations = 0;
        var services = new ServiceCollection();
        services.AddAegisPipelineWithContext("monitor", (b, ctx) =>
        {
            ctx.EnableReloads(monitor);
            ctx.OnPipelineDisposed(() => Interlocked.Increment(ref disposedGenerations));
            b.AddRetry(o => { o.MaxRetryAttempts = monitor.CurrentValue.Attempts; o.Delay = TimeSpan.Zero; });
        });
        var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<IAegisPipelineRegistry>().GetPipeline("monitor");
        Assert.Equal(2, CountAttempts(pipeline));

        monitor.Set(new RetrySettings { Attempts = 4 });

        Assert.Equal(5, CountAttempts(pipeline));
        Assert.Equal(1, disposedGenerations); // eski nesil boşalınca bırakıldı
        Assert.Equal(1, monitor.Listeners);   // eski neslin aboneliği kaldırıldı, yalnızca güncel nesil dinliyor

        provider.Dispose();
        Assert.Equal(2, disposedGenerations);
        Assert.Equal(0, monitor.Listeners);
    }

    [Fact]
    public void AddReloadToken_RebuildsOnCancellation_AndIgnoresAlreadyCancelledToken()
    {
        var sources = new List<CancellationTokenSource>();
        var builds = 0;
        var services = new ServiceCollection();
        services.AddAegisPipelineWithContext("token", (b, ctx) =>
        {
            Interlocked.Increment(ref builds);
            var cts = new CancellationTokenSource();
            sources.Add(cts);
            ctx.AddReloadToken(cts.Token);
            ctx.AddReloadToken(new CancellationToken(canceled: true)); // kurulumda döngüye girmemeli
            b.AddTimeout(TimeSpan.FromSeconds(1));
        });
        using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<IAegisPipelineRegistry>().GetPipeline("token");
        Assert.Equal(1, builds);

        sources[0].Cancel();

        Assert.Equal(2, builds);
        Assert.Equal(7, pipeline.Execute(() => 7));
    }

    [Fact]
    public async Task Registries_SupportAwaitUsing()
    {
        var registry = new AegisPipelineRegistry();
        registry.RegisterPipeline("x", new AegisPipelineBuilder("x").AddTimeout(TimeSpan.FromSeconds(1)).Build());
        var pipeline = registry.GetPipeline("x");
        await using (registry)
        {
            Assert.Equal(1, pipeline.Execute(() => 1));
        }

        var keyed = new AegisPipelineRegistry<int>();
        await using (keyed)
        {
        }
    }
}
