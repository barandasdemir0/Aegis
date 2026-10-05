using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;

namespace Aegis.Resilience.Testing;

/// <summary>Boru hattının tanımı (Polly: <c>ResiliencePipelineDescriptor</c>).</summary>
public sealed class AegisPipelineDescriptor
{
    internal AegisPipelineDescriptor(string name, IReadOnlyList<AegisStrategyDescriptor> strategies, bool isReloadable)
    {
        Name = name;
        Strategies = strategies;
        IsReloadable = isReloadable;
    }

    public string Name { get; }

    /// <summary>
    /// Stratejiler, dıştan içe çalışma sırasıyla. <c>AddPipeline</c> ile iç içe kurulan boru hatları düzleştirilir
    /// (Polly ile aynı).
    /// </summary>
    public IReadOnlyList<AegisStrategyDescriptor> Strategies { get; }

    /// <summary>İlk (en dış) strateji; boru hattı boşsa <see cref="InvalidOperationException"/>.</summary>
    public AegisStrategyDescriptor FirstStrategy => Strategies.Count > 0
        ? Strategies[0]
        : throw new InvalidOperationException($"'{Name}' boru hattında strateji yok.");

    /// <summary>Boru hattı yapılandırma değişince yeniden yüklenebiliyor mu.</summary>
    public bool IsReloadable { get; }

    /// <summary>Belirtilen seçenek tipindeki ilk stratejinin seçenekleri; yoksa <see cref="InvalidOperationException"/>.</summary>
    public TOptions GetOptions<TOptions>()
        where TOptions : class =>
        Strategies.Select(s => s.Options).OfType<TOptions>().FirstOrDefault()
        ?? throw new InvalidOperationException($"'{Name}' boru hattında {typeof(TOptions).Name} seçenekli strateji yok.");
}
