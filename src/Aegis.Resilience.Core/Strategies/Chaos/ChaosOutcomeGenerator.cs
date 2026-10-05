using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Strategies.Chaos;

/// <summary>
/// Ağırlıklı kaos sonucu üretici (Polly: <c>OutcomeGenerator&lt;T&gt;</c>): her enjeksiyonda ağırlığa göre bir istisna veya sonuç
/// seçer. Ör. %70 "503 yanıtı", %30 <see cref="TimeoutException"/>.
/// <code>
/// new ChaosOutcomeGenerator()
///     .AddResult(_ =&gt; new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), weight: 70)
///     .AddException&lt;TimeoutException&gt;(weight: 30)
/// </code>
/// </summary>
public sealed class ChaosOutcomeGenerator
{
    private readonly List<(Func<AegisContext, ChaosOutcome> Factory, int Weight)> _entries = [];
    private long _totalWeight; // long: int.MaxValue ağırlıklar toplanırken taşmaz (Polly 8.6.1 taşma hatasının dersi)

    /// <summary>Seçilen sonuç: ya <see cref="Exception"/> ya da <see cref="Result"/>.</summary>
    public readonly record struct ChaosOutcome(object? Result, Exception? Exception);

    /// <summary>Varsayılan kurucusuyla oluşturulan istisna ekler.</summary>
    public ChaosOutcomeGenerator AddException<TException>(int weight = 100)
        where TException : Exception, new() =>
        Add(static _ => new ChaosOutcome(null, new TException()), weight);

    /// <summary>Üreticiyle oluşturulan istisna ekler.</summary>
    public ChaosOutcomeGenerator AddException(Func<AegisContext, Exception> factory, int weight = 100)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Add(ctx => new ChaosOutcome(null, factory(ctx)), weight);
    }

    /// <summary>Sahte sonuç ekler (tipi boru hattının sonuç tipiyle uyumlu olmalı).</summary>
    public ChaosOutcomeGenerator AddResult(Func<AegisContext, object?> factory, int weight = 100)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Add(ctx => new ChaosOutcome(factory(ctx), null), weight);
    }

    private ChaosOutcomeGenerator Add(Func<AegisContext, ChaosOutcome> factory, int weight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weight);
        _entries.Add((factory, weight));
        _totalWeight += weight;
        return this;
    }

    /// <summary>
    /// <paramref name="random"/> ∈ [0, 1) değerine göre ağırlıklı seçim yapar. Üretici boşsa null: enjeksiyon yapılmaz (Polly 8.8 ile
    /// aynı); yanlış yapılandırılmış kaos ayarı gerçek çağrıyı düşürmez.
    /// </summary>
    internal ChaosOutcome? Generate(AegisContext context, double random)
    {
        if (_entries.Count == 0)
        {
            return null;
        }

        var roll = random * _totalWeight;
        foreach (var (factory, weight) in _entries)
        {
            roll -= weight;
            if (roll < 0)
            {
                return factory(context);
            }
        }

        return _entries[_entries.Count - 1].Factory(context);
    }
}
