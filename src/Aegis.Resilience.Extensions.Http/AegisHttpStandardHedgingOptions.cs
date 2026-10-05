using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Core.Strategies.Timeout;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// Standart hedging seçenekleri (Microsoft: <c>HttpStandardHedgingResilienceOptions</c>). Zincir: toplam zaman aşımı →
/// hedging → (uç nokta başına) eşzamanlılık sınırı → devre kesici → deneme zaman aşımı. Her uç nokta (authority)
/// kendi devre kesicisine sahiptir: çöken bölge diğerlerini etkilemez, hedging sağlıklı gruba geçer.
/// </summary>
public sealed class AegisHttpStandardHedgingOptions
{
    /// <summary>Tüm denemeler dahil toplam süre (varsayılan 30 sn).</summary>
    public TimeoutOptions TotalRequestTimeout { get; set; } = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Birincile ek yedek deneme sayısı (Microsoft <c>Hedging.MaxHedgedAttempts</c> ile aynı anlam; varsayılan 1).</summary>
    public int MaxHedgedAttempts { get; set; } = 1;

    /// <summary>Yeniden deneme bütçesi (isteğe bağlı; gRPC A6): ek hedging denemeleri yalnızca bütçe izin verirse başlar.</summary>

    public Aegis.Resilience.Core.Strategies.Retry.RetryBudget? Budget { get; set; }


    /// <summary>Yedek deneme gecikmesi (varsayılan 2 sn; <c>Timeout.InfiniteTimeSpan</c>: yalnızca önceki başarısız olunca).</summary>
    public TimeSpan HedgingDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Uç nokta başına eşzamanlılık sınırı (varsayılan 1000).</summary>
    public ConcurrencyLimiterOptions EndpointRateLimiter { get; set; } = new() { MaxConcurrentExecutions = 1000, QueueTimeout = TimeSpan.Zero };

    /// <summary>Uç nokta başına devre kesici.</summary>
    public CircuitBreakerOptions EndpointCircuitBreaker { get; set; } = new()
    {
        FailureRatio = 0.1,
        SamplingDuration = TimeSpan.FromSeconds(30),
        MinimumThroughput = 100,
        BreakDuration = TimeSpan.FromSeconds(5)
    };

    /// <summary>Uç nokta başına deneme zaman aşımı (varsayılan 10 sn).</summary>
    public TimeoutOptions EndpointAttemptTimeout { get; set; } = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Sıralı gruplar: deneme i, grup i'ye gider (ör. önce birincil bölge, sonra yedek bölge).</summary>
    public IList<UriEndpointGroup> OrderedGroups { get; set; } = new List<UriEndpointGroup>();

    /// <summary>Ağırlıklı gruplar (ör. trafik bölme / kanarya ile hedging birlikte).</summary>
    public IList<WeightedUriEndpointGroup> WeightedGroups { get; set; } = new List<WeightedUriEndpointGroup>();

    /// <summary>Ağırlıklı grupların seçim zamanı.</summary>
    public WeightedGroupSelectionMode SelectionMode { get; set; } = WeightedGroupSelectionMode.InitialAttempt;

    /// <summary>Idempotent olmayan istekler için de hedging yap (varsayılan: hayır; tek deneme yapılır).</summary>
    public bool AllowNonIdempotentHedging { get; set; }

    /// <summary>En fazla kaç farklı uç nokta için ayrı boru hattı tutulur (kardinalite koruması; varsayılan 1000).</summary>
    public int MaxEndpointPipelines { get; set; } = 1000;

    /// <summary>Seçenekleri ve tutarlılığı doğrular.</summary>
    public void Validate()
    {
        TotalRequestTimeout.Validate();
        EndpointRateLimiter.Validate();
        EndpointCircuitBreaker.Validate();
        EndpointAttemptTimeout.Validate();
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.AtLeast(MaxHedgedAttempts, 1, nameof(AegisHttpStandardHedgingOptions), nameof(MaxHedgedAttempts));

        if (OrderedGroups.Count > 0 && WeightedGroups.Count > 0)
        {
            throw new ArgumentException($"{nameof(AegisHttpStandardHedgingOptions)}: OrderedGroups ve WeightedGroups birlikte kullanılamaz.");
        }

        foreach (var group in OrderedGroups.Concat<UriEndpointGroup>(WeightedGroups))
        {
            if (group.Endpoints.Count == 0)
            {
                throw new ArgumentException($"{nameof(AegisHttpStandardHedgingOptions)}: her yönlendirme grubunda en az bir uç nokta olmalı.");
            }

            foreach (var endpoint in group.Endpoints)
            {
                if (endpoint.Uri is null || !endpoint.Uri.IsAbsoluteUri)
                {
                    throw new ArgumentException($"{nameof(AegisHttpStandardHedgingOptions)}: uç nokta adresi mutlak bir URI olmalı.");
                }

                Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.AtLeast(endpoint.Weight, 1, nameof(WeightedUriEndpoint), nameof(WeightedUriEndpoint.Weight));
            }
        }

        foreach (var group in WeightedGroups)
        {
            Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.AtLeast(group.Weight, 1, nameof(WeightedUriEndpointGroup), nameof(WeightedUriEndpointGroup.Weight));
        }

        StandardOptionsValidation.EnsureConsistency(TotalRequestTimeout.Timeout, EndpointAttemptTimeout.Timeout, EndpointCircuitBreaker.SamplingDuration,
            nameof(AegisHttpStandardHedgingOptions));

        if (EndpointCircuitBreaker.StateProvider is not null)
        {
            throw new ArgumentException(
                $"{nameof(AegisHttpStandardHedgingOptions)}: uç nokta başına ayrı devre kurulduğu için StateProvider (tek devre) kullanılamaz. " +
                "ManualControl tüm uç noktalarla çalışır.");
        }
    }
}
