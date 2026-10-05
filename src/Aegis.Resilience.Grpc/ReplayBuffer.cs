using Grpc.Core;

namespace Aegis.Resilience.Grpc;

/// <summary>
/// İstemci ve çift yönlü akışta gönderilen mesajların yeniden oynatma tamponu (gRPC A6 / Grpc.Net.Client
/// <c>MaxRetryBufferPerCallSize</c>). Her deneme mesajları baştan oynatır, sonra canlı mesajları bekler. Toplam boyut sınırı aşılınca
/// ya da çağrı başka nedenle commit olunca tampon yeniden oynatmayı bırakır (artık yeniden denenemez) ve gönderilen mesajları
/// bellekten düşürür. Çağıranın yazması etkin deneme mesajı gönderene kadar bekler (geri basınç; bellek sınırsız büyümez).
/// </summary>
internal sealed class ReplayBuffer<T>(long maxBytes, Func<T, int>? sizer)
    where T : class
{
    private readonly object _lock = new();
    private readonly List<T?> _messages = [];
    private TaskCompletionSource<bool> _changed = NewSignal();
    private long _bytes;
    private int _written;
    private bool _completed;
    private Exception? _failure;

    /// <summary>Yeniden oynatma artık mümkün değil (sınır aşıldı ya da çağrı commit oldu).</summary>
    public bool IsCommitted { get; private set; }

    /// <summary>Çağıranın yazma seçenekleri (her denemenin istek akışına uygulanır).</summary>
    public WriteOptions? WriteOptions { get; set; }

    /// <summary>Çağıranın mesajı: tampona eklenir, etkin deneme gönderene kadar beklenir.</summary>
    public async Task AddAsync(T message)
    {
        int index;
        lock (_lock)
        {
            if (_failure is not null)
            {
                throw _failure;
            }

            if (_completed)
            {
                throw new InvalidOperationException("İstek akışı tamamlandı; yeni mesaj yazılamaz.");
            }

            index = _messages.Count;
            _messages.Add(message);
            if (!IsCommitted && sizer is not null)
            {
                _bytes += sizer(message);
                IsCommitted = _bytes > maxBytes; // Grpc.Net.Client gibi: sınırı aşan çağrı commit olur
            }

            Signal();
        }

        await WaitWrittenAsync(index).ConfigureAwait(false);
    }

    /// <summary>Çağıran istek akışını tamamladı.</summary>
    public void Complete()
    {
        lock (_lock)
        {
            _completed = true;
            Signal();
        }
    }

    /// <summary>Çağrı commit oldu (ör. çift yönlü akışta ilk yanıt): yeniden oynatma biter.</summary>
    public void Commit()
    {
        lock (_lock)
        {
            IsCommitted = true;
        }
    }

    /// <summary>Çağrı kalıcı olarak bitti/başarısız: bekleyen yazmalar bu hatayla döner.</summary>
    public void Fail(Exception failure)
    {
        lock (_lock)
        {
            _failure ??= failure;
            Signal();
        }
    }

    /// <summary>
    /// Bir denemenin pompası: tampondaki mesajları <paramref name="index"/>'ten itibaren sırayla verir; mesaj yoksa bekler; akış
    /// tamamlandıysa null döner.
    /// </summary>
    public async Task<(bool HasMessage, T? Message)> NextAsync(int index, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                if (index < _messages.Count)
                {
                    return (true, _messages[index]);
                }

                if (_completed)
                {
                    return (false, null);
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Mesaj etkin denemede gönderildi. Commit sonrası gönderilen mesaj bellekten düşürülür.</summary>
    public void MarkWritten(int index)
    {
        lock (_lock)
        {
            if (index + 1 > _written)
            {
                _written = index + 1;
            }

            if (IsCommitted)
            {
                _messages[index] = null; // yeniden oynatılmayacak; bellek bırakılır
            }

            Signal();
        }
    }

    private async Task WaitWrittenAsync(int index)
    {
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                if (_written > index)
                {
                    return;
                }

                if (_failure is not null)
                {
                    throw _failure;
                }

                changed = _changed.Task;
            }

            await changed.ConfigureAwait(false);
        }
    }

    private void Signal()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>Commit olmuş çağrının hatasını taşır: yeniden deneme koşuluna takılmaz, çağırana özgün istisna olarak döner.</summary>
internal sealed class CommittedCallException(Exception inner) : Exception("Çağrı commit oldu; yeniden denenemez.", inner);
