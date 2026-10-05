using System.Collections.Concurrent;

namespace Aegis.Resilience.Distributed.Abstractions;

/// <summary>
/// Hız sınırlama algoritmaları (saf fonksiyonlar). Redis deposundaki Lua betikleri aynı formülleri uygular; değişiklik
/// ikisinde birlikte yapılmalıdır (ortak test paketi farkı yakalar).
/// </summary>
internal static class RateLimitAlgorithms
{
    public static DistributedRateLimitDecision Acquire(LimiterState state, DistributedRateLimitRule rule, int permitCount, long nowMs) =>
        rule.Algorithm switch
        {
            DistributedRateLimitAlgorithm.FixedWindow => FixedWindow(state, rule, permitCount, nowMs),
            DistributedRateLimitAlgorithm.SlidingWindow => SlidingWindow(state, rule, permitCount, nowMs),
            _ => TokenBucket(state, rule, permitCount, nowMs)
        };

    private static DistributedRateLimitDecision TokenBucket(LimiterState state, DistributedRateLimitRule rule, int permitCount, long nowMs)
    {
        var capacity = rule.PermitLimit;
        var ratePerMs = (double)rule.TokensPerPeriod / WindowMs(rule);
        if (state.Tokens < 0)
        {
            state.Tokens = capacity;
            state.LastRefillMs = nowMs;
        }

        var elapsed = nowMs - state.LastRefillMs;
        if (elapsed > 0)
        {
            state.Tokens = Math.Min(capacity, state.Tokens + (elapsed * ratePerMs));
            state.LastRefillMs = nowMs;
        }

        if (state.Tokens >= permitCount)
        {
            state.Tokens -= permitCount;
            return new DistributedRateLimitDecision(true, (int)state.Tokens, null);
        }

        var retryAfter = permitCount > capacity ? (TimeSpan?)null : Milliseconds((permitCount - state.Tokens) / ratePerMs);
        return new DistributedRateLimitDecision(false, (int)state.Tokens, retryAfter);
    }

    private static DistributedRateLimitDecision FixedWindow(LimiterState state, DistributedRateLimitRule rule, int permitCount, long nowMs)
    {
        var windowMs = WindowMs(rule);
        var windowId = nowMs / windowMs;
        if (windowId != state.WindowId)
        {
            state.WindowId = windowId;
            state.CurrentCount = 0;
        }

        if (state.CurrentCount + permitCount <= rule.PermitLimit)
        {
            state.CurrentCount += permitCount;
            return new DistributedRateLimitDecision(true, rule.PermitLimit - state.CurrentCount, null);
        }

        var retryAfter = permitCount > rule.PermitLimit ? (TimeSpan?)null : Milliseconds(((windowId + 1) * windowMs) - nowMs);
        return new DistributedRateLimitDecision(false, Math.Max(0, rule.PermitLimit - state.CurrentCount), retryAfter);
    }

    // Kayan pencere tahmini: önceki pencerenin sayısı, pencerede kalan oranla ağırlıklandırılıp güncel sayıya eklenir.
    private static DistributedRateLimitDecision SlidingWindow(LimiterState state, DistributedRateLimitRule rule, int permitCount, long nowMs)
    {
        var windowMs = WindowMs(rule);
        var windowId = nowMs / windowMs;
        if (windowId != state.WindowId)
        {
            state.PreviousCount = windowId == state.WindowId + 1 ? state.CurrentCount : 0;
            state.CurrentCount = 0;
            state.WindowId = windowId;
        }

        var elapsedInWindow = nowMs - (windowId * windowMs);
        var previousWeight = (double)(windowMs - elapsedInWindow) / windowMs;
        var estimated = (state.PreviousCount * previousWeight) + state.CurrentCount;
        var limit = rule.PermitLimit;

        if (estimated + permitCount <= limit)
        {
            state.CurrentCount += permitCount;
            return new DistributedRateLimitDecision(true, (int)(limit - estimated - permitCount), null);
        }

        return new DistributedRateLimitDecision(false, Math.Max(0, (int)(limit - estimated)),
            permitCount > limit ? null : Milliseconds(SlidingRetryAfterMs(state, limit, permitCount, windowMs, elapsedInWindow)));
    }

    /// <summary>
    /// Tahmin <c>limit</c> altına ne zaman iner: önce bu pencerede önceki pencerenin ağırlığı azalarak; yetmezse bir
    /// sonraki pencerede güncel sayının ağırlığı azalarak.
    /// </summary>
    private static double SlidingRetryAfterMs(LimiterState state, int limit, int permitCount, long windowMs, long elapsedInWindow)
    {
        var roomWithoutPrevious = limit - state.CurrentCount - permitCount;
        if (roomWithoutPrevious >= 0 && state.PreviousCount > 0)
        {
            return (windowMs * (1 - ((double)roomWithoutPrevious / state.PreviousCount))) - elapsedInWindow;
        }

        var untilNextWindow = windowMs - elapsedInWindow;
        var roomNextWindow = limit - permitCount;
        return state.CurrentCount == 0
            ? untilNextWindow
            : untilNextWindow + Math.Max(0, windowMs * (1 - ((double)roomNextWindow / state.CurrentCount)));
    }

    private static long WindowMs(DistributedRateLimitRule rule) => Math.Max(1, (long)rule.Window.TotalMilliseconds);

    private static TimeSpan Milliseconds(double ms) => TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(ms)));
}
