using System.Collections.Concurrent;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;

namespace Aegis.Resilience.Extensions.DependencyInjection;

/// <summary>
/// Generic anahtarlı boru hattı kayıt defteri (Polly: <c>ResiliencePipelineRegistry&lt;TKey&gt;</c>).
/// <list type="bullet">
/// <item>Statik tanım: <see cref="TryAddBuilder"/> ile anahtar başına ayrı yapılandırma.</item>
/// <item>Dinamik anahtar: <see cref="DynamicBuilder"/> ile önceden bilinmeyen anahtarlar için (ör. kiracı, uç nokta) ilk
/// erişimde kurulum. Her anahtar ayrı strateji örnekleri (ayrı devre kesici / kota) alır.</item>
/// <item>Kardinalite koruması: <see cref="MaxDynamicPipelines"/> dinamik anahtar sayısını sınırlar (saldırgan rastgele anahtarla
/// bellek şişiremez). Polly'de bu sınır yoktur.</item>
/// </list>
/// Registry dispose edilince kurduğu tüm boru hatlarını dispose eder.
/// </summary>
public sealed class AegisPipelineRegistry<TKey> : IAegisPipelineProvider<TKey>, IDisposable, IAsyncDisposable
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Lazy<IAegisPipeline>> _pipelines;
    private readonly ConcurrentDictionary<TKey, Action<IAegisPipelineBuilder, TKey>> _builders;
    private readonly Func<TKey, string> _nameFormatter;
    private int _dynamicCount;
    private volatile bool _disposed;

    public AegisPipelineRegistry(IEqualityComparer<TKey>? comparer = null, Func<TKey, string>? nameFormatter = null)
    {
        _pipelines = new ConcurrentDictionary<TKey, Lazy<IAegisPipeline>>(comparer ?? EqualityComparer<TKey>.Default);
        _builders = new ConcurrentDictionary<TKey, Action<IAegisPipelineBuilder, TKey>>(comparer ?? EqualityComparer<TKey>.Default);
        _nameFormatter = nameFormatter ?? (static key => key.ToString() ?? "default");
    }

    /// <summary>Tanımı olmayan anahtarlar için yapılandırma (null ise yalnızca tanımlı anahtarlar çözülür).</summary>
    public Action<IAegisPipelineBuilder, TKey>? DynamicBuilder { get; set; }

    /// <summary>Dinamik olarak kurulabilecek en fazla boru hattı sayısı (varsayılan 10.000). Aşılırsa <see cref="InvalidOperationException"/>.</summary>
    public int MaxDynamicPipelines { get; set; } = 10_000;

    /// <summary>Kurulan boru hatlarına uygulanacak ortak builder ayarı (ör. telemetri, TimeProvider).</summary>
    public Action<AegisPipelineBuilder, TKey>? ConfigureBuilder { get; set; }

    /// <summary>Anahtar için yapılandırma ekler. Anahtar zaten tanımlıysa false.</summary>
    public bool TryAddBuilder(TKey key, Action<IAegisPipelineBuilder, TKey> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _builders.TryAdd(key, configure);
    }

    /// <summary>Anahtarın boru hattını döner; yoksa verilen yapılandırmayla kurar (Polly: <c>GetOrAddPipeline</c>).</summary>
    public IAegisPipeline GetOrAddPipeline(TKey key, Action<IAegisPipelineBuilder, TKey> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Resolve(key, configure, countsAsDynamic: false);
    }

    /// <inheritdoc />
    public IAegisPipeline GetPipeline(TKey key) =>
        TryGetPipeline(key, out var pipeline) && pipeline is not null
            ? pipeline
            : throw new KeyNotFoundException($"'{_nameFormatter(key)}' anahtarı için Aegis boru hattı tanımlı değil ve dinamik üretici yok.");

    /// <inheritdoc />
    public bool TryGetPipeline(TKey key, out IAegisPipeline? pipeline)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pipelines.TryGetValue(key, out var existing))
        {
            pipeline = existing.Value;
            return true;
        }

        if (_builders.TryGetValue(key, out var configure))
        {
            pipeline = Resolve(key, configure, countsAsDynamic: false);
            return true;
        }

        if (DynamicBuilder is { } dynamicBuilder)
        {
            pipeline = Resolve(key, dynamicBuilder, countsAsDynamic: true);
            return true;
        }

        pipeline = null;
        return false;
    }

    private IAegisPipeline Resolve(TKey key, Action<IAegisPipelineBuilder, TKey> configure, bool countsAsDynamic)
    {

        var lazy = _pipelines.GetOrAdd(key, k => new Lazy<IAegisPipeline>(() =>
        {
            if (countsAsDynamic && Interlocked.Increment(ref _dynamicCount) > MaxDynamicPipelines)
            {
                Interlocked.Decrement(ref _dynamicCount);
                throw new InvalidOperationException(
                    $"Dinamik boru hattı sınırı ({MaxDynamicPipelines}) aşıldı. Anahtar kardinalitesini kontrol edin veya MaxDynamicPipelines'ı artırın.");
            }

            var builder = new AegisPipelineBuilder(_nameFormatter(k));
            ConfigureBuilder?.Invoke(builder, k);
            configure(builder, k);
            return builder.Build();
        }, LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return lazy.Value;
        }
        catch
        {
            // AEGIS-139 ile aynı: başarısız kurulum önbelleklenmez, sonraki çağrı yeniden dener.
            ((ICollection<KeyValuePair<TKey, Lazy<IAegisPipeline>>>)_pipelines).Remove(new KeyValuePair<TKey, Lazy<IAegisPipeline>>(key, lazy));
            throw;
        }
    }

    /// <summary>Kurulmuş boru hatlarının anahtarları.</summary>
    public IReadOnlyCollection<TKey> Keys => [.. _pipelines.Keys];

    /// <summary>Kayıt defterini ve boru hatlarını bırakır (<c>await using</c> için; Polly: <c>ResiliencePipelineRegistry.DisposeAsync</c>).</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var lazy in _pipelines.Values)
        {
            if (lazy.IsValueCreated)
            {
                lazy.Value.Dispose();
            }
        }

        _pipelines.Clear();
    }
}
