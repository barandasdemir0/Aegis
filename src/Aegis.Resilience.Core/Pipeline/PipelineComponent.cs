using System.Diagnostics;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Pipeline;

/// <summary>
/// Pre-compiled Pipeline bileşenleri için temel soyut sınıf. Katmanlar arasında başarısızlık
/// <see cref="Outcome{TResult}"/> ile taşınır; istisna yalnızca boru hattının en dışında fırlatılır.
/// Zincir, kullanıcının geri çağrısını ve durumunu (<c>TState</c>) closure'suz taşır.
/// </summary>
internal abstract class PipelineComponent
{
    public abstract ValueTask<Outcome<TResult>> ExecuteAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state);
}
