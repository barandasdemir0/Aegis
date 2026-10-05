using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Strategies.Retry;

public enum DelayBackoffType
{
    Constant,
    Linear,
    Exponential,
    DecorrelatedJitter
}
