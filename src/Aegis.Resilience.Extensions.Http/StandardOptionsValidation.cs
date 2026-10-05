using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Core.Strategies.Timeout;

namespace Aegis.Resilience.Extensions.Http;

internal static class StandardOptionsValidation
{
    public static void EnsureConsistency(TimeSpan totalTimeout, TimeSpan attemptTimeout, TimeSpan samplingDuration, string optionsName)
    {
        var infinite = System.Threading.Timeout.InfiniteTimeSpan;
        if (totalTimeout != infinite && attemptTimeout != infinite && attemptTimeout >= totalTimeout)
        {
            throw new ArgumentException(
                $"{optionsName}: deneme zaman aşımı ({attemptTimeout}) toplam zaman aşımından ({totalTimeout}) küçük olmalı; aksi halde hiçbir yeniden deneme yapılamaz.");
        }

        if (attemptTimeout != infinite && samplingDuration < TimeSpan.FromTicks(attemptTimeout.Ticks * 2))
        {
            throw new ArgumentException(
                $"{optionsName}: devre kesici örnekleme penceresi ({samplingDuration}) deneme zaman aşımının ({attemptTimeout}) en az iki katı olmalı; " +
                "aksi halde zaman aşımına uğrayan denemeler pencereye düşmeden sayılmaz.");
        }
    }
}
