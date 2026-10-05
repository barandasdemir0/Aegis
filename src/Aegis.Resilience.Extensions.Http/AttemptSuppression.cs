using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// Bağlama "ek deneme yapma" işaretini koyar ve istek bitince önceki değerini geri yükler. Yeniden gönderilmesi güvenli
/// olmayan istekte Retry/Hedging ek deneme üretmez; işaret kullanıcının bağlamında başka isteklere sızmaz.
/// </summary>
internal readonly struct AttemptSuppression
{
    private readonly bool _applied;
    private readonly bool _hadPrevious;
    private readonly object? _previous;

    private AttemptSuppression(bool hadPrevious, object? previous)
    {
        _applied = true;
        _hadPrevious = hadPrevious;
        _previous = previous;
    }

    public static AttemptSuppression Apply(AegisContext context)
    {
        var hadPrevious = context.TryGetProperty<object>(AegisContextKeys.SuppressAdditionalAttempts, out var previous);
        context.Properties[AegisContextKeys.SuppressAdditionalAttempts] = true;
        return new AttemptSuppression(hadPrevious, previous);
    }

    public void Restore(AegisContext context)
    {
        if (!_applied)
        {
            return;
        }

        if (_hadPrevious)
        {
            context.Properties[AegisContextKeys.SuppressAdditionalAttempts] = _previous;
        }
        else
        {
            context.Properties.Remove(AegisContextKeys.SuppressAdditionalAttempts);
        }
    }
}
