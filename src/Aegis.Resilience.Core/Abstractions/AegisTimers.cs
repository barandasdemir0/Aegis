namespace Aegis.Resilience.Core.Abstractions;

/// <summary>
/// Zamanlayıcı sürelerinin tek kuralı. .NET zamanlayıcıları (Task.Delay, CancelAfter, SemaphoreSlim.WaitAsync) üst sınırı
/// aşan süreyi çalışma anında ArgumentOutOfRangeException ile reddeder; kullanıcı ise "sınırsız" anlamında sık sık
/// <see cref="TimeSpan.MaxValue"/> verir. Sınırı aşan süre sonsuz sayılır (yalnızca iptal sonlandırır).
/// </summary>
internal static class AegisTimers
{
    /// <summary>Tüm hedeflerde geçerli en uzun süre: int.MaxValue ms (≈ 24,8 gün; .NET Framework zamanlayıcı sınırı).</summary>
    internal static readonly TimeSpan MaxDuration = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>Sınırı aşan süreyi <see cref="Timeout.InfiniteTimeSpan"/> yapar; diğerlerini aynen döner.</summary>
    internal static TimeSpan Normalize(TimeSpan duration) => duration > MaxDuration ? Timeout.InfiniteTimeSpan : duration;
}
