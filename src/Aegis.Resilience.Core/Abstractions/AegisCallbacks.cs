using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Abstractions;

/// <summary>
/// Sonucu zaten belirlenmiş bir işlemden SONRA çağrılan kullanıcı bildirimlerini (OnOpened, OnClosed, OnHalfOpened vb.) güvenle çalıştırır.
/// Bildirimin hatası çağıranın gerçek sonucunu/istisnasını asla ezmez; <c>aegis.callback.errors.total</c> sayacına yazılır.
/// </summary>
public static class AegisCallbacks
{
    /// <summary>Bildirimi çalıştırır; fırlatılan istisnayı yutar ve telemetriye kaydeder. İptal istisnaları da yutulur.</summary>
    public static async ValueTask InvokeSafelyAsync<TArgs>(Func<TArgs, ValueTask>? callback, TArgs args, string callbackName)
    {
        if (callback == null)
        {
            return;
        }

        try
        {
            await callback(args).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Gözlemci hatası kasıtlı olarak yutulur: işlem sonucu zaten belirlendi.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AegisTelemetry.CountCallbackError(pipeline: null, callbackName, ex);
        }
    }

    /// <summary>Senkron bildirimi çalıştırır; fırlatılan istisnayı yutar ve telemetriye kaydeder.</summary>
    public static void InvokeSafely(Action? callback, string callbackName)
    {
        if (callback == null)
        {
            return;
        }

        try
        {
            callback();
        }
#pragma warning disable CA1031 // Gözlemci hatası kasıtlı olarak yutulur: işlem sonucu zaten belirlendi.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AegisTelemetry.CountCallbackError(pipeline: null, callbackName, ex);
        }
    }
}
