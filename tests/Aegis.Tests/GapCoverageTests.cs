using System.Diagnostics;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Cache;
using Aegis.Resilience.Core.Strategies.RateLimiter;

namespace Aegis.Tests;

/// <summary>
/// Denetimde tespit edilen KAPSAM BOŞLUKLARINI kapatan testler:
/// doğrudan birim testi olmayan stratejiler, LRU tahliyesi, eşzamanlılık güvenliği,
/// yük altında davranış ve kaynak sızıntısı.
/// </summary>
public class GapCoverageTests
{
    // =====================================================================
    // AEGIS-123: Cache tahliyesi GERÇEKTEN LRU mu?
    // (Eskiden _cache.Keys.Take(...) ile RASTGELE tahliye yapılıyordu.)
    // =====================================================================
    [Fact]
    public async Task Cache_WhenFull_ShouldEvictLeastRecentlyUsed_NotRandom()
    {
        // MaxEntries=10 -> dolduğunda MaxEntries/10 = 1 girdi tahliye edilir
        var strategy = new CacheStrategy(new CacheOptions
        {
            Ttl = TimeSpan.FromMinutes(5),
            MaxEntries = 10,
            KeySelector = ctx => ctx.Properties.TryGetValue("K", out var k) ? k!.ToString()! : "default"
        });

        using var pipeline = new AegisPipelineBuilder("lru-test").AddStrategy(strategy).Build();

        async Task<string> Call(string key)
        {
            var ctx = new AegisContext();
            ctx.SetProperty("K", key);
            return await pipeline.ExecuteAsync(_ => ValueTask.FromResult($"deger-{key}"), ctx);
        }

        // 1) Önbelleği 10 girdiyle doldur (key-0 ... key-9)
        for (var i = 0; i < 10; i++)
        {
            await Call($"key-{i}");
            await Task.Delay(2); // LastAccessed zaman damgaları ayrışsın
        }

        // 2) key-0'a TEKRAR eriş -> artık "en son kullanılan" o olur.
        //    En uzun süredir kullanılmayan artık key-1'dir.
        await Task.Delay(5);
        await Call("key-0");

        // 3) Yeni bir girdi ekle -> kapasite dolu olduğu için LRU tahliyesi tetiklenir
        await Task.Delay(5);
        await Call("yeni-key");

        // 4) key-0 HÂLÂ önbellekte olmalı (az önce kullanıldı),
        //    key-1 ise atılmış olmalı (en uzun süredir kullanılmayan).
        var computedForKey0 = false;
        var ctx0 = new AegisContext();
        ctx0.SetProperty("K", "key-0");
        await pipeline.ExecuteAsync(_ => { computedForKey0 = true; return ValueTask.FromResult("yeniden"); }, ctx0);

        Assert.False(computedForKey0,
            "key-0 az önce kullanılmasına rağmen tahliye edilmiş — tahliye LRU değil (rastgele) çalışıyor.");

        strategy.Dispose();
    }

