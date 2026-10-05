using System.Collections.Concurrent;
using Aegis.Resilience.Core.Abstractions;

namespace Aegis.Resilience.Extensions.DependencyInjection;

/// <summary>
/// Adlandırılmış boru hatlarını tembel (lazy) ve tekil olarak kuran kayıt defteri.
/// <para>
/// Yapılandırıcıdan (configurator) kurduğu boru hatlarının yaşam döngüsünün sahibidir: eşzamanlı
/// çağrılarda tek bir örnek üretir (<see cref="Lazy{T}"/>) ve Dispose edildiğinde — DI konteyneri
/// kapanırken — zamanlayıcı/semafor gibi kaynakları serbest bırakır (AEGIS-119).
/// Dışarıdan <see cref="RegisterPipeline"/> ile verilen boru hatları çağıranın sahipliğinde kalır.
/// </para>
/// </summary>
public sealed class AegisPipelineRegistry : IAegisPipelineRegistry, IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<IAegisPipeline>> _pipelines = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IAegisPipelineConfigurator> _configurators = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<IAegisPipeline> _ownedPipelines = new();
    private readonly object _ownershipLock = new();
    private readonly IServiceProvider? _serviceProvider;
    private volatile bool _disposed;

    public AegisPipelineRegistry() : this(null, null) { }

    public AegisPipelineRegistry(IServiceProvider? serviceProvider, IEnumerable<IAegisPipelineConfigurator>? configurators = null)
    {
        _serviceProvider = serviceProvider;
        if (configurators != null)
        {
            foreach (var configurator in configurators)
            {
                _configurators[configurator.Name] = configurator;
            }
        }
    }

    public IAegisPipeline GetPipeline(string name)
    {
        if (TryGetPipeline(name, out var pipeline) && pipeline != null)
        {
            return pipeline;
        }

        throw new KeyNotFoundException($"'{name}' adında bir Aegis Resilience boru hattı bulunamadı. Lütfen IServiceCollection üzerinden kaydettiğinizden emin olun.");
    }

    public bool TryGetPipeline(string name, out IAegisPipeline? pipeline)
    {
        ArgumentNullException.ThrowIfNull(name);
        // AEGIS-138: Dispose sonrası çağrı, configurator üzerinden YENİ bir boru hattı kurup kaynağı diriltiyordu
        // (Timer/Semaphore içeren stratejiler bir daha asla dispose edilmiyordu). Polly: Dispose_EnsureNotUsableAnymore.
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pipelines.TryGetValue(name, out var existing))
        {
            pipeline = existing.Value;
            return true;
        }

        if (!_configurators.TryGetValue(name, out var configurator))
        {
            pipeline = null;
            return false;
        }

        if (_serviceProvider == null)
        {
            throw new InvalidOperationException($"'{name}' boru hattını yapılandırmak için IServiceProvider gereklidir, ancak sağlanmadı.");
        }

        // Lazy: Yarış durumunda yalnızca tek bir boru hattı örneği kurulur; fazladan kurulan
        // örneklerin (Timer/SemaphoreSlim içeren stratejiler) sızması engellenir.
        var lazy = _pipelines.GetOrAdd(name, _ => new Lazy<IAegisPipeline>(
            () => TrackOwnership(configurator.Build(_serviceProvider)),
            LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            pipeline = lazy.Value;
            return true;
        }
        catch
        {
            // AEGIS-139: Lazy<T>, fabrika istisnasını SONSUZA KADAR önbellekler. Configurator tek bir kez patlarsa
            // (örn. açılışta yapılandırma servisi henüz hazır değil) o boru hattı uygulama ömrü boyunca ölü kalıyordu.
            // Zehirli Lazy sözlükten atomik olarak (yalnızca hâlâ aynı örnekse) kaldırılır; sonraki çağrı yeniden dener.
            ((ICollection<KeyValuePair<string, Lazy<IAegisPipeline>>>)_pipelines).Remove(
                new KeyValuePair<string, Lazy<IAegisPipeline>>(name, lazy));
            throw;
        }
    }

    public void RegisterPipeline(string name, IAegisPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(pipeline);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _pipelines[name] = new Lazy<IAegisPipeline>(() => pipeline);
    }

    public IReadOnlyDictionary<string, IAegisPipeline> GetAllPipelines()
    {
        // Henüz initialize edilmemiş configurator'ları da materialize ediyoruz
        foreach (var pair in _configurators)
        {
            if (!_pipelines.ContainsKey(pair.Key))
            {
                TryGetPipeline(pair.Key, out _);
            }
        }

        var snapshot = new Dictionary<string, IAegisPipeline>(_pipelines.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _pipelines)
        {
            snapshot[pair.Key] = pair.Value.Value;
        }

        return snapshot;
    }

    private IAegisPipeline TrackOwnership(IAegisPipeline pipeline)
    {
        lock (_ownershipLock)
        {
            _ownedPipelines.Add(pipeline);
        }

        return pipeline;
    }

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

        IAegisPipeline[] owned;
        lock (_ownershipLock)
        {
            owned = _ownedPipelines.ToArray();
            _ownedPipelines.Clear();
        }

        foreach (var pipeline in owned)
        {
            pipeline.Dispose();
        }

        _pipelines.Clear();
    }
}
