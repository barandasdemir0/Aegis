using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Aegis.Resilience.Core.Abstractions;

namespace Aegis.Resilience.Extensions.DependencyInjection;

/// <summary>
/// DI ile kurulan boru hattının kurulum bağlamı (Polly: <c>AddResiliencePipelineContext</c>). Yapılandırma temsilcisi her
/// kurulumda (ilk kurulum ve her yeniden yükleme) yeni bir bağlamla çağrılır; bağlamda yapılan abonelikler ve dispose
/// bildirimleri o nesle aittir ve nesil emekliye ayrılıp boşalınca bırakılır.
/// </summary>
public class AegisPipelineContext
{
    private readonly Action _reload;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<Action> _disposedCallbacks = [];

    /// <param name="serviceProvider">Uygulamanın servis sağlayıcısı.</param>
    /// <param name="pipelineName">Kurulan boru hattının adı.</param>
    /// <param name="reload">Boru hattını yeniden kurdurur (abonelikler tetikler).</param>
    protected AegisPipelineContext(IServiceProvider serviceProvider, string pipelineName, Action reload)
    {
        ServiceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        PipelineName = pipelineName ?? throw new ArgumentNullException(nameof(pipelineName));
        _reload = reload ?? throw new ArgumentNullException(nameof(reload));
    }

    /// <summary>Uygulamanın servis sağlayıcısı: boru hattı kurulurken servis çözmek için (ör. <c>ILogger</c>, depo, saat).</summary>
    public IServiceProvider ServiceProvider { get; }

    /// <summary>Kurulan boru hattının adı (Polly: <c>PipelineKey</c>).</summary>
    public string PipelineName { get; }

    /// <summary>Adlandırılmış seçeneğin güncel değeri (Polly / Microsoft: <c>GetOptions&lt;TOptions&gt;</c>).</summary>
    public TOptions GetOptions<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(string? name = null)
        where TOptions : class =>
        ServiceProvider.GetRequiredService<IOptionsMonitor<TOptions>>().Get(name ?? Options.DefaultName);

    /// <summary>
    /// <typeparamref name="TOptions"/> değişince boru hattını yeniden kurar (Polly / Microsoft: <c>EnableReloads&lt;TOptions&gt;</c>).
    /// Uçuştaki çağrılar eski nesille tamamlanır; kurulum hatasında eski nesil çalışmaya devam eder.
    /// </summary>
    public void EnableReloads<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(string? name = null)
        where TOptions : class =>
        EnableReloads(ServiceProvider.GetRequiredService<IOptionsMonitor<TOptions>>(), name);

    /// <summary>Verilen izleyicideki <typeparamref name="TOptions"/> değişince yeniden kurar (Polly 8.8: <c>EnableReloads(IOptionsMonitor)</c>).</summary>
    public void EnableReloads<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(
        IOptionsMonitor<TOptions> monitor, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var optionsName = name ?? Options.DefaultName;
        var subscription = monitor.OnChange((_, changedName) =>
        {
            if (string.Equals(changedName ?? Options.DefaultName, optionsName, StringComparison.Ordinal))
            {
                _reload();
            }
        });

        if (subscription is not null)
        {
            _subscriptions.Add(subscription);
        }
    }

    /// <summary>
    /// Belirteç iptal edilince boru hattını yeniden kurar (Polly: <c>AddReloadToken</c>); ör. özel yapılandırma kaynağının
    /// değişiklik belirteci. İptal edilemeyen ya da zaten iptal edilmiş belirteç yok sayılır (kurulum sırasında döngüye girmesin).
    /// </summary>
    public void AddReloadToken(CancellationToken cancellationToken)
    {
        if (cancellationToken.CanBeCanceled && !cancellationToken.IsCancellationRequested)
        {
            _subscriptions.Add(cancellationToken.Register(static state => ((Action)state!)(), _reload));
        }
    }

    /// <summary>
    /// Bu kurulumun boru hattı dispose edildiğinde çağrılır (Polly / Microsoft: <c>OnPipelineDisposed</c>): yeniden yüklemede
    /// eski nesil boşalınca ya da uygulama kapanırken. Boru hattıyla birlikte oluşturulan kaynakları bırakmak içindir.
    /// </summary>
    public void OnPipelineDisposed(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _disposedCallbacks.Add(callback);
    }

    /// <summary>Bağlam yeniden kurma kaynağı (abonelik) ya da dispose bildirimi topladı mı.</summary>
    internal bool HasResources => _subscriptions.Count > 0 || _disposedCallbacks.Count > 0;

    /// <summary>
    /// Bağlamın topladığı kaynakları (abonelikler + dispose bildirimleri) tek bir nesne olarak devralır; neslin sahibi onu
    /// boru hattıyla birlikte dispose eder. Bağlam bundan sonra kullanılmaz.
    /// </summary>
    protected IDisposable TakeResources() => new GenerationResources([.. _subscriptions], [.. _disposedCallbacks]);

    private sealed class GenerationResources(IDisposable[] subscriptions, Action[] disposedCallbacks) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }

            foreach (var callback in disposedCallbacks)
            {
                AegisCallbacks.InvokeSafely(callback, nameof(OnPipelineDisposed));
            }
        }
    }
}
