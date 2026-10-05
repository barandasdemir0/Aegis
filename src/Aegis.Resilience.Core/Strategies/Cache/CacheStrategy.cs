using System.Diagnostics;
using System.Collections.Concurrent;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Strategies.Cache;

/// <summary>
/// Sıfır harici bağımlılıkla yüksek performanslı in-memory Cache-Aside dayanıklılık stratejisi.
/// Süresi dolan girişler hem erişim anında (lazy eviction) hem de periyodik arka plan taramasıyla (scavenging) temizlenir.
/// </summary>
public sealed class CacheStrategy : AegisStrategy, IDisposable
{
    /// <summary>
    /// Önbellek girdisi. <see cref="LastAccessedTicks"/> alanı LRU (Least Recently Used) tahliyesi için
    /// her isabette güncellenir; kapasite dolduğunda EN UZUN SÜREDİR kullanılmayan girdiler atılır (AEGIS-123).
    /// </summary>
    private sealed class CacheEntry
    {
        public required object? Value { get; init; }

        /// <summary>Kayan sürede her isabette eklenen süre (zaman damgası birimi); 0 ise sabit süre.</summary>
        public required long SlidingTicks { get; init; }

        /// <summary>Monotonik (Stopwatch) son kullanma zaman damgası — duvar saati değişimlerinden etkilenmez (AEGIS-144). Kayan sürede isabetle ileri alınır.</summary>
        public long ExpiresAtTimestamp;
        public long LastAccessedTicks;
    }

    private readonly CacheOptions _staticOptions;

    /// <inheritdoc />
    public override object? Options => _staticOptions;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly Timer _scavengeTimer;
    private readonly object _evictionLock = new();

    private CacheOptions? _lastGoodOptions;

    /// <summary>Canlı seçenekleri doğrulayarak çözer; geçersizse son geçerli seçeneklerle devam eder (AEGIS-145).</summary>
    private CacheOptions ResolveOptions() => DynamicOptionsResolver.Resolve(_staticOptions, _staticOptions.OptionsProvider, static o => o.Validate(), ref _lastGoodOptions);

    public override string Name => "Cache";

    /// <summary>
    /// Önbellekte hâlihazırda tutulan girdi sayısı (izleme/teşhis amaçlı).
    /// </summary>
    public int EntryCount => _cache.Count;

    public CacheStrategy(CacheOptions options)
    {
        _staticOptions = options ?? throw new ArgumentNullException(nameof(options));
        _staticOptions.Validate(); // fail-fast: geçersiz yapılandırma kurulum anında bildirilir (AEGIS-130)

        // Periyodik arka plan taraması: varsayılan olarak TTL'nin 2 katı veya en az 30 saniye aralıkla çalışır
        var scavengeInterval = TimeSpan.FromMilliseconds(Math.Min(AegisTimers.MaxDuration.TotalMilliseconds, Math.Max(30_000, options.Ttl.TotalMilliseconds * 2)));
        _scavengeTimer = new Timer(_ => EvictExpiredEntries(), null, scavengeInterval, scavengeInterval);
    }

    // Bellek içi isabet (en sık yol) async değildir: sonuç doğrudan döner. Iskalama ve dış depo ExecuteLookupAsync'tedir.
    protected override ValueTask<Outcome<TResult>> ExecuteCoreAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        var options = ResolveOptions();
        var key = options.KeySelector?.Invoke(context)
            ?? (context.TryGetProperty<object>(AegisContextKeys.CacheKey, out var customKey) && customKey != null
                ? customKey.ToString()!
                : context.PipelineName ?? "default_cache_key");

        if (options.Store is null && TryGetFromMemory<TResult>(key) is { Found: true } hit)
        {
            return new ValueTask<Outcome<TResult>>(OnHit(options, key, hit.Value!, context));
        }

