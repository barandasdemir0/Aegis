using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Data.SqlClient;
using Aegis.Resilience.Distributed.Abstractions;
using Aegis.Resilience.Extensions.Caching;

namespace Aegis.CompatibilityTests;

/// <summary>1.2.0 ekosistem yetenekleri .NET Framework 4.8 üzerinde.</summary>
public class EcosystemCompatibilityTests
{
    [Fact]
    public async Task ConsecutiveFailureCircuit_OpensOnFramework()
    {
        var state = new CircuitBreakerStateProvider();
        using var pipeline = new AegisPipelineBuilder("fx-art-arda")
            .AddCircuitBreaker(o => { o.MinimumThroughput = 1000; o.ConsecutiveFailureThreshold = 2; o.StateProvider = state; })
            .Build();

        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()));
        }

        Assert.Equal(CircuitState.Open, state.CircuitState);
    }

    [Fact]
    public async Task SlidingCache_AndDistributedCacheStore_WithSourceGeneratedJson()
    {
        var clock = new FakeTimeProvider();
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var store = new DistributedCacheStore(cache, new SystemTextJsonCacheSerializer(FxJsonContext.Default.Options));
        using var pipeline = new AegisPipelineBuilder("fx-cache")
            .AddCache(o => { o.KeySelector = _ => "k"; o.Store = store; o.Ttl = TimeSpan.FromMinutes(1); o.SlidingExpiration = true; })
            .WithTimeProvider(clock)
            .Build();
        var calls = 0;

        var first = await pipeline.ExecuteAsync(_ => new ValueTask<FxUrun>(new FxUrun { Id = ++calls }));
        var second = await pipeline.ExecuteAsync(_ => new ValueTask<FxUrun>(new FxUrun { Id = ++calls }));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void SqlTransientErrors_RecognizeWrappedDeadlock_OnFramework()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var error = (SqlError)typeof(SqlError).GetConstructors(flags).Single(c => c.GetParameters().Length == 8 &&
            c.GetParameters()[7].ParameterType == typeof(Exception)).Invoke(new object?[] { 1205, (byte)0, (byte)13, "s", "kilitlenme", "", 0, null });
        var errors = (SqlErrorCollection)typeof(SqlErrorCollection).GetConstructors(flags).Single().Invoke(null);
        typeof(SqlErrorCollection).GetMethod("Add", flags)!.Invoke(errors, new object[] { error });
        var exception = (SqlException)typeof(SqlException).GetMethods(flags).Single(m => m.Name == "CreateException" &&
            m.GetParameters().Length == 4 && m.GetParameters()[2].ParameterType == typeof(Guid)).Invoke(null, new object?[] { errors, "16.0", Guid.Empty, null })!;

        Assert.True(AegisSqlTransientErrors.IsTransient(new InvalidOperationException("sarmalayıcı", exception)));
    }

    [Fact]
    public async Task DistributedRateLimiter_OnFramework()
    {
        using var pipeline = new AegisPipelineBuilder("fx-rl")
            .AddDistributedRateLimiter(new InMemoryDistributedRateLimitStore(), o =>
            {
                o.Algorithm = DistributedRateLimitAlgorithm.SlidingWindow;
                o.PermitLimit = 2;
                o.Window = TimeSpan.FromMinutes(1);
            })
            .Build();

        await pipeline.ExecuteAsync(_ => new ValueTask<int>(1));
        await pipeline.ExecuteAsync(_ => new ValueTask<int>(1));
        var ex = await Assert.ThrowsAsync<RateLimitRejectedException>(async () => await pipeline.ExecuteAsync(_ => new ValueTask<int>(1)));
        Assert.NotNull(ex.RetryAfter);
    }
}

public sealed class FxUrun
{
    public int Id { get; set; }
}

[JsonSerializable(typeof(FxUrun))]
internal sealed partial class FxJsonContext : JsonSerializerContext;
