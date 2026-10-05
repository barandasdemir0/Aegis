using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Pipeline;

/// <summary>
/// Yapılandırma değişince TÜM boru hattını yeniden kuran sarmalayıcı (Polly: <c>EnableReloads</c> / <c>ReloadableComponent</c>).
/// <para>
/// <see cref="Reload"/> yeni bir nesil kurar; yeni çağrılar hemen yeni nesle gider. Eski nesil, üzerinde uçuşta çağrı
/// kalmadığında dispose edilir; böylece yeniden yükleme sırasında süren istekler kırılmaz (Polly: <c>ExecutionTrackingComponent</c>).
/// Kurulum başarısız olursa eski nesil çalışmaya devam eder ve hata <see cref="LastReloadError"/> ile raporlanır.
/// </para>
/// <para>
/// Not: yeni nesil yeni strateji örnekleri demektir (devre kesici durumu, kota sayaçları sıfırlanır). Yalnızca seçenek
/// değerlerini canlı değiştirmek için strateji başına <c>OptionsProvider</c> daha uygundur (durum korunur).
/// </para>
/// </summary>
public sealed class ReloadableAegisPipeline : IAegisPipeline
{
    private readonly Reloadable<IAegisPipeline> _pipeline;

    public ReloadableAegisPipeline(string name, Func<IAegisPipeline> factory)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _pipeline = new Reloadable<IAegisPipeline>(factory);
        _pipeline.Reloaded += error => Reloaded?.Invoke(this, error);
    }

    public string Name { get; }

    /// <summary>Güncel neslin stratejileri.</summary>
    public IReadOnlyList<IAegisStrategy> Strategies => _pipeline.Current.Strategies;

    /// <summary>Güncel nesil (tanımlayıcılar ve testler için).</summary>
    public IAegisPipeline Current => _pipeline.Current;

    /// <summary>Kaç kez başarıyla yeniden yüklendi.</summary>
    public int ReloadCount => _pipeline.ReloadCount;

    /// <summary>Son başarısız yeniden yüklemenin hatası (başarılı yüklemede temizlenir).</summary>
    public Exception? LastReloadError => _pipeline.LastReloadError;

    /// <summary>Yeniden yükleme sonrası (başarılı veya başarısız) çağrılır.</summary>
    public event Action<ReloadableAegisPipeline, Exception?>? Reloaded;

    /// <summary>Boru hattını yeniden kurar. Kurulum hatası fırlatılmaz; eski nesil çalışmaya devam eder.</summary>
    public bool Reload() => _pipeline.Reload();

    /// <summary>Boru hattıyla birlikte dispose edilecek kaynak ekler (ör. yapılandırma değişikliği aboneliği).</summary>
    public void AddOwnedDisposable(IDisposable disposable) => _pipeline.AddOwnedDisposable(disposable);

    public ValueTask<TResult> ExecuteAsync<TResult>(Func<AegisContext, ValueTask<TResult>> callback, AegisContext? context = null)
    {
        var lease = _pipeline.Acquire();
        return Track(lease, Invoke(lease, static (p, s) => p.ExecuteAsync(s.callback, s.context), (callback, context)));
    }

    public ValueTask ExecuteAsync(Func<AegisContext, ValueTask> callback, AegisContext? context = null)
    {
        var lease = _pipeline.Acquire();
        ValueTask pending;
        try
        {
            pending = lease.Value.ExecuteAsync(callback, context);
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        if (pending.IsCompleted)
        {
            lease.Dispose();
            return pending;
        }

        return AwaitAndReleaseAsync(pending, lease);
    }

    public ValueTask<TResult> ExecuteAsync<TResult, TState>(Func<AegisContext, TState, ValueTask<TResult>> callback, TState state, AegisContext? context = null)
    {
        var lease = _pipeline.Acquire();
        return Track(lease, Invoke(lease, static (p, s) => p.ExecuteAsync(s.callback, s.state, s.context), (callback, state, context)));
    }

    public ValueTask<Outcome<TResult>> ExecuteOutcomeAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback, TState state, AegisContext? context = null)
    {
        var lease = _pipeline.Acquire();
        return Track(lease, Invoke(lease, static (p, s) => p.ExecuteOutcomeAsync(s.callback, s.state, s.context), (callback, state, context)));
    }

    public void Dispose() => _pipeline.Dispose();

    /// <summary>Çağrıyı güncel nesilde başlatır; eşzamanlı istisnada kiralamayı bırakır.</summary>
    private static ValueTask<T> Invoke<T, TArgs>(Reloadable<IAegisPipeline>.Lease lease, Func<IAegisPipeline, TArgs, ValueTask<T>> start, TArgs args)
    {
        try
        {
            return start(lease.Value, args);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>Tamamlanınca kiralamayı bırakır (eşzamanlı tamamlanmada tahsis yok).</summary>
    private static ValueTask<T> Track<T>(Reloadable<IAegisPipeline>.Lease lease, ValueTask<T> pending)
    {
        if (pending.IsCompleted)
        {
            lease.Dispose();
            return pending;
        }

        return AwaitAndReleaseAsync(pending, lease);
    }

    private static async ValueTask<T> AwaitAndReleaseAsync<T>(ValueTask<T> pending, Reloadable<IAegisPipeline>.Lease lease)
    {
        using (lease)
        {
            return await pending.ConfigureAwait(false);
        }
    }

    private static async ValueTask AwaitAndReleaseAsync(ValueTask pending, Reloadable<IAegisPipeline>.Lease lease)
    {
        using (lease)
        {
            await pending.ConfigureAwait(false);
        }
    }
}
