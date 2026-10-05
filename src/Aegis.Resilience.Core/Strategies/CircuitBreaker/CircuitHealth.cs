namespace Aegis.Resilience.Core.Strategies.CircuitBreaker;

/// <summary>Örnekleme penceresinin sağlık özeti.</summary>
/// <param name="SuccessCount">Penceredeki başarılı çağrı sayısı.</param>
/// <param name="FailureCount">Penceredeki başarısız çağrı sayısı.</param>
/// <param name="SlowCallCount">Penceredeki yavaş çağrı sayısı.</param>
public readonly record struct CircuitHealth(int SuccessCount, int FailureCount, int SlowCallCount)
{
    /// <summary>Toplam çağrı sayısı.</summary>
    public int Throughput => SuccessCount + FailureCount;

    /// <summary>Hata oranı [0, 1]; çağrı yoksa 0.</summary>
    public double FailureRate => Throughput == 0 ? 0 : (double)FailureCount / Throughput;
}
