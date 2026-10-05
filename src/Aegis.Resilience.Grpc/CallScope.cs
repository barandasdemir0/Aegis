using Grpc.Core;

namespace Aegis.Resilience.Grpc;

/// <summary>
/// Bir gRPC çağrısının tüm denemelerini kapsayan iptal: çağıranın token'ı + deadline (gRPC A6: deadline tüm denemeleri kapsar;
/// süre dolunca kalan yeniden denemeler beklenmeden atlanır). Deadline yoksa tahsis yapılmaz.
/// </summary>
internal sealed class CallScope : IDisposable
{
    private readonly CancellationTokenSource? _cts;
    private readonly DateTime? _deadline;
    private volatile bool _released;

    private CallScope(CancellationToken token, CancellationTokenSource? cts, DateTime? deadline)
    {
        Token = token;
        _cts = cts;
        _deadline = deadline;
    }

    public CancellationToken Token { get; }

    /// <summary>Deadline geçti mi (çağıran iptal etmeden gelen iptalin nedeni).</summary>
    // Zamanlayıcı saatten birkaç ms önce tetiklenebilir: kaynak iptal edildiyse süre dolmuş sayılır.
    public bool DeadlineExpired =>
        _deadline is { } deadline && ((!_released && _cts!.IsCancellationRequested) || DateTime.UtcNow >= deadline);

    /// <summary>Çağrının kapsamını oluşturur.</summary>
    /// <param name="options">Çağrı seçenekleri.</param>
    /// <param name="alwaysOwnSource">Deadline olmasa da kendi iptal kaynağını oluştur (akış erken bırakılınca iptal için).</param>
    public static CallScope Create(CallOptions options, bool alwaysOwnSource = false)
    {
        if (options.Deadline is not { } deadline || deadline == DateTime.MaxValue)
        {
            return alwaysOwnSource
                ? Owned(CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken), deadline: null)
                : new CallScope(options.CancellationToken, cts: null, deadline: null);
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken);
        var remaining = deadline.ToUniversalTime() - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            cts.Cancel();
        }
        else
        {
            cts.CancelAfter(remaining.TotalMilliseconds >= int.MaxValue ? TimeSpan.FromMilliseconds(int.MaxValue - 1) : remaining);
        }

        return Owned(cts, deadline.ToUniversalTime());
    }

    private static CallScope Owned(CancellationTokenSource cts, DateTime? deadline) => new(cts.Token, cts, deadline);

    /// <summary>Kapsamı iptal eder (akış çağıran tarafından erken bırakıldı).</summary>
    public void Cancel()
    {
        _released = true; // bu iptal deadline değildir
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // zaten kapatıldı
        }
    }

    /// <summary>
    /// İptali gRPC durumuna çevirir: çağıran iptal ettiyse <see cref="StatusCode.Cancelled"/>, deadline dolduysa
    /// <see cref="StatusCode.DeadlineExceeded"/> (Grpc.Net.Client ile aynı); aksi halde null.
    /// </summary>
    public RpcException? ToRpcException(OperationCanceledException exception, CancellationToken callerToken)
    {
        if (callerToken.IsCancellationRequested)
        {
            return new RpcException(new Status(StatusCode.Cancelled, "Çağrı iptal edildi.", exception));
        }

        return DeadlineExpired
            ? new RpcException(new Status(StatusCode.DeadlineExceeded, "Deadline aşıldı (tüm denemeler dahil).", exception))
            : null;
    }

    public void Dispose() => _cts?.Dispose();
}
