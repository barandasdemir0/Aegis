namespace Aegis.Resilience.Core.Pipeline;

/// <summary>
/// Canlı yenilenebilen kaynak: <see cref="Reload"/> yeni bir nesil kurar, yeni kullanımlar hemen yeni nesle gider; eski nesil
/// üzerinde süren kullanım kalmadığında dispose edilir. Kurulum hatasında eski nesil çalışmaya devam eder.
/// <para>
/// Kullanım: <c>using var lease = reloadable.Acquire(); lease.Value.…</c>. Kiralama, neslin kullanım sırasında dispose
/// edilmesini engeller. <see cref="ReloadableAegisPipeline"/> ve HTTP standart işleyicileri bu türü kullanır.
/// </para>
/// </summary>
public sealed class Reloadable<T> : IDisposable
    where T : class, IDisposable
{
    private readonly Func<T> _factory;
    private readonly object _lock = new();
    private readonly List<IDisposable> _ownedDisposables = [];
    private Generation _current;
    private int _disposed;

    public Reloadable(Func<T> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _current = new Generation(factory());
    }

    /// <summary>Güncel nesil (anlık görüntü; uzun süre kullanılacaksa <see cref="Acquire"/> tercih edin).</summary>
    public T Current => Volatile.Read(ref _current).Value;

    /// <summary>Başarılı yeniden yükleme sayısı.</summary>
    public int ReloadCount { get; private set; }

    /// <summary>Son başarısız yeniden yüklemenin hatası (başarılı yüklemede temizlenir).</summary>
    public Exception? LastReloadError { get; private set; }

    /// <summary>Her yeniden yükleme denemesinden sonra (hata varsa istisnayla) çağrılır.</summary>
    public event Action<Exception?>? Reloaded;

    /// <summary>Güncel nesli kiralar; kiralama dispose edilene kadar nesil dispose edilmez.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0041:Public members should not use oblivious types",
        Justification = "Yanlış pozitif: genel türün iç içe struct'ı (Reloadable<T>.Lease) null-bilinçsiz görünür; T class kısıtlıdır.")]
    public Lease Acquire()
    {
        while (true)
        {
            var generation = Volatile.Read(ref _current);
            if (generation.TryEnter())
            {
                return new Lease(generation);
            }

            // Emekliye ayrılmış nesle girilmedi: güncel nesil değişmiş olmalı, tekrar dene.
        }
    }

    /// <summary>Yeni nesli kurar. Kurulum hatası fırlatılmaz; eski nesil çalışmaya devam eder.</summary>
    public bool Reload()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        T next;
        try
        {
            next = _factory();
        }
        catch (Exception ex)
        {
            LastReloadError = ex;
            Reloaded?.Invoke(ex);
            return false;
        }

        Generation previous;
        lock (_lock)
        {
            previous = _current;
            Volatile.Write(ref _current, new Generation(next));
            ReloadCount++;
            LastReloadError = null;
        }

        previous.Retire();
        Reloaded?.Invoke(null);
        return true;
    }

    /// <summary>Kaynakla birlikte dispose edilecek nesne ekler (ör. yapılandırma değişikliği aboneliği).</summary>
    public void AddOwnedDisposable(IDisposable disposable)
    {
        ArgumentNullException.ThrowIfNull(disposable);
        lock (_lock)
        {
            _ownedDisposables.Add(disposable);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        IDisposable[] owned;
        lock (_lock)
        {
            owned = [.. _ownedDisposables];
            _ownedDisposables.Clear();
        }

        foreach (var disposable in owned)
        {
            disposable.Dispose();
        }

        Volatile.Read(ref _current).Retire();
    }

    /// <summary>Bir neslin kiralanması; dispose edilince kullanım sayacı düşer.</summary>
    public readonly struct Lease : IDisposable
    {
        private readonly Generation _generation;

        internal Lease(Generation generation) => _generation = generation;

        /// <summary>Kiralanan nesil.</summary>
        public T Value => _generation.Value;

        public void Dispose() => _generation?.Exit();
    }

    /// <summary>Bir nesil: kullanımları sayar, emekliye ayrılıp boşalınca bir kez dispose eder.</summary>
    internal sealed class Generation(T value)
    {
        // Alt 31 bit: süren kullanım sayısı; en üst bit: emekli bayrağı.
        private const int RetiredFlag = int.MinValue;
        private int _state;
        private int _disposed;

        public T Value { get; } = value;

        public bool TryEnter()
        {
            while (true)
            {
                var state = Volatile.Read(ref _state);
                if ((state & RetiredFlag) != 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _state, state + 1, state) == state)
                {
                    return true;
                }
            }
        }

        public void Exit()
        {
            if (Interlocked.Decrement(ref _state) == RetiredFlag)
            {
                DisposeOnce(); // emekli ve son kullanım bitti
            }
        }

        public void Retire()
        {
            int state;
            while (true)
            {
                state = Volatile.Read(ref _state);
                if ((state & RetiredFlag) != 0)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _state, state | RetiredFlag, state) == state)
                {
                    break;
                }
            }

            if (state == 0)
            {
                DisposeOnce(); // süren kullanım yok
            }
        }

        private void DisposeOnce()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Value.Dispose();
            }
        }
    }
}
