using System.Diagnostics;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Pipeline;

/// <summary>Kullanıcı geri çağrısı + kullanıcı durumu (TState biçimleri için; struct, tahsis yok).</summary>
internal readonly struct UserCallback<TCallback, TState>(TCallback callback, TState state)
{
    public TCallback Callback { get; } = callback;
    public TState State { get; } = state;
}
