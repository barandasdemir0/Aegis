using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Abstractions;

/// <summary>
/// Yerleşik stratejilerin (ve isteyen özel stratejilerin) tabanı: sıfır tahsisli "hızlı yol".
/// <para>
/// <see cref="IAegisStrategy"/> her katmanda bir sonrakini bir closure ile çağırmayı gerektirir (katman başına ~96 B).
/// Bu taban, sonraki adımı <c>TState</c> olarak alan <see cref="ExecuteCoreAsync{TResult, TState}"/>
/// sunar: boru hattı durumu bir struct ile geçirir ve statik, önbellekli bir delegate kullanır; tahsis olmaz.
/// Başarısızlık <see cref="Outcome{TResult}"/> ile taşınır, katmanlar arasında istisna fırlatılmaz.
/// </para>
/// <para>
/// <see cref="IAegisStrategy"/> değişmedi: bu tabanı kullanmayan özel stratejiler eskisi gibi çalışır (closure'lı yol).
/// </para>
/// </summary>
public abstract class AegisStrategy : IAegisStrategy
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <summary>
    /// Stratejinin kurulumdaki seçenek nesnesi (Polly: <c>StrategyDescriptor.Options</c>); testlerde boru hattının doğru
    /// kurulduğunu doğrulamak için. Canlı değişen seçenekler (<c>OptionsProvider</c>) burada yansımaz.
    /// </summary>
    public virtual object? Options => null;

    /// <summary>
    /// Stratejinin kullandığı saat (Polly: <c>ResiliencePipelineBuilderBase.TimeProvider</c>). Varsayılan
    /// <see cref="TimeProvider.System"/>. Testlerde sahte saat (ör. <c>FakeTimeProvider</c>) verilerek gecikme,
    /// zaman aşımı, devre açık kalma süresi ve hız sınırı pencereleri beklemeden, deterministik olarak sınanabilir.
    /// Boru hattında <c>AegisPipelineBuilder.TimeProvider</c> ile tüm stratejilere birlikte atanır.
    /// </summary>
    public TimeProvider TimeProvider { get; private set; } = TimeProvider.System;

    /// <summary>Saati değiştirir; strateji saate bağlı iç durumunu (ör. son dolum zamanı) yeniden kurar.</summary>
    public void UseTimeProvider(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        TimeProvider = timeProvider;
        _isSystemClock = ReferenceEquals(timeProvider, TimeProvider.System);
        OnTimeProviderChanged();
    }

    private bool _isSystemClock = true;

    private Telemetry.AegisStrategyTelemetry? _telemetry;

    /// <summary>
    /// Stratejinin olay kaynağı (Polly: <c>ResilienceStrategyTelemetry</c>). Olaylar <c>aegis.strategy.events</c> vb. standart
    /// metriklere ve boru hattına verilen dinleyicilere (ör. <c>ILogger</c>) gider. Dinleyici yoksa maliyetsizdir.
    /// </summary>
    public Telemetry.AegisStrategyTelemetry Telemetry => _telemetry ??= new(pipelineName: null, Name, options: null);

    /// <summary>Telemetri kaynağını ayarlar (boru hattı <c>Build</c> anında çağırır).</summary>
    public void UseTelemetry(string? pipelineName, Telemetry.AegisTelemetryOptions? options) =>
        UseTelemetry(pipelineName, instanceName: null, options);

    internal void UseTelemetry(string? pipelineName, string? instanceName, Telemetry.AegisTelemetryOptions? options) =>
        _telemetry = new(pipelineName, Name, options, instanceName);

    /// <summary>Monotonik zaman damgası. Sistem saatinde sanal çağrısız (doğrudan <see cref="System.Diagnostics.Stopwatch"/>).</summary>
    protected long GetTimestamp() => _isSystemClock ? System.Diagnostics.Stopwatch.GetTimestamp() : TimeProvider.GetTimestamp();

    /// <summary><paramref name="startTimestamp"/>'ten bu yana geçen süre (<see cref="GetTimestamp"/> ile uyumlu).</summary>
    protected TimeSpan GetElapsedTime(long startTimestamp) =>
        _isSystemClock ? System.Diagnostics.Stopwatch.GetElapsedTime(startTimestamp) : TimeProvider.GetElapsedTime(startTimestamp);

    /// <summary>Saat değiştiğinde çağrılır; saat tabanlı başlangıç değerleri tutan stratejiler bunu geçersiz kılar.</summary>
    protected virtual void OnTimeProviderChanged()
    {
    }

    /// <summary>
    /// Stratejinin asıl mantığı. <paramref name="callback"/> sonraki katmanı çalıştırır ve asla istisna fırlatmaz;
    /// başarısızlık <see cref="Outcome{TResult}.Exception"/> ile gelir. Uygulama da başarısızlığı mümkün olduğunca
    /// fırlatmak yerine <see cref="Outcome{TResult}.FromException"/> ile döndürmelidir. Yine de fırlatılan istisnalar
    /// katman sınırında yakalanıp sonuca çevrilir (dış stratejiler bunu normal bir başarısızlık olarak görür).
    /// </summary>
    protected abstract ValueTask<Outcome<TResult>> ExecuteCoreAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state);

    /// <summary>
    /// Klasik (istisna fırlatan) arayüz: doğrudan strateji kullanan kod ve testler için. İçeride hızlı yola yönlendirilir.
    /// </summary>
    public ValueTask<TResult> ExecuteAsync<TResult>(Func<AegisContext, ValueTask<TResult>> callback, AegisContext context)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(context);

        return OutcomeInvoker.Unwrap(ExecuteOutcomeAsync(
            static (ctx, userCallback) => OutcomeInvoker.Invoke(userCallback, ctx), context, callback));
    }

    /// <summary>Hızlı yol girişi: strateji içinden sızan istisnaları da sonuca çevirir; asla fırlatmaz.</summary>
    internal ValueTask<Outcome<TResult>> ExecuteOutcomeAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        ValueTask<Outcome<TResult>> pending;
        try
        {
            pending = ExecuteCoreAsync(callback, context, state);
        }
        catch (Exception ex)
        {
            return new ValueTask<Outcome<TResult>>(Outcome<TResult>.FromException(ex));
        }

        return pending.IsCompletedSuccessfully ? pending : AwaitGuardedAsync(pending);
    }

    private static async ValueTask<Outcome<TResult>> AwaitGuardedAsync<TResult>(ValueTask<Outcome<TResult>> pending)
    {
        try
        {
            return await pending.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Outcome<TResult>.FromException(ex);
        }
    }
}
