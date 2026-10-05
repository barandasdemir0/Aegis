using System.ComponentModel;
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
using Aegis.Resilience.Core.Strategies.Cache;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Data.SqlClient;
using Aegis.Resilience.Distributed.Abstractions;
using Aegis.Resilience.Extensions.Caching;

namespace Aegis.Tests;

/// <summary>
/// NuGet'teki ilk 20 resilience paketi karşısında kapatılan boşluklar: art arda hata devresi (Polly v7), cache TTL türleri ve
/// dış/dağıtık depo (Polly.Caching.*), SQL geçici hata tanıma (Enterprise Library TFH / Topaz), dağıtık hız sınırlama
/// (AspNetCoreRateLimit.Redis / RedisRateLimiting — bellek içi depoyla; Redis için RealRedisRateLimitTests).
/// </summary>
public class EcosystemParityTests
{
    // ---------------------------------------------------------------- 1) Art arda hata devresi (Polly v7 CircuitBreaker)

    [Fact]
    public async Task ConsecutiveFailureThreshold_OpensBeforeMinimumThroughput()
    {
        var state = new CircuitBreakerStateProvider();
        using var pipeline = new AegisPipelineBuilder("art-arda")
            .AddCircuitBreaker(o => { o.MinimumThroughput = 1000; o.ConsecutiveFailureThreshold = 3; o.StateProvider = state; })
            .Build();

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()).AsTask());
        }

        Assert.Equal(CircuitState.Open, state.CircuitState); // oran kuralı 1000 çağrı beklerdi
        await Assert.ThrowsAsync<BrokenCircuitException>(() => pipeline.ExecuteAsync(_ => new ValueTask<int>(1)).AsTask());
    }

    [Fact]
    public async Task ConsecutiveFailureThreshold_SuccessResetsTheCount()
    {
        var state = new CircuitBreakerStateProvider();
        using var pipeline = new AegisPipelineBuilder("art-arda-sifirla")
            .AddCircuitBreaker(o => { o.MinimumThroughput = 1000; o.ConsecutiveFailureThreshold = 2; o.StateProvider = state; })
            .Build();

        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException()).AsTask());
            await pipeline.ExecuteAsync(_ => new ValueTask<int>(1)); // araya giren başarı
        }

        Assert.Equal(CircuitState.Closed, state.CircuitState);
    }

    [Fact]
    public void ConsecutiveFailureThreshold_MustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CircuitBreakerOptions { ConsecutiveFailureThreshold = 0 }.Validate());
        new CircuitBreakerOptions { ConsecutiveFailureThreshold = null }.Validate(); // varsayılan: kapalı
    }

    // ---------------------------------------------------------------- 2) Cache: kayan süre, sonuca göre süre, dış depo

    private static (IAegisPipeline Pipeline, FakeTimeProvider Clock) CachePipeline(Action<CacheOptions> configure)
    {
        var clock = new FakeTimeProvider();
        var pipeline = new AegisPipelineBuilder("cache").AddCache(o => { o.KeySelector = _ => "k"; configure(o); }).WithTimeProvider(clock).Build();
        return (pipeline, clock);
    }

    [Fact]
    public async Task SlidingExpiration_KeepsFrequentlyReadEntryAlive_AndExpiresIdleOne()
    {
        var (pipeline, clock) = CachePipeline(o => { o.Ttl = TimeSpan.FromSeconds(10); o.SlidingExpiration = true; });
        using var _ = pipeline;
        var calls = 0;
        ValueTask<int> Load(Aegis.Resilience.Core.Context.AegisContext ctx) => new(++calls);

        await pipeline.ExecuteAsync(Load);
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(8)); // her okuma süreyi yeniler: toplam 24 sn > 10 sn TTL
            Assert.Equal(1, await pipeline.ExecuteAsync(Load));
        }

        clock.Advance(TimeSpan.FromSeconds(11)); // okunmadan TTL geçti
        Assert.Equal(2, await pipeline.ExecuteAsync(Load));
    }

    [Fact]
    public async Task FixedExpiration_IsTheDefault()
    {
        var (pipeline, clock) = CachePipeline(o => o.Ttl = TimeSpan.FromSeconds(10));
        using var _ = pipeline;
        var calls = 0;
        ValueTask<int> Load(Aegis.Resilience.Core.Context.AegisContext ctx) => new(++calls);

        await pipeline.ExecuteAsync(Load);
        clock.Advance(TimeSpan.FromSeconds(8));
        await pipeline.ExecuteAsync(Load);
        clock.Advance(TimeSpan.FromSeconds(3)); // ilk yazmadan 11 sn sonra
        Assert.Equal(2, await pipeline.ExecuteAsync(Load));
    }

    [Fact]
    public async Task TtlGenerator_SetsPerResultLifetime_AndZeroSkipsCaching()
    {
        var (pipeline, clock) = CachePipeline(o => o.TtlGenerator = (_, result) => result is "gecici" ? TimeSpan.Zero : TimeSpan.FromMinutes(5));
        using var _ = pipeline;
        var calls = 0;

        Assert.Equal("gecici", await pipeline.ExecuteAsync(_ => new ValueTask<string>(++calls == 1 ? "gecici" : "kalici")));
        Assert.Equal("kalici", await pipeline.ExecuteAsync(_ => new ValueTask<string>(++calls == 1 ? "gecici" : "kalici"))); // önceki önbelleğe alınmadı
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal("kalici", await pipeline.ExecuteAsync(_ => new ValueTask<string>("yeni")));
        Assert.Equal(2, calls);
    }

    private sealed class FlakyStore : IAegisCacheStore
    {
        public bool FailReads { get; set; }

        public bool FailWrites { get; set; }

        public Dictionary<string, object?> Items { get; } = [];

        public ValueTask<AegisCacheLookup<T>> TryGetAsync<T>(string key, CancellationToken cancellationToken) =>
            FailReads ? throw new TimeoutException("redis yavaş")
            : Items.TryGetValue(key, out var value) ? new(AegisCacheLookup.Hit((T)value!)) : new(AegisCacheLookup.Miss<T>());

        public ValueTask SetAsync<T>(string key, T value, AegisCacheEntryOptions options, CancellationToken cancellationToken)
        {
            if (FailWrites)
            {
                throw new TimeoutException("redis yavaş");
            }

            Items[key] = value;
            return default;
        }

        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken)
        {
            Items.Remove(key);
            return default;
        }
    }

    [Fact]
    public async Task ExternalStore_ServesHits_AndStoreFailuresNeverFailTheCall()
    {
        var store = new FlakyStore();
        var errors = new List<string>();
        var (pipeline, _) = CachePipeline(o => { o.Store = store; o.OnCacheError = (key, ex) => errors.Add($"{key}:{ex.GetType().Name}"); });
        using var disposable = pipeline;
        var calls = 0;
        ValueTask<int> Load(Aegis.Resilience.Core.Context.AegisContext ctx) => new(++calls);

        await pipeline.ExecuteAsync(Load);
        Assert.Equal(1, await pipeline.ExecuteAsync(Load));      // depodan isabet
        Assert.Equal(1, store.Items["k"]);

        store.FailReads = true;
        Assert.Equal(2, await pipeline.ExecuteAsync(Load));      // okuma hatası = ıskalama, çağrı sürer
        store.FailReads = false;
        store.FailWrites = true;
        store.Items.Clear();
        Assert.Equal(3, await pipeline.ExecuteAsync(Load));      // yazma hatası yok sayılır
        Assert.Equal(["k:TimeoutException", "k:TimeoutException"], errors);
    }

    [Fact]
    public async Task InvalidateAsync_RemovesFromExternalStore()
    {
        var store = new FlakyStore();
        var strategy = new CacheStrategy(new CacheOptions { KeySelector = _ => "k", Store = store });
        using var pipeline = new AegisPipelineBuilder("cache").AddStrategy(strategy).Build();

        await pipeline.ExecuteAsync(_ => new ValueTask<int>(1));
        await strategy.InvalidateAsync("k");

        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task DistributedCacheStore_RoundTripsThroughIDistributedCache_WithSourceGeneratedJson()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var store = new DistributedCacheStore(cache, new SystemTextJsonCacheSerializer(CacheJsonContext.Default.Options), keyPrefix: "katalog:");
        var (pipeline, _) = CachePipeline(o => o.Store = store);
        using var disposable = pipeline;
        var calls = 0;

        var first = await pipeline.ExecuteAsync(_ => new ValueTask<Urun>(new Urun(++calls, "Kalem")));
        var second = await pipeline.ExecuteAsync(_ => new ValueTask<Urun>(new Urun(++calls, "Silgi")));

        Assert.Equal(first, second);                           // ikinci çağrı dağıtık önbellekten (yeni nesne, aynı değer)
        Assert.Equal(1, calls);
        Assert.NotNull(await cache.GetAsync("katalog:k"));     // önek uygulandı
    }

    [Fact]
    public void SystemTextJsonCacheSerializer_RequiresTypeInfoResolver()
    {
        Assert.Throws<ArgumentException>(() => new SystemTextJsonCacheSerializer(new System.Text.Json.JsonSerializerOptions()));
#pragma warning disable IL2026, IL3050 // test: yansıma tabanlı serileştiricinin çalıştığını doğrular
        var reflection = SystemTextJsonCacheSerializer.CreateReflectionBased();
#pragma warning restore IL2026, IL3050
        Assert.Equal(new Urun(7, "Defter"), reflection.Deserialize<Urun>(reflection.Serialize(new Urun(7, "Defter"))));
    }

    // ---------------------------------------------------------------- 3) SQL geçici hata tanıma (Topaz eşdeğeri)

    /// <summary>SqlException'ın genel kurucusu yok; Microsoft.Data.SqlClient'ın kendi iç fabrikasıyla oluşturulur.</summary>
    private static SqlException CreateSqlException(int number, Exception? innerException = null)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var errorCtor = typeof(SqlError).GetConstructors(flags).Single(c =>
            c.GetParameters().Select(p => p.ParameterType).SequenceEqual(
                [typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string), typeof(int), typeof(Exception)]));
        var error = (SqlError)errorCtor.Invoke([number, (byte)0, (byte)16, "sunucu", $"hata {number}", "", 0, null]);

        var errors = (SqlErrorCollection)typeof(SqlErrorCollection).GetConstructors(flags).Single().Invoke(null);
        typeof(SqlErrorCollection).GetMethod("Add", flags)!.Invoke(errors, [error]);

        var create = typeof(SqlException).GetMethods(flags).Single(m => m.Name == "CreateException" &&
            m.GetParameters().Select(p => p.ParameterType).SequenceEqual([typeof(SqlErrorCollection), typeof(string), typeof(Guid), typeof(Exception)]));
        return (SqlException)create.Invoke(null, [errors, "16.0", Guid.Empty, innerException])!;
    }

    [Theory]
    [InlineData(1205, true)]   // kilitlenme kurbanı
    [InlineData(40501, true)]  // servis meşgul (Azure SQL)
    [InlineData(40613, true)]  // veritabanı kullanılamıyor
    [InlineData(49920, true)]  // çok fazla işlem
    [InlineData(-2, false)]    // komut zaman aşımı: işlem tamamlanmış olabilir, bilerek geçici değil
    [InlineData(547, false)]   // yabancı anahtar ihlali: kalıcı
    [InlineData(2627, false)]  // benzersiz anahtar ihlali: kalıcı
    public void SqlTransientErrors_ClassifiesByErrorNumber(int number, bool expected)
    {
        Assert.Equal(expected, AegisSqlTransientErrors.IsTransient(CreateSqlException(number)));
    }

    [Fact]
    public void SqlTransientErrors_PreLoginHandshake_IsTransientOnlyWithWin32Inner()
    {
        Assert.True(AegisSqlTransientErrors.IsTransient(CreateSqlException(203, new Win32Exception(203))));
        Assert.False(AegisSqlTransientErrors.IsTransient(CreateSqlException(203)));
    }

    [Fact]
    public void SqlTransientErrors_WalksInnerExceptionChain_AndTreatsTimeoutAsTransient()
    {
        var wrappedByOrm = new InvalidOperationException("DbUpdateException benzeri sarmalayıcı", CreateSqlException(1205));
        Assert.True(AegisSqlTransientErrors.IsTransient(wrappedByOrm));
        Assert.True(AegisSqlTransientErrors.IsTransient(new TimeoutException()));
        Assert.False(AegisSqlTransientErrors.IsTransient(new InvalidOperationException("başka")));
        Assert.False(AegisSqlTransientErrors.IsTransient(null));
        Assert.Equal(175, AegisSqlTransientErrors.ErrorNumbers.Count);
    }

    [Fact]
    public async Task HandleSqlTransientErrors_RetriesDeadlock_ButNotConstraintViolation()
    {
        using var pipeline = new AegisPipelineBuilder("sql")
            .AddRetry(o => { o.Delay = TimeSpan.Zero; o.ShouldHandleOutcome = new AegisPredicateBuilder().HandleSqlTransientErrors(); })
            .Build();

        var deadlockCalls = 0;
        Assert.Equal("ok", await pipeline.ExecuteAsync(_ => ++deadlockCalls < 3 ? throw CreateSqlException(1205) : new ValueTask<string>("ok")));
        Assert.Equal(3, deadlockCalls);

        var violationCalls = 0;
        await Assert.ThrowsAsync<SqlException>(() => pipeline.ExecuteAsync<string>(_ => { violationCalls++; throw CreateSqlException(547); }).AsTask());
        Assert.Equal(1, violationCalls);
    }

    // ---------------------------------------------------------------- 4) Dağıtık hız sınırlama (bellek içi depo)

    private static readonly DistributedRateLimitRule FixedRule = new(DistributedRateLimitAlgorithm.FixedWindow, 3, TimeSpan.FromSeconds(1), 3);

    [Fact]
    public async Task InMemoryStore_FixedWindow_LimitsAndReportsRetryAfter()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryDistributedRateLimitStore(clock);

        for (var i = 0; i < 3; i++)
        {
            Assert.True((await store.TryAcquireAsync("api", FixedRule, 1, default)).IsAcquired);
        }

        var rejected = await store.TryAcquireAsync("api", FixedRule, 1, default);
        Assert.False(rejected.IsAcquired);
        Assert.InRange(rejected.RetryAfter!.Value, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1));

        clock.Advance(rejected.RetryAfter.Value);
        Assert.True((await store.TryAcquireAsync("api", FixedRule, 1, default)).IsAcquired);
    }

    [Fact]
    public async Task InMemoryStore_TokenBucket_AllowsBurst_ThenRefillsAtRate()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryDistributedRateLimitStore(clock);
        var rule = new DistributedRateLimitRule(DistributedRateLimitAlgorithm.TokenBucket, 5, TimeSpan.FromSeconds(1), 5);

        for (var i = 0; i < 5; i++)
        {
            Assert.True((await store.TryAcquireAsync("api", rule, 1, default)).IsAcquired); // patlama: kapasite kadar
        }

        var rejected = await store.TryAcquireAsync("api", rule, 1, default);
        Assert.False(rejected.IsAcquired);
        Assert.Equal(TimeSpan.FromMilliseconds(200), rejected.RetryAfter); // saniyede 5 jeton: 1 jeton 200 ms

        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.True((await store.TryAcquireAsync("api", rule, 1, default)).IsAcquired);
        Assert.False((await store.TryAcquireAsync("api", rule, 1, default)).IsAcquired);
    }

    [Fact]
    public async Task InMemoryStore_SlidingWindow_PreventsDoubleBurstAtWindowEdge()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryDistributedRateLimitStore(clock);
        var sliding = new DistributedRateLimitRule(DistributedRateLimitAlgorithm.SlidingWindow, 4, TimeSpan.FromSeconds(1), 4);
        var fixedRule = new DistributedRateLimitRule(DistributedRateLimitAlgorithm.FixedWindow, 4, TimeSpan.FromSeconds(1), 4);

        async Task<int> BurstAsync(string key, DistributedRateLimitRule rule)
        {
            var acquired = 0;
            for (var i = 0; i < 4; i++)
            {
                acquired += (await store.TryAcquireAsync(key, rule, 1, default)).IsAcquired ? 1 : 0;
            }

            return acquired;
        }

        clock.Advance(TimeSpan.FromMilliseconds(900)); // pencerenin sonuna yakın
        Assert.Equal(4, await BurstAsync("kayan", sliding));
        Assert.Equal(4, await BurstAsync("sabit", fixedRule));
        clock.Advance(TimeSpan.FromMilliseconds(200)); // yeni pencerenin başı: 200 ms içinde...

        Assert.Equal(4, await BurstAsync("sabit", fixedRule)); // sabit pencere: 200 ms'de 8 istek
        Assert.True(await BurstAsync("kayan", sliding) <= 1);   // kayan pencere: önceki pencerenin ağırlığı sayılır
    }

    [Fact]
    public async Task DistributedRateLimiter_SharesQuotaAcrossPods_AndRejectsLikeOtherLimiters()
    {
        var store = new InMemoryDistributedRateLimitStore(); // iki "pod" aynı depoyu görür
        var rejections = new List<TimeSpan?>();
        IAegisPipeline Pod(string name) => new AegisPipelineBuilder(name)
            .AddDistributedRateLimiter(store, o =>
            {
                o.LimiterKey = "odeme-api";
                o.Algorithm = DistributedRateLimitAlgorithm.FixedWindow;
                o.PermitLimit = 3;
                o.Window = TimeSpan.FromMinutes(1);
                o.OnRejected = a => { rejections.Add(a.RetryAfter); return default; };
            })
            .Build();
        using var podA = Pod("pod-a");
        using var podB = Pod("pod-b");

        await podA.ExecuteAsync(_ => new ValueTask<int>(1));
        await podB.ExecuteAsync(_ => new ValueTask<int>(1));
        await podA.ExecuteAsync(_ => new ValueTask<int>(1));
        var ex = await Assert.ThrowsAsync<RateLimitRejectedException>(() => podB.ExecuteAsync(_ => new ValueTask<int>(1)).AsTask());

        Assert.NotNull(ex.RetryAfter);
        Assert.Single(rejections);
    }

    [Fact]
    public async Task DistributedRateLimiter_PartitionsHaveSeparateQuotas()
    {
        var store = new InMemoryDistributedRateLimitStore();
        using var pipeline = new AegisPipelineBuilder("kiraci")
            .AddDistributedRateLimiter(store, o =>
            {
                o.Algorithm = DistributedRateLimitAlgorithm.FixedWindow;
                o.PermitLimit = 1;
                o.Window = TimeSpan.FromMinutes(1);
                o.PartitionKeySelector = ctx => ctx.OperationKey;
            })
            .Build();

        await pipeline.ExecuteAsync(_ => new ValueTask<int>(1), new Aegis.Resilience.Core.Context.AegisContext { OperationKey = "kiraci-a" });
        await pipeline.ExecuteAsync(_ => new ValueTask<int>(1), new Aegis.Resilience.Core.Context.AegisContext { OperationKey = "kiraci-b" });
        await Assert.ThrowsAsync<RateLimitRejectedException>(() =>
            pipeline.ExecuteAsync(_ => new ValueTask<int>(1), new Aegis.Resilience.Core.Context.AegisContext { OperationKey = "kiraci-a" }).AsTask());
    }

    [Fact]
    public void DistributedRateLimiterOptions_Validate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DistributedRateLimiterOptions { PermitLimit = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new DistributedRateLimiterOptions { Window = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new DistributedRateLimiterOptions { TokensPerPeriod = 0 }.Validate());
    }
}

internal sealed record Urun(int Id, string Ad);

[JsonSerializable(typeof(Urun))]
internal sealed partial class CacheJsonContext : JsonSerializerContext;