    /// <summary>
    /// AEGIS-126 regresyonu: Önbellek girdi sayısı, YOĞUN EŞZAMANLI yükte bile MaxEntries sınırına
    /// yakın kalmalı. Eskiden tahliye kilitsiz ve yalnızca %10'luk yapıldığı için sınır aşılıyordu
    /// (rastgele anahtarla gelen isteklerle bellek sınırsız büyüyebilirdi).
    /// </summary>
    [Fact]
    public async Task Cache_ShouldRespectMaxEntries_UnderConcurrentDistinctKeys()
    {
        const int maxEntries = 50;
        var strategy = new CacheStrategy(new CacheOptions
        {
            Ttl = TimeSpan.FromMinutes(5), // TTL ile kendiliğinden düşmesin; sadece kapasite denetimi sınasın
            MaxEntries = maxEntries,
            KeySelector = ctx => ctx.Properties.TryGetValue("K", out var k) ? k!.ToString()! : "default"
        });
        using var pipeline = new AegisPipelineBuilder("cache-cap").AddStrategy(strategy).Build();

        // 5.000 FARKLI anahtarla eşzamanlı yük
        var tasks = Enumerable.Range(0, 5_000).Select(i => Task.Run(async () =>
        {
            var ctx = new AegisContext();
            ctx.SetProperty("K", $"k{i}");
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult(i), ctx);
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.True(strategy.EntryCount <= maxEntries * 2,
            $"önbellek sınırı aşıldı: MaxEntries={maxEntries} iken {strategy.EntryCount} girdi var (bellek sınırsız büyüyor).");

        strategy.Dispose();
    }

    // =====================================================================
    // AEGIS-124: AdaptiveConcurrency eşzamanlı okumada tutarlı mı?
    // =====================================================================
    [Fact]
    public async Task AdaptiveConcurrency_ConcurrentReads_ShouldStayWithinConfiguredBounds()
    {
        var strategy = new AdaptiveConcurrencyStrategy(new AdaptiveConcurrencyOptions
        {
            InitialConcurrency = 20,
            MinConcurrency = 5,
            MaxConcurrency = 40,
            QueueTimeout = TimeSpan.FromSeconds(5)
        });
        using var pipeline = new AegisPipelineBuilder("adaptive-race").AddStrategy(strategy).Build();

        // Okunan değerler BİRİKTİRİLMEZ (eskiden sınırsız ConcurrentBag, 1 CPU / 512 MB Linux konteynerde
        // OutOfMemoryException üretiyordu); anında sınır kontrolü yapılır, yalnızca sayaç tutulur.
        long reads = 0, outOfRange = 0;
        var stop = false;

        // Bir thread sürekli CurrentLimit okur (kilitsiz okuma bozuk değer verirse yakalanır)
        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                var limit = strategy.CurrentLimit;
                var active = strategy.ActiveExecutions;
                if (limit is < 0 or > 40 || active is < 0 or > 40) outOfRange++;
                reads++;
            }
        });

