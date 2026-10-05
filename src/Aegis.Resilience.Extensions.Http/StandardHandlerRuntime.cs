using System.Net.Http;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Extensions.DependencyInjection;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// Standart işleyicinin bir nesli: seçenekler + anahtar başına boru hatları. Yapılandırma yeniden yüklenince yeni nesil
/// kurulur; eski nesil, üzerindeki istekler bitince dispose edilir.
/// </summary>
internal sealed class StandardHandlerRuntime : IHttpHandlerRuntime
{
    private const string SharedKey = "";
    private readonly AegisPipelineRegistry<string> _pipelines;

    public StandardHandlerRuntime(string name, AegisHttpStandardResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Options = options;
        Rules = HttpHandlerRules.Create(
            options.HandleHttpFailureStatuses, options.AllowNonIdempotentRetry, options.RetryDisabledMethods, options.ReturnFinalResponse);

        _pipelines = new AegisPipelineRegistry<string>(StringComparer.OrdinalIgnoreCase, key => key.Length == 0 ? name : $"{name}/{key}")
        {
            MaxDynamicPipelines = options.MaxPipelines,
            DynamicBuilder = (builder, _) => Build(builder, options)
        };
    }

    public AegisHttpStandardResilienceOptions Options { get; }

    public HttpHandlerRules Rules { get; }

    /// <summary>İsteğin boru hattı (seçici yoksa tek, paylaşılan boru hattı).</summary>
    public HttpHandlerRoute Route(HttpRequestMessage request) =>
        new(_pipelines.GetPipeline(Options.PipelineSelector?.Invoke(request) ?? SharedKey), Rules);

    /// <summary>Microsoft standart zinciri: eşzamanlılık → toplam timeout → retry → devre kesici → deneme timeout.</summary>
    private static void Build(IAegisPipelineBuilder builder, AegisHttpStandardResilienceOptions options)
    {
        var perKey = options.PipelineSelector is not null;
        builder
            .AddStrategy(new ConcurrencyLimiterStrategy(perKey ? StrategyOptionsCloner.CloneForKey(options.RateLimiter) : options.RateLimiter))
            .AddTimeout(options.TotalRequestTimeout)
            .AddRetry(options.Retry)
            .AddCircuitBreaker(perKey ? StrategyOptionsCloner.CloneForKey(options.CircuitBreaker) : options.CircuitBreaker)
            .AddTimeout(options.AttemptTimeout);
    }

    public void Dispose() => _pipelines.Dispose();
}
