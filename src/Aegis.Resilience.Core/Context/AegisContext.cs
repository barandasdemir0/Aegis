using System.Collections.Concurrent;

namespace Aegis.Resilience.Core.Context;

/// <summary>
/// Dayanıklılık boru hattı (Pipeline) boyunca taşınan çalışma zamanı bağlamı.
/// Sıfır tahsisat (Zero-allocation) hedefiyle nesne havuzlama (Object Pooling) desteği sunar.
/// <para>
/// <see cref="Properties"/> sözlüğü eşzamanlı erişime karşı güvenlidir ve yalnızca ilk kullanımda
/// tahsis edilir; hiç özellik yazılmayan çağrılarda heap tahsisatı yapılmaz.
/// </para>
/// </summary>
public sealed class AegisContext
{
    private ConcurrentDictionary<string, object?>? _properties;
    private string? _correlationId;

    // Havuzdan kiralanan alt bağlamda ebeveynin kimliği henüz üretilmemişse kimlik ebeveynden TEMBEL okunur (hedging başarı
    // yolunda Guid + string tahsisi yapılmaz). Alt bağlam çağrıdan uzun yaşayabilecekse DetachFromParent ile sabitlenir.
    private AegisContext? _correlationParent;

    public CancellationToken CancellationToken { get; set; }
    public string? PipelineName { get; set; }

    /// <summary>
    /// Çağrı korelasyon kimliği. Sıfır tahsisat (Zero-allocation) hedefiyle tembel (lazy) oluşturulur;
    /// okunmadığı sürece heap üzerinde string tahsis edilmez (AEGIS-109). Alt bağlamlar ebeveynle aynı kimliği taşır.
    /// </summary>
    public string CorrelationId
    {
        get => _correlationId ??= _correlationParent?.CorrelationId ?? Guid.NewGuid().ToString("N");
        set => _correlationId = value;
    }

    /// <summary>
    /// Havuzdan kiralanmış bu bağlamı <paramref name="parent"/>'ın alt bağlamı olarak kurar (<see cref="CreateChild"/> ile aynı
    /// içerik: iptal token'ı, ad, işlem anahtarı, bağlam bayrağı, özellik kopyası, ortak korelasyon kimliği).
    /// </summary>
    internal void InitializeAsChildOf(AegisContext parent, CancellationToken cancellationToken)
    {
        CancellationToken = cancellationToken;
        PipelineName = parent.PipelineName;
        OperationKey = parent.OperationKey;
        ContinueOnCapturedContext = parent.ContinueOnCapturedContext;

        if (parent._correlationId is { } id)
        {
            _correlationId = id;
        }
        else
        {
            _correlationParent = parent;
        }

        if (parent._properties is { IsEmpty: false } parentProperties)
        {
            var target = Properties;
            foreach (var pair in parentProperties)
            {
                target[pair.Key] = pair.Value;
            }
        }
    }

    /// <summary>
    /// Kimliği ebeveynden okuyup sabitler ve bağı koparır. Alt bağlam ebeveynden (havuza dönebilecek) uzun yaşayabilecekse
    /// çağrılır; aksi halde havuza dönüp yeniden kullanılan bir ebeveynin kimliği okunabilirdi.
    /// </summary>
    internal void DetachFromParent()
    {
        if (_correlationParent is { } parent)
        {
            _correlationId ??= parent.CorrelationId;
            _correlationParent = null;
        }
    }

    // Tek bir isteğin özellikleri nadiren eşzamanlı yazılır: varsayılan eşzamanlılık seviyesi (çekirdek sayısı kadar kilit)
    // yerine 1 kilit. İş parçacığı güvenliği aynen korunur; oluşturma ve özellikle havuza iadedeki Clear() çok ucuzlar
    // (Clear tüm kilitleri alır; 28 çekirdekte her çağrıya ~900 ns ekliyordu — benchmark ile ölçüldü).
    public IDictionary<string, object?> Properties => _properties ??= new ConcurrentDictionary<string, object?>(concurrencyLevel: 1, capacity: 4, StringComparer.Ordinal);

    public AegisContext(CancellationToken cancellationToken = default, string? pipelineName = null)
    {
        CancellationToken = cancellationToken;
        PipelineName = pipelineName;
    }

    public static AegisContext Create(CancellationToken cancellationToken = default, string? pipelineName = null)
        => new(cancellationToken, pipelineName);

    public bool TryGetProperty<T>(string key, out T? value)
    {
        if (_properties != null && _properties.TryGetValue(key, out var raw) && raw is T casted)
        {
            value = casted;
            return true;
        }

        value = default;
        return false;
    }

    public void SetProperty<T>(string key, T value)
    {
        Properties[key] = value;
    }