        return ExecuteLookupAsync(callback, context, state, options, key);
    }

    private Outcome<TResult> OnHit<TResult>(CacheOptions options, string key, TResult value, AegisContext context)
    {
        options.OnCacheHit?.Invoke(key, value);
        AegisTelemetry.CacheHitsTotal.Add(1, new KeyValuePair<string, object?>("pipeline", context.PipelineName ?? "default"));
        Telemetry.Report(AegisEventNames.OnCacheHit, AegisEventSeverity.Debug, context);
        return Outcome<TResult>.FromResult(value);
    }

    // Dış depoda okuma burada yapılır; bellek içi depoda ExecuteCoreAsync ıskalamayı zaten gördü.
    private async ValueTask<Outcome<TResult>> ExecuteLookupAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state,
        CacheOptions options,
        string key)
    {
        if (options.Store is { } store &&
            await TryGetFromStoreAsync<TResult>(store, options, key, context).ConfigureAwait(context.ContinueOnCapturedContext) is { Found: true } stored)
        {
            return OnHit(options, key, stored.Value!, context);
        }

        options.OnCacheMiss?.Invoke(key);
        AegisTelemetry.CacheMissesTotal.Add(1, new KeyValuePair<string, object?>("pipeline", context.PipelineName ?? "default"));
        Telemetry.Report(AegisEventNames.OnCacheMiss, AegisEventSeverity.Debug, context);

        var outcome = await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
        if (!outcome.IsSuccess)
        {
            return outcome; // hatalar önbelleklenmez
        }

        var result = outcome.Result;
        if (result is null && !options.CacheNulls)
        {
            return outcome;
        }

        var ttl = options.TtlGenerator?.Invoke(context, result) ?? options.Ttl;
        if (ttl <= TimeSpan.Zero)
        {
            return outcome; // üretici "önbelleğe alma" dedi (ya da sıfır süre)
        }

        var entryOptions = new AegisCacheEntryOptions(ttl, options.SlidingExpiration);
        if (options.Store is { } targetStore)
        {
            await SetInStoreAsync(targetStore, options, key, result, entryOptions, context).ConfigureAwait(context.ContinueOnCapturedContext);
        }
        else
        {
            SetInMemory(key, result, entryOptions, options.MaxEntries);
        }

        return outcome;
    }

    // AEGIS-144: TTL için duvar saati (UtcNow) yerine monotonik zaman damgası kullanılır. Duvar saati NTP düzeltmesi,
    // yaz saati veya elle değiştirme ile sıçrayabilir; saat 1 saat ileri alınınca TÜM önbellek anında "bayat"
    // oluyor, geri alınınca girdiler TTL'den saatlerce uzun yaşıyordu.
    private AegisCacheLookup<TResult> TryGetFromMemory<TResult>(string key)
    {
        if (!_cache.TryGetValue(key, out var entry))
        {
            return AegisCacheLookup.Miss<TResult>();
        }

        var now = GetTimestamp();
        if (Volatile.Read(ref entry.ExpiresAtTimestamp) <= now)
        {
            _cache.TryRemove(key, out _); // lazy eviction: süresi dolmuş girişi anında temizle
            return AegisCacheLookup.Miss<TResult>();
        }

        // LRU: bu girdiye erişildiğini işaretle, böylece kapasite dolduğunda son atılacaklardan olur
        Interlocked.Exchange(ref entry.LastAccessedTicks, now);
        if (entry.SlidingTicks > 0)
        {
            Volatile.Write(ref entry.ExpiresAtTimestamp, now + entry.SlidingTicks);
        }

        return AegisCacheLookup.Hit((TResult)entry.Value!);
    }

    private void SetInMemory<TResult>(string key, TResult value, AegisCacheEntryOptions entryOptions, int maxEntries)
    {
        // Süre, değer hazır olduğunda başlar (geri çağrının başladığı anda değil).
        var cachedAt = GetTimestamp();
        var ttlTicks = (long)(entryOptions.Ttl.TotalSeconds * TimeProvider.TimestampFrequency);
        _cache[key] = new CacheEntry
        {
            Value = value,
            SlidingTicks = entryOptions.SlidingExpiration ? ttlTicks : 0,
            ExpiresAtTimestamp = cachedAt + ttlTicks,
            LastAccessedTicks = cachedAt
        };

        // Kapasite denetimi eklemeden SONRA yapılır (AEGIS-126)
        EnforceMaxEntries(maxEntries);
    }

    // Depo hatası çağrıyı düşürmez (önbellek bir hızlandırıcıdır, doğruluk kaynağı değil): okuma hatası ıskalama sayılır.
    // Yalnızca çağıranın kendi iptali yükselir.
    private static async ValueTask<AegisCacheLookup<TResult>> TryGetFromStoreAsync<TResult>(
        IAegisCacheStore store, CacheOptions options, string key, AegisContext context)
    {
        try
        {
            return await store.TryGetAsync<TResult>(key, context.CancellationToken).ConfigureAwait(context.ContinueOnCapturedContext);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, context))
        {
            ReportStoreError(options, key, ex, context);
            return AegisCacheLookup.Miss<TResult>();
        }
    }

    private static async ValueTask SetInStoreAsync<TResult>(
        IAegisCacheStore store, CacheOptions options, string key, TResult value, AegisCacheEntryOptions entryOptions, AegisContext context)
    {
        try
        {
            await store.SetAsync(key, value, entryOptions, context.CancellationToken).ConfigureAwait(context.ContinueOnCapturedContext);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, context))
        {
            ReportStoreError(options, key, ex, context); // sonuç zaten elde; yazılamaması çağrıyı etkilemez
        }
    }

    private static bool IsCallerCancellation(Exception exception, AegisContext context) =>
        exception is OperationCanceledException && context.CancellationToken.IsCancellationRequested;

    private static void ReportStoreError(CacheOptions options, string key, Exception exception, AegisContext context)
    {
        AegisTelemetry.CountCallbackError(context.PipelineName, "CacheStore", exception);
        if (options.OnCacheError is { } onError)
        {
            AegisCallbacks.InvokeSafely(() => onError(key, exception), nameof(CacheOptions.OnCacheError));
        }
    }

    /// <summary>
    /// Önbellek girdi sayısını üst sınırın altında tutar; sınır aşıldığında önce süresi dolmuşları,
    /// gerekirse EN UZUN SÜREDİR KULLANILMAYAN (LRU) girdileri tahliye eder.
    /// <para>
    /// AEGIS-123: Eskiden <c>_cache.Keys.Take(...)</c> kullanılıyordu; ConcurrentDictionary anahtar sırası
    /// garantisiz olduğu için bu "en eski" değil RASTGELE tahliye anlamına geliyor, sık kullanılan sıcak
    /// girdiler atılabiliyordu.
    /// </para>
    /// <para>
    /// AEGIS-126: Tahliye kilitsiz ve yalnızca sınırın %10'u kadar yapıldığı için yoğun eşzamanlı yükte
    /// sözlük sınırın çok üzerine çıkabiliyordu. Artık kilit altında, sınırın altına İNİLENE kadar tahliye edilir.
    /// </para>
    /// </summary>
    private void EnforceMaxEntries(int maxEntries)
    {
        if (maxEntries <= 0 || _cache.Count <= maxEntries)
        {
            return; // hızlı yol: kilit almadan çık
        }

        lock (_evictionLock)
        {
            if (_cache.Count <= maxEntries)
            {
                return;
            }

            // Önce süresi dolanları temizle; çoğu zaman bu yeter.
            EvictExpiredEntries();

            // Hedef: sınırın %80'i. Her eklemede tekrar tahliye yapmamak için sınırın biraz altına inilir.
            var target = Math.Max(1, (int)(maxEntries * 0.8));
            var removeCount = _cache.Count - target;
            if (removeCount <= 0)
            {
                return;
            }

            // DİKKAT: ConcurrentDictionary üzerinde doğrudan LINQ OrderBy KULLANILMAZ (AEGIS-125).
            // OrderBy, kaynağı ICollection görüp Count kadar dizi ayırır ve CopyTo çağırır; sözlük
            // bu arada değişirse ArgumentException ("index is equal to or greater than length")
            // veya yarım dolu dizi yüzünden NullReferenceException fırlar — 10.000 eşzamanlı işlemli
            // yük testinde gerçekten gözlemlendi. ToArray() ise kilit alarak ATOMİK anlık görüntü üretir.
            var toRemove = _cache.ToArray()
                .OrderBy(kv => Volatile.Read(ref kv.Value.LastAccessedTicks))
                .Take(removeCount)
                .Select(kv => kv.Key);

            foreach (var k in toRemove)
            {
                _cache.TryRemove(k, out _);
            }
        }
    }

    private int _isScavenging;

    /// <summary>
    /// Tüm süresi dolmuş girişleri arka planda temizler (Scavenging).
    /// </summary>
    private void EvictExpiredEntries()
    {
        if (Interlocked.CompareExchange(ref _isScavenging, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var now = GetTimestamp();
            foreach (var kvp in _cache)
            {
                if (kvp.Value.ExpiresAtTimestamp <= now)
                {
                    _cache.TryRemove(kvp.Key, out _);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isScavenging, 0);
        }
    }

    /// <summary>
    /// Belirtilen anahtara ait bellek içi önbellek girdisini temizler. Dış depo (<see cref="CacheOptions.Store"/>) için
    /// <see cref="InvalidateAsync"/> kullanın.
    /// </summary>
    public bool Invalidate(string key) => _cache.TryRemove(key, out _);

    /// <summary>Anahtarı hem bellek içi depodan hem (tanımlıysa) dış depodan siler.</summary>
    public async ValueTask InvalidateAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        _cache.TryRemove(key, out _);
        if (ResolveOptions().Store is { } store)
        {
            await store.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tüm bellek içi önbelleği temizler (dış depo kendi ömür kurallarıyla yönetilir).
    /// </summary>
    public void Clear() => _cache.Clear();

    public void Dispose() => _scavengeTimer.Dispose();
}
