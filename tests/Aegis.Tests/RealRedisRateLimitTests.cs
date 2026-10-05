using Aegis.Resilience.Distributed.Abstractions;
using Aegis.Resilience.Distributed.Redis;
using StackExchange.Redis;

namespace Aegis.Tests;

/// <summary>
/// Redis hız sınırlayıcı deposu gerçek Redis'e karşı: Lua betiklerinin bellek içi algoritmalarla aynı kararları vermesi,
/// iki pod'un kotayı paylaşması ve Redis erişilemezken yerel sınıra düşme. Redis yoksa atlanır
/// (adres: AEGIS_TEST_REDIS, varsayılan localhost:6379).
/// </summary>
public sealed class RealRedisRateLimitTests : IDisposable
{
    private static readonly string RedisConnectionString = Environment.GetEnvironmentVariable("AEGIS_TEST_REDIS") ?? "localhost:6379";

    // Katı semantik ölçülür: 250 ms varsayılan işlem sınırı paralel test yükünde aşılıp yerel sınıra düşebilirdi.
    private static readonly TimeSpan StrictTimeout = TimeSpan.FromSeconds(5);

    private readonly IConnectionMultiplexer? _redis;
    private readonly string _prefix = $"aegistest:rl:{Guid.NewGuid():N}:";

    public RealRedisRateLimitTests()
    {
        try
        {
            var options = ConfigurationOptions.Parse(RedisConnectionString);
            options.ConnectTimeout = 2000;
            options.AbortOnConnectFail = false;
            var mux = ConnectionMultiplexer.Connect(options);
            _redis = mux.IsConnected ? mux : null;
        }
        catch (RedisException)
        {
            _redis = null;
        }
    }

    public void Dispose() => _redis?.Dispose();

    private RedisRateLimitStore Store() => new(_redis!, _prefix, StrictTimeout);

    // Pencere uzun: test süresince pencere sınırı geçilmez, kararlar zamanlamadan bağımsızdır.
    private static DistributedRateLimitRule Rule(DistributedRateLimitAlgorithm algorithm, int limit) =>
        new(algorithm, limit, TimeSpan.FromMinutes(10), limit);

    [SkippableTheory]
    [InlineData(DistributedRateLimitAlgorithm.FixedWindow)]
    [InlineData(DistributedRateLimitAlgorithm.SlidingWindow)]
    [InlineData(DistributedRateLimitAlgorithm.TokenBucket)]
    public async Task Redis_MakesSameDecisionsAsInMemoryAlgorithms(DistributedRateLimitAlgorithm algorithm)
    {
        Skip.If(_redis is null, "Redis yok - test atlandı.");
        var rule = Rule(algorithm, 3);
        var redis = Store();
        var memory = new InMemoryDistributedRateLimitStore();

        for (var i = 0; i < 5; i++)
        {
            var fromRedis = await redis.TryAcquireAsync("anahtar", rule, 1, default);
            var fromMemory = await memory.TryAcquireAsync("anahtar", rule, 1, default);

            Assert.Equal(fromMemory.IsAcquired, fromRedis.IsAcquired);
            Assert.Equal(fromMemory.Remaining, fromRedis.Remaining);
            Assert.Equal(fromMemory.RetryAfter.HasValue, fromRedis.RetryAfter.HasValue);
        }
    }

    // Atomiklik: 4 pod (ayrı bağlantı) × 32 eşzamanlı iş parçacığı aynı anahtara saldırır; Lua betiği sayesinde tam olarak
    // kota kadar istek geçer (yarış yüzünden fazla izin verilmez).
    [SkippableTheory]
    [InlineData(DistributedRateLimitAlgorithm.FixedWindow)]
    [InlineData(DistributedRateLimitAlgorithm.SlidingWindow)]
    [InlineData(DistributedRateLimitAlgorithm.TokenBucket)]
    public async Task Redis_ConcurrentStormFromManyPods_AdmitsExactlyPermitLimit(DistributedRateLimitAlgorithm algorithm)
    {
        Skip.If(_redis is null, "Redis yok - test atlandı.");
        var rule = Rule(algorithm, 100);
        var pods = Enumerable.Range(0, 4).Select(_ => ConnectionMultiplexer.Connect(RedisConnectionString)).ToArray();
        try
        {
            var admitted = 0;
            using var start = new ManualResetEventSlim(false);
            var workers = Enumerable.Range(0, 128).Select(i => Task.Run(async () =>
            {
                var store = new RedisRateLimitStore(pods[i % pods.Length], _prefix, StrictTimeout);
                start.Wait();
                for (var call = 0; call < 10; call++)
                {
                    if ((await store.TryAcquireAsync($"firtina-{algorithm}", rule, 1, default)).IsAcquired)
                    {
                        Interlocked.Increment(ref admitted);
                    }
                }
            })).ToArray();

            start.Set();
            await Task.WhenAll(workers);

            Assert.Equal(100, admitted); // 1.280 istekten tam 100
        }
        finally
        {
            foreach (var pod in pods)
            {
                pod.Dispose();
            }
        }
    }

    [SkippableFact]
    public async Task Redis_TwoPodsShareOneQuota()
    {
        Skip.If(_redis is null, "Redis yok - test atlandı.");
        var rule = Rule(DistributedRateLimitAlgorithm.FixedWindow, 4);
        var podA = Store();
        var podB = Store();

        var acquired = 0;
        for (var i = 0; i < 4; i++)
        {
            acquired += (await podA.TryAcquireAsync("ortak", rule, 1, default)).IsAcquired ? 1 : 0;
            acquired += (await podB.TryAcquireAsync("ortak", rule, 1, default)).IsAcquired ? 1 : 0;
        }

        Assert.Equal(4, acquired); // 8 istekten tam 4'ü: kota pod'lar arasında ortak
        Assert.True(podA.IsAvailable);
    }

    [SkippableFact]
    public async Task Redis_TokenBucket_RetryAfterMatchesRefillRate()
    {
        Skip.If(_redis is null, "Redis yok - test atlandı.");
        var rule = new DistributedRateLimitRule(DistributedRateLimitAlgorithm.TokenBucket, 2, TimeSpan.FromSeconds(10), 1);
        var store = Store();

        await store.TryAcquireAsync("kova", rule, 1, default);
        await store.TryAcquireAsync("kova", rule, 1, default);
        var rejected = await store.TryAcquireAsync("kova", rule, 1, default);

        Assert.False(rejected.IsAcquired);
        Assert.InRange(rejected.RetryAfter!.Value, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(10)); // 10 sn'de 1 jeton
    }

    [Fact]
    public async Task UnreachableRedis_FallsBackToLocalLimit_NotUnlimited()
    {
        var options = ConfigurationOptions.Parse("127.0.0.1:1"); // dinlenmeyen port
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 200;
        options.BacklogPolicy = BacklogPolicy.FailFast;
        using var unreachable = await ConnectionMultiplexer.ConnectAsync(options);
        var store = new RedisRateLimitStore(unreachable, _prefix);
        var rule = Rule(DistributedRateLimitAlgorithm.FixedWindow, 2);

        var decisions = new List<bool>();
        for (var i = 0; i < 3; i++)
        {
            decisions.Add((await store.TryAcquireAsync("yerel", rule, 1, default)).IsAcquired);
        }

        Assert.Equal([true, true, false], decisions); // pod başına sınır sürer
        Assert.False(store.IsAvailable);
    }
}
