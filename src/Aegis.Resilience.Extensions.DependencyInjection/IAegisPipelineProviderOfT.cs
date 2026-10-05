using System.Collections.Concurrent;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;

namespace Aegis.Resilience.Extensions.DependencyInjection;

/// <summary>Anahtara göre boru hattı sağlayıcı (Polly: <c>ResiliencePipelineProvider&lt;TKey&gt;</c>).</summary>
public interface IAegisPipelineProvider<TKey>
    where TKey : notnull
{
    /// <summary>Anahtarın boru hattını döner; tanımlı değilse <see cref="KeyNotFoundException"/>.</summary>
    IAegisPipeline GetPipeline(TKey key);

    /// <summary>Anahtarın boru hattını bulmaya çalışır (tanım veya dinamik üretici varsa ilk erişimde kurulur).</summary>
    bool TryGetPipeline(TKey key, out IAegisPipeline? pipeline);
}
