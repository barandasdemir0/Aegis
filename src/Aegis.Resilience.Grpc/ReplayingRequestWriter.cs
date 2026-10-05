using Grpc.Core;

namespace Aegis.Resilience.Grpc;

/// <summary>Çağırana verilen istek akışı yazıcısı: mesajlar yeniden oynatma tamponuna gider (bkz. <see cref="ReplayBuffer{T}"/>).</summary>
internal sealed class ReplayingRequestWriter<T>(ReplayBuffer<T> buffer) : IClientStreamWriter<T>
    where T : class
{
    public WriteOptions? WriteOptions
    {
        get => buffer.WriteOptions;
        set => buffer.WriteOptions = value;
    }

    public Task WriteAsync(T message) => buffer.AddAsync(message);

#if NET
    // Grpc.Core'un varsayılan uygulaması NotSupportedException fırlatır: token'lı yazma (önerilen kullanım) desteklenmelidir.
    // İptal edilmiş token'la mesaj tampona eklenmez; gönderilmeyi beklerken gelen iptal yalnızca beklemeyi bırakır.
    public Task WriteAsync(T message, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var write = buffer.AddAsync(message);
        return cancellationToken.CanBeCanceled ? write.WaitAsync(cancellationToken) : write;
    }
#endif

    public Task CompleteAsync()
    {
        buffer.Complete();
        return Task.CompletedTask;
    }
}
