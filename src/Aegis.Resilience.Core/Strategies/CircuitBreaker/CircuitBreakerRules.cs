using Aegis.Resilience.Core.Exceptions;

namespace Aegis.Resilience.Core.Strategies.CircuitBreaker;

/// <summary>Dayanıklılık stratejilerinin ortak istisna sınıflandırma kuralları.</summary>
public static class CircuitBreakerRules
{
    /// <summary>
    /// Retry'nin asla yeniden denemediği sonuçlar: iptal (<see cref="OperationCanceledException"/>, Polly ile aynı) ve retler
    /// (<see cref="BrokenCircuitException"/>, <see cref="RateLimitRejectedException"/>). Ret, hedefe hiç gidilmediği anlamına gelir;
    /// hemen yeniden denemek yalnızca dönen bir döngü üretir (Microsoft standart işleyicisi de retleri yeniden denemez).
    /// </summary>
    public static bool IsControlFlow(Exception exception) =>
        exception is OperationCanceledException || IsRejection(exception);

    /// <summary>
    /// Devre kesicinin hata saymadığı sonuçlar: retler ve ÇAĞIRANIN kendi iptali. Çağıran iptal etmediği halde gelen iptal
    /// (ör. <c>HttpClient.Timeout</c> → <see cref="TaskCanceledException"/>) bağımlılığın yanıt vermediğini gösterir ve hata
    /// sayılır; aksi halde takılan bir bağımlılığa karşı devre hiç açılmazdı (Polly'de bilinen tuzak).
    /// </summary>
    public static bool IsCircuitNeutral(Exception exception, CancellationToken callerToken) =>
        IsRejection(exception) || (exception is OperationCanceledException && callerToken.IsCancellationRequested);

    /// <summary>Dayanıklılık katmanının isteği hedefe göndermeden reddetmesi (açık devre, hız sınırı).</summary>
    public static bool IsRejection(Exception exception) =>
        exception is BrokenCircuitException or RateLimitRejectedException;
}