    /// <summary>Tipli anahtarla özelliği okur (Polly: <c>ResilienceProperties.TryGetValue</c>).</summary>
    public bool TryGetProperty<T>(AegisPropertyKey<T> key, out T? value) => TryGetProperty(key.Key, out value);

    /// <summary>Tipli anahtarla özelliği okur; yoksa <paramref name="defaultValue"/> döner.</summary>
    public T GetPropertyOrDefault<T>(AegisPropertyKey<T> key, T defaultValue) =>
        TryGetProperty(key.Key, out T? value) ? value! : defaultValue;

    /// <summary>Tipli anahtarla özelliği yazar.</summary>
    public void SetProperty<T>(AegisPropertyKey<T> key, T value) => Properties[key.Key] = value;

    /// <summary>
    /// İşlem anahtarı (Polly: <c>ResilienceContext.OperationKey</c>): aynı boru hattını kullanan farklı işlemleri
    /// (ör. "GetOrder", "CreatePayment") telemetride ayırmak için kullanılır. Alt bağlamlara aktarılır.
    /// </summary>
    public string? OperationKey { get; set; }

    /// <summary>
    /// Stratejilerin kendi <c>await</c>'lerinden sonra yakalanan senkronizasyon bağlamında devam edip etmeyeceği
    /// (Polly: <c>ResilienceContext.ContinueOnCapturedContext</c>). Varsayılan <c>false</c> (sunucu uygulamaları için en
    /// hızlısı). UI / eski ASP.NET gibi bağlam gerektiren ortamlarda <c>true</c> verildiğinde, ör. retry gecikmesinden
    /// sonraki deneme de özgün bağlamda çalışır.
    /// </summary>
    public bool ContinueOnCapturedContext { get; set; }

    /// <summary>
    /// Paralel yürütülen stratejiler (Hedging, Pessimistic Timeout vb.) için bu bağlamdan türetilmiş
    /// izole bir alt bağlam (child context) üretir. CorrelationId ve mevcut Properties kopyalanır;
    /// böylece alt akıştaki stratejiler (Cache, PartitionedRateLimiter vb.) anahtarlarını kaybetmez (AEGIS-110).
    /// </summary>
    public AegisContext CreateChild(CancellationToken cancellationToken)
    {
        // AEGIS-135: Alan yerine ÖZELLİK okunur. Ebeveyn CorrelationId'ye henüz erişmemişse alan null'dır;
        // null kopyalanınca her alt bağlam kendi rastgele kimliğini üretiyor ve hedging denemeleri
        // ebeveynle/birbirleriyle korelasyonunu kaybediyordu (Polly: EveryHedgedTaskShouldHaveDifferentContexts
        // + EnsurePrimaryContextFlows). Özellik okuması kimliği ebeveynde TEK SEFERDE üretip sabitler.
        var child = new AegisContext(cancellationToken, PipelineName)
        {
            _correlationId = CorrelationId,
            OperationKey = OperationKey,
            ContinueOnCapturedContext = ContinueOnCapturedContext
        };

        if (_properties is { IsEmpty: false })
        {
            child._properties = new ConcurrentDictionary<string, object?>(_properties, StringComparer.Ordinal);
        }

        return child;
    }

    /// <summary>
    /// Alt bağlamda (child context) üretilen özellikleri bu bağlama geri birleştirir.
    /// Kazanan denemenin yazdığı bilgilerin (ör. Retry-After, teşhis verileri) çağırana ulaşmasını sağlar.
    /// </summary>
    public void MergePropertiesFrom(AegisContext source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (ReferenceEquals(source, this) || source._properties is not { IsEmpty: false } sourceProperties)
        {
            return;
        }

        var target = Properties;
        foreach (var pair in sourceProperties)
        {
            target[pair.Key] = pair.Value;
        }
    }

    /// <summary>
    /// Nesne havuzuna iade edilmeden önce bağlamı temizler (Zero GC allocation).
    /// </summary>
    public void Reset(CancellationToken cancellationToken = default, string? pipelineName = null)
    {
        CancellationToken = cancellationToken;
        PipelineName = pipelineName;
        OperationKey = null;
        ContinueOnCapturedContext = false;
        _correlationParent = null;
        _correlationId = null;
        if (_properties is { IsEmpty: false })
        {
            // Clear() yeni kova ve kilit dizileri ayırır; anahtarları tek tek silmek mevcut tabloyu korur (havuzdan
            // dönen bağlam tahsissiz temizlenir).
            foreach (var pair in _properties)
            {
                _properties.TryRemove(pair.Key, out _);
            }
        }
    }

    /// <summary>
    /// Bağlamın nesne havuzunda beklediğini gösterir. Aynı bağlamın iki kez iade edilmesini
    /// ve farklı isteklerin aynı nesneyi paylaşmasını engeller (AEGIS-111).
    /// </summary>
    internal bool IsPooled { get; set; }
}
