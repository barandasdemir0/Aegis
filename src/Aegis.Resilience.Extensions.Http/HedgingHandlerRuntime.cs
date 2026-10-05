using System.Net.Http;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Hedging;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Extensions.DependencyInjection;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// Standart hedging işleyicisinin bir nesli: dış boru hattı (toplam timeout + hedging), uç nokta başına boru hatları ve
/// seçenekler. Yapılandırma yeniden yüklenince yeni nesil kurulur; eski nesil, üzerindeki istekler bitince dispose edilir.
/// </summary>
internal sealed class HedgingHandlerRuntime : IDisposable
{
    public HedgingHandlerRuntime(string name, AegisHttpStandardHedgingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;

        Outer = new AegisPipelineBuilder(name)
            .AddTimeout(options.TotalRequestTimeout)
            .AddHedging(o =>
            {
                o.MaxHedgedAttempts = options.MaxHedgedAttempts;
                o.HedgingDelay = options.HedgingDelay;
                o.Budget = options.Budget;
                o.ShouldHandleResult = IsTransientResponse;
            })
            .Build();

        Endpoints = new AegisPipelineRegistry<string>(StringComparer.OrdinalIgnoreCase, authority => $"{name}/{authority}")
        {
            MaxDynamicPipelines = options.MaxEndpointPipelines,
            DynamicBuilder = (b, _) =>
            {
                var breaker = StrategyOptionsCloner.CloneForKey(options.EndpointCircuitBreaker);
                breaker.ShouldHandleResult ??= IsTransientResponse;
                b.AddStrategy(new ConcurrencyLimiterStrategy(StrategyOptionsCloner.CloneForKey(options.EndpointRateLimiter)))
                    .AddCircuitBreaker(breaker)
                    .AddTimeout(options.EndpointAttemptTimeout);
            }
        };
    }

    public IAegisPipeline Outer { get; }

    public AegisPipelineRegistry<string> Endpoints { get; }

    public AegisHttpStandardHedgingOptions Options { get; }

    private static bool IsTransientResponse(object? result) =>
        result is HttpResponseMessage response && AegisHttpTransientErrors.IsTransient(response.StatusCode);

    public void Dispose()
    {
        Outer.Dispose();
        Endpoints.Dispose();
    }
}
