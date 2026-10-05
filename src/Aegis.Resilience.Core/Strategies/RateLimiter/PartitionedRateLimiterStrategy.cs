using System.Collections.Concurrent;
using System.Diagnostics;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Strategies.RateLimiter;

/// <summary>
/// Çok kiracılı (Multi-Tenant) mimarilerde kiracı, müşteri veya IP bazlı izole hız sınırlaması sağlayan,
/// Cardinality patlamalarını ve bellek sızıntılarını LRU/tahliye sınırıyla önleyen Partitioned Rate Limiter stratejisi.
/// </summary>
public sealed class PartitionedRateLimiterStrategy : AegisStrategy
{
    private sealed class PartitionEntry
    {
        public RateLimiterStrategy Limiter { get; }
        public long LastAccessedTimestamp;

        public PartitionEntry(RateLimiterStrategy limiter)
        {
            Limiter = limiter;
            LastAccessedTimestamp = Stopwatch.GetTimestamp();
        }
    }

    private readonly PartitionedRateLimiterOptions _staticOptions;

    /// <inheritdoc />
    public override object? Options => _staticOptions;
    private readonly ConcurrentDictionary<string, PartitionEntry> _limiters = new(StringComparer.Ordinal);
    private readonly object _pruneLock = new();

    private PartitionedRateLimiterOptions? _lastGoodOptions;

    /// <summary>Canlı seçenekleri doğrulayarak çözer; geçersizse son geçerli seçeneklerle devam eder (AEGIS-145).</summary>
    private PartitionedRateLimiterOptions ResolveOptions() => DynamicOptionsResolver.Resolve(_staticOptions, _staticOptions.OptionsProvider, static o => o.Validate(), ref _lastGoodOptions);

    public override string Name => "PartitionedRateLimiter";

    /// <summary>
    /// Aktif bölüm (partition) sayısını döner.
    /// </summary>
    public int PartitionCount => _limiters.Count;

    public PartitionedRateLimiterStrategy(PartitionedRateLimiterOptions options)
    {
        _staticOptions = options ?? throw new ArgumentNullException(nameof(options));
        _staticOptions.Validate(); // fail-fast: geçersiz yapılandırma kurulum anında bildirilir (AEGIS-130)
    }

    protected override ValueTask<Outcome<TResult>> ExecuteCoreAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        var options = ResolveOptions();
        var partitionKey = options.PartitionKeySelector?.Invoke(context)
            ?? (context.TryGetProperty<object>(AegisContextKeys.PartitionKey, out var pKey) && pKey != null
                ? pKey.ToString()!
                : context.PipelineName ?? "default_partition");

        var maxPartitions = Math.Max(1, options.MaxPartitions);

        var entry = _limiters.GetOrAdd(partitionKey, key =>
        {
            var limiterOptions = options.OptionsFactory?.Invoke(key) ?? options.DefaultOptions;
            return new PartitionEntry(new RateLimiterStrategy(limiterOptions));
        });

        Interlocked.Exchange(ref entry.LastAccessedTimestamp, Stopwatch.GetTimestamp());

        // Cardinality koruması, eklemeden SONRA uygulanır (AEGIS-126).
        EnforceMaxPartitions(maxPartitions);

        return entry.Limiter.ExecuteOutcomeAsync(callback, context, state);
    }

    /// <summary>
    /// Bölüm sayısını üst sınırın altında tutar; sınır aşıldığında EN UZUN SÜREDİR kullanılmayan
    /// bölümleri (LRU) tahliye eder.
    /// <para>
    /// AEGIS-126: Önceki sürümde tahliye (a) <c>Monitor.TryEnter</c> ile yapıldığı için yoğun eşzamanlı
    /// yükte çoğu çağrı tahliyeyi TAMAMEN atlıyor, (b) atlamadığında da yalnızca sınırın %20'si kadar
    /// kayıt siliyordu. Sonuç: <c>MaxPartitions=20</c> iken 3.000 farklı kiracıyla yapılan yük testinde
    /// sözlük 2.772 kayda kadar büyüyordu — yani rastgele kiracı kimliğiyle gelen istekler bellek
    /// tüketimini sınırsız artırabiliyordu (DoS). Artık gerçek kilit alınır ve sınırın altına İNİLİR.
    /// </para>
    /// </summary>
    private void EnforceMaxPartitions(int maxPartitions)
    {
        if (_limiters.Count <= maxPartitions)
        {
            return; // hızlı yol: kilit almadan çık
        }

        lock (_pruneLock)
        {
            // Hedef: sınırın %80'i. Her eklemede tekrar tahliye yapmamak için sınırın biraz altına inilir.
            var target = Math.Max(1, (int)(maxPartitions * 0.8));
            var removeCount = _limiters.Count - target;
            if (removeCount <= 0)
            {
                return;
            }

            // DİKKAT: ConcurrentDictionary üzerinde doğrudan LINQ OrderBy KULLANILMAZ (AEGIS-125).
            // OrderBy kaynağı ICollection görüp Count kadar dizi ayırır ve CopyTo çağırır; sözlük
            // bu sırada değişirse ArgumentException veya NullReferenceException fırlatır.
            // ConcurrentDictionary.ToArray() kilit alarak ATOMİK anlık görüntü verir.
            var oldestKeys = _limiters.ToArray()
                .OrderBy(kv => Volatile.Read(ref kv.Value.LastAccessedTimestamp))
                .Take(removeCount)
                .Select(kv => kv.Key);

            foreach (var key in oldestKeys)
            {
                _limiters.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Belirtilen bölüme ait hız sınırlayıcıyı sıfırlar.
    /// </summary>
    public bool Invalidate(string partitionKey) => _limiters.TryRemove(partitionKey, out _);

    /// <summary>
    /// Tüm bölümlerin hız sınırlayıcılarını sıfırlar.
    /// </summary>
    public void Clear() => _limiters.Clear();
}
