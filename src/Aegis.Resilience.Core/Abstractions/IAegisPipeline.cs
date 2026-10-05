using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Abstractions;

/// <summary>
/// Birleştirilmiş dayanıklılık boru hattı (Pipeline) arayüzü.
/// <para>
/// Uygulayıcı yalnızca iki temel <c>ExecuteAsync</c> metodunu yazar; Polly v8 eşdeğeri diğer biçimler (TState,
/// CancellationToken, senkron <c>Execute</c>, fırlatmayan <c>ExecuteOutcomeAsync</c>) bu ikisine düşer. .NET 8+'da bunlar
/// varsayılan arayüz üyeleridir; .NET Framework / netstandard2.0'da (çalışma zamanı varsayılan gövde desteklemez) aynı
/// imzalarla <c>AegisPipelineExecutionExtensions</c> genişletme metotlarıdır. Çağıran kod iki dünyada aynıdır.
/// </para>
/// </summary>
public interface IAegisPipeline : IDisposable
{
    /// <summary>
    /// Boru hattının benzersiz adı.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Boru hattında yer alan sıralı stratejilerin listesi.
    /// </summary>
    IReadOnlyList<IAegisStrategy> Strategies { get; }

    /// <summary>
    /// Bir dönüş değeri olan asenkron işlemi boru hattından geçirerek çalıştırır.
    /// </summary>
    ValueTask<TResult> ExecuteAsync<TResult>(
        Func<AegisContext, ValueTask<TResult>> callback,
        AegisContext? context = null);

    /// <summary>
    /// Dönüş değeri olmayan asenkron işlemi boru hattından geçirerek çalıştırır.
    /// </summary>
    ValueTask ExecuteAsync(
        Func<AegisContext, ValueTask> callback,
        AegisContext? context = null);

#if !AEGIS_LEGACY
    /// <summary>Durumu geri çağrıya closure oluşturmadan aktarır.</summary>
    ValueTask<TResult> ExecuteAsync<TResult, TState>(Func<AegisContext, TState, ValueTask<TResult>> callback, TState state, AegisContext? context = null) =>
        AegisPipelineFallbacks.ExecuteAsync(this, callback, state, context);

    /// <summary>Dönüş değeri olmayan, durumlu biçim.</summary>
    ValueTask ExecuteAsync<TState>(Func<AegisContext, TState, ValueTask> callback, TState state, AegisContext? context = null) =>
        AegisPipelineFallbacks.ExecuteAsync(this, callback, state, context);

    /// <summary>Yalnızca <see cref="CancellationToken"/> ile çalıştırır.</summary>
    ValueTask<TResult> ExecuteAsync<TResult>(Func<CancellationToken, ValueTask<TResult>> callback, CancellationToken cancellationToken) =>
        AegisPipelineFallbacks.ExecuteAsync(this, callback, cancellationToken);

    /// <summary>Dönüş değeri olmayan <see cref="CancellationToken"/> biçimi.</summary>
    ValueTask ExecuteAsync(Func<CancellationToken, ValueTask> callback, CancellationToken cancellationToken) =>
        AegisPipelineFallbacks.ExecuteAsync(this, callback, cancellationToken);

    /// <summary>Durumlu <see cref="CancellationToken"/> biçimi.</summary>
    ValueTask<TResult> ExecuteAsync<TResult, TState>(Func<TState, CancellationToken, ValueTask<TResult>> callback, TState state, CancellationToken cancellationToken) =>
        AegisPipelineFallbacks.ExecuteAsync(this, callback, state, cancellationToken);

    /// <summary>Dönüş değeri olmayan, durumlu <see cref="CancellationToken"/> biçimi.</summary>
    ValueTask ExecuteAsync<TState>(Func<TState, CancellationToken, ValueTask> callback, TState state, CancellationToken cancellationToken) =>
        AegisPipelineFallbacks.ExecuteAsync(this, callback, state, cancellationToken);

    /// <summary>Sonucu <see cref="Outcome{TResult}"/> olarak döner; başarısızlıkta istisna fırlatmaz.</summary>
    ValueTask<Outcome<TResult>> ExecuteOutcomeAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback, TState state, AegisContext? context = null) =>
        AegisPipelineFallbacks.ExecuteOutcomeAsync(this, callback, state, context);

    /// <summary>Fırlatmayan biçim; sıradan değer döndüren geri çağrıyı sarar.</summary>
    ValueTask<Outcome<TResult>> ExecuteOutcomeAsync<TResult>(Func<AegisContext, ValueTask<TResult>> callback, AegisContext? context = null) =>
        AegisPipelineFallbacks.ExecuteOutcomeAsync(this, callback, context);

    /// <summary>Senkron, değer döndüren işlemi çalıştırır.</summary>
    TResult Execute<TResult>(Func<AegisContext, TResult> callback, AegisContext? context = null) =>
        AegisPipelineFallbacks.Execute(this, callback, context);

    /// <summary>Senkron, değer döndürmeyen işlemi çalıştırır.</summary>
    void Execute(Action<AegisContext> callback, AegisContext? context = null) =>
        AegisPipelineFallbacks.Execute(this, callback, context);

    /// <summary>Senkron, durumlu biçim.</summary>
    TResult Execute<TResult, TState>(Func<AegisContext, TState, TResult> callback, TState state, AegisContext? context = null) =>
        AegisPipelineFallbacks.Execute(this, callback, state, context);

    /// <summary>Senkron, dönüş değeri olmayan durumlu biçim.</summary>
    void Execute<TState>(Action<AegisContext, TState> callback, TState state, AegisContext? context = null) =>
        AegisPipelineFallbacks.Execute(this, callback, state, context);

    /// <summary>Senkron <see cref="CancellationToken"/> biçimi.</summary>
    TResult Execute<TResult>(Func<CancellationToken, TResult> callback, CancellationToken cancellationToken) =>
        AegisPipelineFallbacks.Execute(this, callback, cancellationToken);

    /// <summary>Senkron, dönüş değeri olmayan <see cref="CancellationToken"/> biçimi.</summary>
    void Execute(Action<CancellationToken> callback, CancellationToken cancellationToken) =>
        AegisPipelineFallbacks.Execute(this, callback, cancellationToken);

    /// <summary>En basit senkron biçim.</summary>
    TResult Execute<TResult>(Func<TResult> callback) => AegisPipelineFallbacks.Execute(this, callback);

    /// <summary>En basit senkron, dönüş değeri olmayan biçim.</summary>
    void Execute(Action callback) => AegisPipelineFallbacks.Execute(this, callback);
#endif
}
