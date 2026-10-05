namespace Aegis.Resilience.Core.Strategies.CircuitBreaker;

public enum CircuitState
{
    Closed = 0,
    Open = 1,
    HalfOpen = 2,
    Isolated = 3
}