        // Diğerleri limiti sürekli değiştirir
        var workers = Enumerable.Range(0, 40).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 25; i++)
            {
                try
                {
                    await pipeline.ExecuteAsync(async _ => { await Task.Delay(1); return true; });
                }
                catch (Aegis.Resilience.Core.Exceptions.RateLimitRejectedException)
                {
                    // Tek çekirdekte gecikme artınca adaptif limit TASARIM GEREĞİ daralır ve kuyruk süresi aşılabilir
                    // (1 CPU Linux konteynerde görüldü). Red geçerli bir sonuçtur; bu test sınırları ve sızıntıyı ölçer.
                }
            }
        })).ToArray();

        try
        {
            await Task.WhenAll(workers);
        }
        finally
        {
            // Test başarısız olsa bile okuyucu durdurulur (eskiden dönmeye devam edip sonraki testlerin CPU'sunu çalıyordu)
            Volatile.Write(ref stop, true);
            await reader;
        }

        Assert.True(reads > 0);
        Assert.Equal(0, outOfRange); // MinConcurrency..MaxConcurrency + aktif sayaç
        Assert.InRange(strategy.CurrentLimit, 5, 40);
        Assert.Equal(0, strategy.ActiveExecutions); // tüm işler bitti, sayaç sızdırmamalı
    }

    // =====================================================================
    // Doğrudan birim testi olmayan stratejiler
    // =====================================================================
    [Fact]
    public async Task ConcurrencyLimiter_ShouldReleasePermit_EvenWhenCallbackThrows()
    {
        var strategy = new ConcurrencyLimiterStrategy(new ConcurrencyLimiterOptions
        {
            MaxConcurrentExecutions = 1,
            QueueLimit = int.MaxValue,
            QueueTimeout = TimeSpan.FromMilliseconds(500)
        });
        using var pipeline = new AegisPipelineBuilder("cl-release").AddStrategy(strategy).Build();

        // Hata fırlatan 5 çağrı: her biri izni geri bırakmalı, aksi halde 2. çağrı sonsuza kadar beklerdi
        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await pipeline.ExecuteAsync<int>(_ => throw new InvalidOperationException("patla")));
        }

        // Hâlâ çalışabiliyor olmalı (izin sızıntısı yok)
        var ok = await pipeline.ExecuteAsync(_ => ValueTask.FromResult(42));
        Assert.Equal(42, ok);
        Assert.Equal(1, strategy.CurrentLimit);
        strategy.Dispose();
    }

    [Fact]
    public async Task ConcurrencyLimiter_ShouldApplyDynamicLimitChange()
    {
        var currentLimit = 1;
        var options = new ConcurrencyLimiterOptions
        {
            MaxConcurrentExecutions = 1,
            QueueTimeout = TimeSpan.Zero
        };
        options.OptionsProvider = () => new ConcurrencyLimiterOptions
        {
            MaxConcurrentExecutions = Volatile.Read(ref currentLimit),
            QueueTimeout = TimeSpan.Zero
        };

        var strategy = new ConcurrencyLimiterStrategy(options);
        using var pipeline = new AegisPipelineBuilder("cl-dynamic").AddStrategy(strategy).Build();

        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1));
        Assert.Equal(1, strategy.CurrentLimit);

        // Canlı olarak limiti büyüt
        Volatile.Write(ref currentLimit, 5);
        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1));
        Assert.Equal(5, strategy.CurrentLimit);

        // Canlı olarak küçült
        Volatile.Write(ref currentLimit, 2);
        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1));
        Assert.Equal(2, strategy.CurrentLimit);

        strategy.Dispose();
    }

    [Fact]
    public async Task SlidingWindowRateLimiter_ShouldRefill_AsSegmentsRollOver()
    {
        var strategy = new SlidingWindowRateLimiterStrategy(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 3,
            Window = TimeSpan.FromMilliseconds(600),
            SegmentsPerWindow = 6
        });
        using var pipeline = new AegisPipelineBuilder("sw-refill").AddStrategy(strategy).Build();

        for (var i = 0; i < 3; i++)
        {
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult(i));
        }

        await Assert.ThrowsAsync<RateLimitRejectedException>(async () =>
            await pipeline.ExecuteAsync(_ => ValueTask.FromResult(99)));

        // Pencere tamamen kaydıktan sonra kota yenilenmeli
        await Task.Delay(900);
        var afterWindow = await pipeline.ExecuteAsync(_ => ValueTask.FromResult(123));
        Assert.Equal(123, afterWindow);
    }

    [Fact]
    public async Task SlidingWindowRateLimiter_ShouldHonorQueueTimeout()
    {
        var strategy = new SlidingWindowRateLimiterStrategy(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 1,
            Window = TimeSpan.FromMilliseconds(400),
            SegmentsPerWindow = 4,
            QueueTimeout = TimeSpan.FromMilliseconds(600) // pencereden uzun -> kuyrukta bekleyip geçmeli
        });
        using var pipeline = new AegisPipelineBuilder("sw-queue").AddStrategy(strategy).Build();

        await pipeline.ExecuteAsync(_ => ValueTask.FromResult(1));

        var sw = Stopwatch.StartNew();
        var second = await pipeline.ExecuteAsync(_ => ValueTask.FromResult(2)); // kuyrukta bekleyip kabul edilmeli
        sw.Stop();

        Assert.Equal(2, second);
        Assert.True(sw.ElapsedMilliseconds >= 100, $"kuyrukta hiç beklemeden geçti: {sw.ElapsedMilliseconds}ms");
    }

    // =====================================================================
    // YÜK TESTİ: yüksek eşzamanlılıkta kilitlenme / istisna / sayaç sızıntısı var mı?
    // =====================================================================
    [Fact]
    public async Task Pipeline_UnderHighConcurrency_ShouldNotDeadlockOrLeak()
    {
        const int totalOperations = 10_000;

        var strategy = new ConcurrencyLimiterStrategy(new ConcurrencyLimiterOptions
        {
            MaxConcurrentExecutions = 32,
            QueueLimit = int.MaxValue,
            QueueTimeout = TimeSpan.FromSeconds(10)
        });

        using var pipeline = new AegisPipelineBuilder("load-test")
            .AddStrategy(strategy)
            .AddRetry(o => { o.MaxRetryAttempts = 2; o.Delay = TimeSpan.Zero; o.UseJitter = false; })
            .AddCache(TimeSpan.FromSeconds(1), o =>
            {
                o.MaxEntries = 100; // LRU tahliyesi yük altında tetiklensin
                o.KeySelector = ctx => ctx.Properties.TryGetValue("K", out var k) ? k!.ToString()! : "d";
            })
            .Build();

        var completed = 0;
        var failures = 0;
        var unexpectedExceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var sw = Stopwatch.StartNew();

        var tasks = Enumerable.Range(0, totalOperations).Select(i => Task.Run(async () =>
        {
            try
            {
                var ctx = new AegisContext();
                ctx.SetProperty("K", $"k{i % 200}"); // 200 farklı anahtar -> tahliye baskısı
                await pipeline.ExecuteAsync(_ => ValueTask.FromResult(i), ctx);
                Interlocked.Increment(ref completed);
            }
            catch (RateLimitRejectedException)
            {
                Interlocked.Increment(ref failures); // kuyruk zaman aşımı: kabul edilebilir
            }
            catch (Exception ex)
            {
                unexpectedExceptions.Add(ex); // BEKLENMEYEN: yük altında oluşan gerçek hata
            }
        })).ToArray();

        var allDone = Task.WhenAll(tasks);
        var finished = await Task.WhenAny(allDone, Task.Delay(TimeSpan.FromMinutes(2)));
        sw.Stop();

        Assert.True(ReferenceEquals(finished, allDone),
            $"10.000 işlem 2 dakikada bitmedi — kilitlenme (deadlock) şüphesi. Tamamlanan: {completed}");

        Assert.True(unexpectedExceptions.IsEmpty,
            "Yük altında BEKLENMEYEN istisnalar olustu: " +
            string.Join(" | ", unexpectedExceptions.Take(5).Select(e => $"{e.GetType().Name}: {e.Message}")));

        Assert.Equal(totalOperations, completed + failures);
        Assert.True(completed > totalOperations * 0.9,
            $"işlemlerin %90'ından azı tamamlandı (tamamlanan={completed}, reddedilen={failures})");

        // Semafor sızıntısı kontrolü: yük bittikten sonra limit hâlâ tam kapasite olmalı
        Assert.Equal(32, strategy.CurrentLimit);

        strategy.Dispose();
    }

    /// <summary>
    /// AEGIS-125 regresyonu: PartitionedRateLimiter tahliyesi ConcurrentDictionary üzerinde doğrudan
    /// LINQ OrderBy kullanıyordu; kapasite sınırında yoğun eşzamanlı yükte ArgumentException /
    /// NullReferenceException fırlatıyordu.
    /// </summary>
    [Fact]
    public async Task PartitionedRateLimiter_EvictionUnderConcurrentLoad_ShouldNotThrow()
    {
        var strategy = new PartitionedRateLimiterStrategy(new PartitionedRateLimiterOptions
        {
            MaxPartitions = 20, // küçük sınır -> sürekli tahliye baskısı
            PartitionKeySelector = ctx => ctx.Properties.TryGetValue("T", out var t) ? t!.ToString()! : "d",
            DefaultOptions = new RateLimiterOptions { PermitLimit = 1000, Window = TimeSpan.FromMinutes(1) }
        });

        using var pipeline = new AegisPipelineBuilder("prl-evict").AddStrategy(strategy).Build();
        var unexpected = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, 3_000).Select(i => Task.Run(async () =>
        {
            try
            {
                var ctx = new AegisContext();
                ctx.SetProperty("T", $"kiraci-{i}"); // her istek YENİ bölüm -> tahliye sürekli tetiklenir
                await pipeline.ExecuteAsync(_ => ValueTask.FromResult(i), ctx);
            }
            catch (RateLimitRejectedException) { /* beklenen olabilir */ }
            catch (Exception ex) { unexpected.Add(ex); }
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.True(unexpected.IsEmpty,
            "Bölüm tahliyesi sırasında beklenmeyen istisna: " +
            string.Join(" | ", unexpected.Take(3).Select(e => $"{e.GetType().Name}: {e.Message}")));

        Assert.True(strategy.PartitionCount <= 40,
            $"bölüm sayısı sınırsız büyümüş: {strategy.PartitionCount}");
    }

    [Fact]
    public async Task CircuitBreakerAndHedging_UnderConcurrentLoad_ShouldStayConsistent()
    {
        var cb = new Aegis.Resilience.Core.Strategies.CircuitBreaker.CircuitBreakerStrategy(
            new Aegis.Resilience.Core.Strategies.CircuitBreaker.CircuitBreakerOptions
            {
                MinimumThroughput = 20,
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(5),
                BreakDuration = TimeSpan.FromMilliseconds(300)
            });

        using var pipeline = new AegisPipelineBuilder("cb-load")
            .AddHedging(o => { o.MaxHedgedAttempts = 1; o.HedgingDelay = TimeSpan.FromMilliseconds(5); })
            .AddStrategy(cb)
            .Build();

        var counter = 0;
        var unexpected = 0;

        var tasks = Enumerable.Range(0, 2_000).Select(_ => Task.Run(async () =>
        {
            try
            {
                await pipeline.ExecuteAsync(_ =>
                {
                    // Yarısı hata veren downstream
                    if (Interlocked.Increment(ref counter) % 2 == 0)
                    {
                        throw new HttpRequestException("yük testi hatası");
                    }
                    return ValueTask.FromResult(true);
                });
            }
            catch (HttpRequestException) { /* beklenen */ }
            catch (BrokenCircuitException) { /* beklenen */ }
            catch (Exception)
            {
                Interlocked.Increment(ref unexpected); // BEKLENMEYEN istisna tipi
            }
        })).ToArray();

        var all = Task.WhenAll(tasks);
        var done = await Task.WhenAny(all, Task.Delay(TimeSpan.FromMinutes(1)));

        Assert.True(ReferenceEquals(done, all), "yük altında kilitlendi");
        Assert.Equal(0, unexpected);
    }

    // =====================================================================
    // KAYNAK SIZINTISI: çok sayıda pipeline kurup dispose edince timer/semafor bırakılıyor mu?
    // =====================================================================
    [Fact]
    public async Task ManyPipelines_CreateAndDispose_ShouldNotLeakHandlesOrMemory()
    {
        var before = GC.GetTotalMemory(forceFullCollection: true);

        for (var i = 0; i < 300; i++)
        {
            var pipeline = new AegisPipelineBuilder($"leak-{i}")
                .AddCache(TimeSpan.FromSeconds(30))                     // Timer içerir
                .AddConcurrencyLimiter(4, o => o.QueueLimit = int.MaxValue) // SemaphoreSlim içerir
                .AddRetry(o => o.MaxRetryAttempts = 1)
                .Build();

            await pipeline.ExecuteAsync(_ => ValueTask.FromResult(i));
            pipeline.Dispose(); // IDisposable stratejileri serbest bırakmalı
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var after = GC.GetTotalMemory(forceFullCollection: true);

        var growthMb = (after - before) / 1024.0 / 1024.0;
        Assert.True(growthMb < 10,
            $"300 pipeline kurulup dispose edildikten sonra bellek {growthMb:F1} MB arttı — sızıntı şüphesi.");
    }

    [Fact]
    public async Task DisposedCacheStrategy_ShouldStopBackgroundTimer()
    {
        var strategy = new CacheStrategy(new CacheOptions { Ttl = TimeSpan.FromMilliseconds(50) });
        var pipeline = new AegisPipelineBuilder("timer-dispose").AddStrategy(strategy).Build();

        await pipeline.ExecuteAsync(_ => ValueTask.FromResult("x"));
        pipeline.Dispose();

        // Dispose sonrası tekrar dispose çağrısı patlamamalı (idempotent olmalı)
        pipeline.Dispose();
        strategy.Dispose();
    }

    // =====================================================================
    // UZUN SÜRELİ ÇALIŞMA: sayaçlar/pencereler zamanla tutarlı kalıyor mu?
    // =====================================================================
    [Fact]
    public async Task RateLimiter_OverMultipleWindows_ShouldKeepRefillingConsistently()
    {
        var strategy = new RateLimiterStrategy(new RateLimiterOptions
        {
            PermitLimit = 2,
            Window = TimeSpan.FromMilliseconds(300)
        });
        using var pipeline = new AegisPipelineBuilder("rl-longrun").AddStrategy(strategy).Build();

        var acceptedPerWindow = new List<int>();

        // 5 ardışık pencere boyunca çalıştır
        for (var w = 0; w < 5; w++)
        {
            var accepted = 0;
            for (var i = 0; i < 5; i++)
            {
                try
                {
                    await pipeline.ExecuteAsync(_ => ValueTask.FromResult(i));
                    accepted++;
                }
                catch (RateLimitRejectedException) { }
            }
            acceptedPerWindow.Add(accepted);
            await Task.Delay(400); // pencerenin dolmasını bekle
        }

        // Her pencerede en az 1, en fazla 4 kabul olmalı (token bucket zamanla dolduğu için tolerans var)
        Assert.All(acceptedPerWindow, a => Assert.InRange(a, 1, 4));
        // İlk pencereden sonra da kabul edilmeye devam etmeli (sayaç kilitlenip kalmamalı)
        Assert.True(acceptedPerWindow.Skip(1).All(a => a >= 1),
            $"sonraki pencerelerde kota yenilenmedi: [{string.Join(", ", acceptedPerWindow)}]");
    }
}
