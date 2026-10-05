using Aegis.Resilience.Core.Abstractions;

namespace Aegis.Resilience.Core.Pipeline;

/// <summary>
/// Boru hattı inşa edici (Builder) somut sınıfı.
/// <para>
/// Bir builder TEK bir boru hattı üretir (AEGIS-140). <see cref="Build"/> sonrası <see cref="AddStrategy"/> veya
/// ikinci bir <see cref="Build"/> çağrısı <see cref="InvalidOperationException"/> fırlatır. Aksi halde aynı
/// strateji ÖRNEKLERİ (devre kesici durumu, semaforlar, önbellek sözlükleri) iki boru hattı arasında sessizce
/// paylaşılır ve birini dispose etmek diğerini de kullanılmaz hâle getirir (Polly: AddPipeline_AfterUsed_Throws).
/// </para>
/// </summary>
public sealed class AegisPipelineBuilder : IAegisPipelineBuilder
{
    private readonly List<IAegisStrategy> _strategies = new();
    private bool _built;

    public string Name { get; }
    public IReadOnlyList<IAegisStrategy> Strategies => _strategies;

    public AegisPipelineBuilder(string name = "Default")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public IAegisPipelineBuilder AddStrategy(IAegisStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ThrowIfBuilt();
        _strategies.Add(strategy);
        return this;
    }

    /// <summary>
    /// Boru hattındaki tüm stratejilerin kullanacağı saat (Polly: <c>ResiliencePipelineBuilderBase.TimeProvider</c>).
    /// Null ise her strateji <see cref="System.TimeProvider.System"/> kullanır. <see cref="Build"/> anında
    /// <see cref="AegisStrategy"/> tabanlı her stratejiye atanır.
    /// </summary>
    public TimeProvider? TimeProvider { get; set; }

    /// <summary>
    /// Telemetri seçenekleri (Polly: <c>ConfigureTelemetry(TelemetryOptions)</c>): olay dinleyicileri (ör. <c>ILogger</c>),
    /// metrik zenginleştiricileri ve önem sağlayıcı. Null olsa da standart metrikler (<c>aegis.strategy.events</c>,
    /// <c>aegis.strategy.attempt.duration</c>, <c>aegis.pipeline.duration</c>) boru hattı adıyla yayınlanır.
    /// </summary>
    public Telemetry.AegisTelemetryOptions? TelemetryOptions { get; set; }

    /// <summary>
    /// Boru hattı örneğinin adı (Polly: <c>ResiliencePipelineBuilderBase.InstanceName</c>). Aynı adlı boru hattının
    /// örneklerini (ör. kiracı veya HTTP istemcisi başına) telemetride ayırt eder: olaylarda
    /// <see cref="Telemetry.AegisTelemetryEvent.PipelineInstance"/>, metriklerde <c>pipeline.instance</c> etiketi.
    /// </summary>
    public string? InstanceName { get; set; }

    public IAegisPipeline Build()
    {
        ThrowIfBuilt();
        _built = true;

        foreach (var strategy in _strategies)
        {
            if (strategy is AegisStrategy aegisStrategy)
            {
                if (TimeProvider is { } timeProvider)
                {
                    aegisStrategy.UseTimeProvider(timeProvider);
                }

                aegisStrategy.UseTelemetry(Name, InstanceName, TelemetryOptions);
            }
        }

        return new AegisPipeline(Name, _strategies.ToArray(), TelemetryOptions, InstanceName);
    }

    private void ThrowIfBuilt()
    {
        if (_built)
        {
            throw new InvalidOperationException(
                $"'{Name}' builder'ı zaten bir boru hattı üretti. Bir builder yalnızca bir kez Build() edilebilir; " +
                "strateji örneklerinin (devre kesici durumu, semaforlar) boru hatları arasında paylaşılmasını önlemek için yeni bir builder oluşturun.");
        }
    }
}
