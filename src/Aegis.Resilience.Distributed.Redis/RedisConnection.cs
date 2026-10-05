using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using StackExchange.Redis;

namespace Aegis.Resilience.Distributed.Redis;

/// <summary>
/// Redis depolarının ortak bağlantı yönetimi: hazır ya da arka planda kurulan bağlantı (AEGIS-157), işlem süre sınırı
/// (AEGIS-161) ve erişilebilirlik takibi (AEGIS-160). Devre kesici ve hız sınırlayıcı depoları bunu paylaşır.
/// </summary>
internal sealed class RedisConnection
{
    private static readonly TimeSpan RecentFailureWindow = TimeSpan.FromSeconds(5);

    private readonly IConnectionMultiplexer? _redis;
    private readonly Func<Task<IConnectionMultiplexer>>? _connect;
    private readonly object _connectLock = new();
    private Task<IConnectionMultiplexer>? _connecting;
    private long _lastTransientFailure;

    public RedisConnection(IConnectionMultiplexer redis, TimeSpan? operationTimeout)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        OperationTimeout = ValidateTimeout(operationTimeout);
    }

    public RedisConnection(Func<Task<IConnectionMultiplexer>> connect, TimeSpan? operationTimeout)
    {
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        OperationTimeout = ValidateTimeout(operationTimeout);
        _connecting = StartConnecting();
    }

    /// <summary>Tek bir Redis işleminin üst süresi.</summary>
    public TimeSpan OperationTimeout { get; }

    /// <summary>Bağlantı kurulu ve son <c>5 sn</c> içinde işlem süre sınırı aşılmadıysa <c>true</c>.</summary>
    public bool IsAvailable =>
        (_redis?.IsConnected ?? (Volatile.Read(ref _connecting) is { IsCompletedSuccessfully: true } connected && connected.Result.IsConnected)) &&
        !HadRecentTransientFailure();

    /// <summary>Veritabanı; bağlantı henüz yoksa geçici hata fırlatır (çağıran yerel duruma düşer).</summary>
    public IDatabase GetDatabase()
    {
        if (_redis != null)
        {
            return _redis.GetDatabase();
        }

        var connecting = Volatile.Read(ref _connecting)!;
        if (connecting.IsCompletedSuccessfully)
        {
            return connecting.Result.GetDatabase();
        }

        if (connecting.IsCompleted)
        {
            // Bağlantı denemesi hata verdi: bir sonraki denemeyi (tek seferde bir tane) başlat.
            lock (_connectLock)
            {
                if (ReferenceEquals(_connecting, connecting))
                {
                    Volatile.Write(ref _connecting, StartConnecting());
                }
            }
        }

        throw new RedisNotConnectedException();
    }

    /// <summary>Başarılı bir işlem, Redis'in yeniden yanıt verdiğini gösterir: health check hemen Healthy'ye dönebilsin.</summary>
    public void MarkSuccess() => Interlocked.Exchange(ref _lastTransientFailure, 0);

    public void MarkTransientFailure() => Interlocked.Exchange(ref _lastTransientFailure, Stopwatch.GetTimestamp());

    /// <summary>Yerel duruma düşülerek yutulacak geçici Redis hataları.</summary>
    public static bool IsTransient(Exception ex) => ex is RedisException or RedisNotConnectedException or TimeoutException or IOException or SocketException;

    private bool HadRecentTransientFailure()
    {
        var last = Interlocked.Read(ref _lastTransientFailure);
        return last != 0 && Stopwatch.GetElapsedTime(last) < RecentFailureWindow;
    }

    private Task<IConnectionMultiplexer> StartConnecting()
    {
        var task = Task.Run(_connect!);
        // Gözlenmemiş görev istisnası (UnobservedTaskException) oluşmasın
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return task;
    }

    private static TimeSpan ValidateTimeout(TimeSpan? timeout)
    {
        var value = timeout ?? RedisCircuitBreakerStateStore.DefaultOperationTimeout;
        if (value != Timeout.InfiniteTimeSpan && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), value,
                "Redis işlem süre sınırı pozitif olmalı veya Timeout.InfiniteTimeSpan olmalıdır.");
        }

        return value;
    }

    /// <summary>Arka plan bağlantısı henüz kurulmadı; depo yerel duruma düşer.</summary>
    private sealed class RedisNotConnectedException() : Exception("Redis bağlantısı henüz kurulmadı; yerel duruma düşülüyor.");
}
