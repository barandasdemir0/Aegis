using System.Collections.Concurrent;
using System.Net;

namespace Aegis.TortureTests.Harness;

/// <summary>
/// Tek iş parçacıklı eşitleme bağlamı (WinForms/WPF/eski ASP.NET benzeri). Kütüphane içeride yakalanan bağlama dönmeye
/// çalışırsa ve çağıran bu bağlamda <c>.GetAwaiter().GetResult()</c> ile bekliyorsa kilitlenme olur.
/// </summary>
public sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _thread;

    public SingleThreadSynchronizationContext()
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = "torture-ui" };
        _thread.Start();
    }

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    /// <summary>Gövdeyi bağlamın tek iş parçacığında çalıştırır ve bitmesini bekler.</summary>
    public Task RunAsync(Action body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ =>
        {
            try
            {
                body();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }, null);
        return done.Task;
    }

    private void Pump()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            callback(state);
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>Sahte HTTP sunucusu: yanıtı betikten üretir, gelen gövdeleri ve üretilen yanıtları kaydeder.</summary>
public sealed class ScriptedServer(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> script) : HttpMessageHandler
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public ConcurrentBag<byte[]> ReceivedBodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);
        if (request.Content is not null)
        {
            ReceivedBodies.Add(await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        }

        return await script(call, request, cancellationToken).ConfigureAwait(false);
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        SendAsync(request, cancellationToken).GetAwaiter().GetResult();

    public static Task<HttpResponseMessage> Respond(HttpStatusCode status, HttpContent? content = null) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = content ?? new ByteArrayContent([]) });
}

/// <summary>Dispose edildiğini kaydeden yanıt gövdesi (sızan yanıtları yakalamak için).</summary>
public sealed class TrackedContent(string body) : StringContent(body)
{
    private int _disposed;

    public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    protected override void Dispose(bool disposing)
    {
        Interlocked.Exchange(ref _disposed, 1);
        base.Dispose(disposing);
    }
}

/// <summary>Yalnızca ileri okunabilen akış (ağdan gelen gövde gibi): ikinci okuma için geri sarılamaz.</summary>
public sealed class ForwardOnlyStream(byte[] data) : Stream
{
    private int _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = Math.Min(count, data.Length - _position);
        Array.Copy(data, _position, buffer, offset, n);
        _position += n;
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
