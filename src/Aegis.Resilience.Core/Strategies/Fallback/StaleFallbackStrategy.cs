using System.Diagnostics;
using System.Collections.Concurrent;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Strategies.Fallback;

/// <summary>
/// Dış servis çöktüğünde veya geciktiğinde, önceden başarıyla alınmış son veriyi (Stale Data)
/// kullanıcının önüne kesintisiz sunan ve opsiyonel olarak arka planda yenileyen FinTech seviyesi dayanıklılık stratejisi.
/// </summary>
public sealed class StaleFallbackStrategy : AegisStrategy
{
    private readonly StaleFallbackOptions _options;

    /// <inheritdoc />
    public override object? Options => _options;
    // CachedAt: monotonik Stopwatch zaman damgası — duvar saati sıçramalarından etkilenmez (AEGIS-144)
    private readonly ConcurrentDictionary<string, (object? Value, long CachedAt)> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _activeBackgroundRefreshes = new(StringComparer.Ordinal);
    private readonly object _pruneLock = new();

    public override string Name => "StaleFallback";

    public StaleFallbackStrategy(StaleFallbackOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    protected override async ValueTask<Outcome<TResult>> ExecuteCoreAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        var cacheKey = _options.KeyGenerator?.Invoke(context) ?? context.PipelineName ?? "default";
        var now = GetTimestamp();

        // 1. Stale-While-Revalidate desteği (FreshnessDuration tanımlıysa)
        if (_options.FreshnessDuration.HasValue &&
            _cache.TryGetValue(cacheKey, out var existingEntry) &&
            existingEntry.Value is TResult existingResult)
        {
            var age = TimeProvider.GetElapsedTime(existingEntry.CachedAt, now);
            if (age <= _options.FreshnessDuration.Value)
            {
                context.SetProperty(StaleFallbackOptions.IsStaleDataKey, false);
                return Outcome<TResult>.FromResult(existingResult);
            }

            if (age <= _options.MaxStaleAge)
            {
                // Kullanıcıyı bekletmeden anında bayat veriyi dön, arka planda tazelemeyi başlat (AEGIS-009)
                context.SetProperty(StaleFallbackOptions.IsStaleDataKey, true);
                Telemetry.Report(AegisEventNames.OnStaleFallback, AegisEventSeverity.Warning, context);

                if (AfterStaleReadForTesting is { } hook)
                {
                    await hook().ConfigureAwait(context.ContinueOnCapturedContext);
                }

                // Cache Stampede koruması: Aynı anahtar için aynı anda yalnızca 1 arka plan yenilemesi çalıştırılır (AEGIS-104)
                if (_activeBackgroundRefreshes.TryAdd(cacheKey, 0) && IsRefreshStillNeeded(cacheKey, existingEntry.CachedAt))
                {
                    // CreateChild: CorrelationId ve Properties (tenant/cache anahtarları) arka plan yenilemesinde de korunur (AEGIS-142).
                    // Arka plan bağlamı, çağıranın (havuza dönebilecek) bağlamından ÖNCE, şimdi kopyalanır.
                    var bgContext = context.CreateChild(CancellationToken.None);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var fresh = await callback(bgContext, state).ConfigureAwait(bgContext.ContinueOnCapturedContext);
                            if (fresh.IsSuccess)
                            {
                                SaveToCache(cacheKey, fresh.Result); // başarısız yenileme kullanıcıyı etkilemez
                            }
                        }
#pragma warning disable CA1031 // Arka plan yenilemesindeki beklenmeyen hata da kullanıcıyı etkilememeli
                        catch
#pragma warning restore CA1031
                        {
                        }
                        finally
                        {
                            _activeBackgroundRefreshes.TryRemove(cacheKey, out _);
                        }
                    });
                }

                return Outcome<TResult>.FromResult(existingResult);
            }
        }

        var outcome = await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);

        if (outcome.Exception is not { } exception)
        {
            // Başarılı sonucu önbelleğe al
            SaveToCache(cacheKey, outcome.Result);
            context.SetProperty(StaleFallbackOptions.IsStaleDataKey, false);
            return outcome;
        }

        // Hata alındı: Önbellekte geçerli bir bayat veri var mı kontrol et
        if (ShouldHandle(exception) &&
            _cache.TryGetValue(cacheKey, out var entry) &&
            TimeProvider.GetElapsedTime(entry.CachedAt) <= _options.MaxStaleAge &&
            entry.Value is TResult cachedResult)
        {
            context.SetProperty(StaleFallbackOptions.IsStaleDataKey, true);
            Telemetry.Report(AegisEventNames.OnStaleFallback, AegisEventSeverity.Warning, context);
            return Outcome<TResult>.FromResult(cachedResult);
        }

        return outcome;
    }

    /// <summary>
    /// AEGIS-159: Bayat girdi okunduktan sonra yenileme hakkı alınana kadar geçen sürede başka bir yenileme TAMAMLANMIŞ
    /// olabilir (iş parçacığı tam bu arada askıya alınırsa). Eskiden eski anlık görüntüye bakıp önbellek zaten
    /// tazeyken ikinci bir yenileme başlatılıyordu ("anahtar başına tek yenileme" garantisi bozuluyordu).
    /// Girdi değiştiyse hak geri bırakılır ve yenileme yapılmaz.
    /// </summary>
    private bool IsRefreshStillNeeded(string cacheKey, long observedCachedAt)
    {
        if (_cache.TryGetValue(cacheKey, out var latest) && latest.CachedAt != observedCachedAt)
        {
            _activeBackgroundRefreshes.TryRemove(cacheKey, out _);
            return false;
        }

        return true;
    }

    /// <summary>Test kancası: bayat girdi okunduktan sonra, yenileme hakkı alınmadan hemen önce beklenir (yarışı deterministik üretmek için).</summary>
    internal Func<Task>? AfterStaleReadForTesting { get; set; }

    private void SaveToCache(string key, object? value)
    {
        if (_cache.Count >= _options.MaxCacheEntries && !_cache.ContainsKey(key))
        {
            if (Monitor.TryEnter(_pruneLock))
            {
                try
                {
                    if (_cache.Count >= _options.MaxCacheEntries)
                    {
                        var now = GetTimestamp();
                        var expiredKeys = _cache
                            .Where(kv => TimeProvider.GetElapsedTime(kv.Value.CachedAt, now) > _options.MaxStaleAge)
                            .Select(kv => kv.Key)
                            .Take(Math.Max(10, _options.MaxCacheEntries / 5))
                            .ToList();

                        if (expiredKeys.Count == 0)
                        {
                            expiredKeys = _cache.Keys.Take(Math.Max(10, _options.MaxCacheEntries / 5)).ToList();
                        }

                        foreach (var k in expiredKeys)
                        {
                            _cache.TryRemove(k, out _);
                        }
                    }
                }
                finally
                {
                    Monitor.Exit(_pruneLock);
                }
            }
        }

        _cache[key] = (value, GetTimestamp());
    }

    private bool ShouldHandle(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return false;
        }

        return _options.ShouldHandle?.Invoke(ex) ?? true;
    }
}
