using System.Diagnostics;
using System.Diagnostics.Metrics;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Telemetry;

/// <summary>Standart olay adları (Polly ile aynı adlar; panolar ve alarmlar iki kütüphane arasında taşınabilir).</summary>
public static class AegisEventNames
{
    public const string OnRetry = "OnRetry";
    
    /// <summary>Yeniden deneme bütçesi tükendiği için deneme yapılmadı (<see cref="Strategies.Retry.RetryBudget"/>).</summary>
    public const string OnRetryBudgetExhausted = "OnRetryBudgetExhausted";
    public const string ExecutionAttempt = "ExecutionAttempt";
    public const string OnTimeout = "OnTimeout";
    public const string OnCircuitOpened = "OnCircuitOpened";
    public const string OnCircuitClosed = "OnCircuitClosed";
    public const string OnCircuitHalfOpened = "OnCircuitHalfOpened";
    public const string OnRateLimiterRejected = "OnRateLimiterRejected";
    public const string OnHedging = "OnHedging";
    public const string OnFallback = "OnFallback";
    public const string ChaosOnFault = "Chaos.OnFault";
    public const string ChaosOnLatency = "Chaos.OnLatency";
    public const string ChaosOnOutcome = "Chaos.OnOutcome";
    public const string ChaosOnBehavior = "Chaos.OnBehavior";
    public const string PipelineExecuting = "PipelineExecuting";
    public const string PipelineExecuted = "PipelineExecuted";

    // Yalnızca Aegis
    public const string OnCacheHit = "OnCacheHit";
    public const string OnCacheMiss = "OnCacheMiss";
    public const string OnStaleFallback = "OnStaleFallback";
    public const string OnRequestCollapsed = "OnRequestCollapsed";
}
