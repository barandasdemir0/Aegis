using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Pipeline;

/// <summary>
/// Polly v8 ile eşdeğer çalıştırma biçimleri: <c>TState</c> (closure'suz), <see cref="CancellationToken"/>,
/// fırlatmayan <see cref="ExecuteOutcomeAsync{TResult, TState}"/> ve senkron <c>Execute</c>. Hepsi aynı çekirdekten
/// (<c>ExecuteCoreAsync</c>) geçer; davranış (iptal, telemetri, bağlam havuzu, AEGIS-133/134/146) birebir aynıdır.
/// </summary>
public sealed partial class AegisPipeline
{
    // =============================================================================================================
    // Async — TState (closure tahsisi olmadan)
    // =============================================================================================================

    /// <summary>Durumu (<paramref name="state"/>) geri çağrıya closure oluşturmadan aktarır.</summary>
    public ValueTask<TResult> ExecuteAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<TResult>> callback,
        TState state,
        AegisContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return UnwrapAsync(
            ExecuteCoreAsync(
                static (ctx, s) => OutcomeInvoker.Invoke(s.Callback, ctx, s.State),
                new UserCallback<Func<AegisContext, TState, ValueTask<TResult>>, TState>(callback, state),
                context,
                default));
    }

    /// <summary>Dönüş değeri olmayan, durumlu biçim.</summary>
    public ValueTask ExecuteAsync<TState>(
        Func<AegisContext, TState, ValueTask> callback,
        TState state,
        AegisContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return UnwrapVoidAsync(
            ExecuteCoreAsync(
                static (ctx, s) => OutcomeInvoker.InvokeVoid(s.Callback, ctx, s.State),
                new UserCallback<Func<AegisContext, TState, ValueTask>, TState>(callback, state),
                context,
                default));
    }

    // =============================================================================================================
    // Async — CancellationToken (bağlam oluşturmadan; bağlam havuzdan alınır)
    // =============================================================================================================

    /// <summary>
    /// Yalnızca <see cref="CancellationToken"/> ile çalıştırır (Polly: <c>ExecuteAsync(ct =&gt; ..., token)</c>).
    /// Geri çağrıya giden token, stratejilerin (ör. Timeout, Hedging) bağladığı güncel token'dır.
    /// </summary>
    public ValueTask<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return UnwrapAsync(
            ExecuteCoreAsync(
                static (ctx, cb) => OutcomeInvoker.Invoke(static (c, f) => f(c.CancellationToken), ctx, cb),
                callback,
                null,
                cancellationToken));
    }

    /// <summary>Dönüş değeri olmayan <see cref="CancellationToken"/> biçimi.</summary>
    public ValueTask ExecuteAsync(
        Func<CancellationToken, ValueTask> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return UnwrapVoidAsync(
            ExecuteCoreAsync(
                static (ctx, cb) => OutcomeInvoker.InvokeVoid(static (c, f) => f(c.CancellationToken), ctx, cb),
                callback,
                null,
                cancellationToken));
    }

    /// <summary>Durumlu <see cref="CancellationToken"/> biçimi (closure'suz).</summary>
    public ValueTask<TResult> ExecuteAsync<TResult, TState>(
        Func<TState, CancellationToken, ValueTask<TResult>> callback,
        TState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return UnwrapAsync(
            ExecuteCoreAsync(
                static (ctx, s) => OutcomeInvoker.Invoke(static (c, u) => u.Callback(u.State, c.CancellationToken), ctx, s),
                new UserCallback<Func<TState, CancellationToken, ValueTask<TResult>>, TState>(callback, state),
                null,
                cancellationToken));
    }

    /// <summary>Dönüş değeri olmayan, durumlu <see cref="CancellationToken"/> biçimi.</summary>
    public ValueTask ExecuteAsync<TState>(
        Func<TState, CancellationToken, ValueTask> callback,
        TState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return UnwrapVoidAsync(
            ExecuteCoreAsync(
                static (ctx, s) => OutcomeInvoker.InvokeVoid(static (c, u) => u.Callback(u.State, c.CancellationToken), ctx, s),
                new UserCallback<Func<TState, CancellationToken, ValueTask>, TState>(callback, state),
                null,
                cancellationToken));
    }

    // =============================================================================================================
    // Async — Outcome (asla fırlatmaz)
    // =============================================================================================================

    /// <summary>
    /// Sonucu <see cref="Outcome{TResult}"/> olarak döner; başarısızlıkta istisna FIRLATMAZ (Polly: <c>ExecuteOutcomeAsync</c>).
    /// Yüksek hacimli yollarda istisna maliyetinden kaçınmak için kullanılır. Geri çağrı kendisi fırlatırsa da yakalanır.
    /// </summary>
    public ValueTask<Outcome<TResult>> ExecuteOutcomeAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        TState state,
        AegisContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(callback);

        // Terminal katman geri çağrıyı korumasız çağırır (iç sarmalayıcılar asla fırlatmaz); kullanıcının Outcome geri
        // çağrısı fırlatabileceği için koruma burada, yalnızca bu biçimde eklenir.
        return ExecuteCoreAsync(
            static (ctx, s) => OutcomeInvoker.InvokeOutcome(s.Callback, ctx, s.State),
            new UserCallback<Func<AegisContext, TState, ValueTask<Outcome<TResult>>>, TState>(callback, state),
            context,
            default);
    }

    /// <summary>Fırlatmayan biçim; sıradan değer döndüren geri çağrıyı sarar.</summary>
    public ValueTask<Outcome<TResult>> ExecuteOutcomeAsync<TResult>(
        Func<AegisContext, ValueTask<TResult>> callback,
        AegisContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return ExecuteCoreAsync(static (ctx, cb) => OutcomeInvoker.Invoke(cb, ctx), callback, context, default);
    }

    // =============================================================================================================
    // Senkron — Execute (Polly: Execute(...)). Stratejiler eşzamanlı tamamlanırsa bloklama olmaz; aksi halde
    // (ör. retry gecikmesi) çağıran iş parçacığı sonucu bekler. Tüm biçimler tek, korumalı yardımcıya iner.
    // =============================================================================================================

    /// <summary>Senkron geri çağrıyı çalıştırır; istisnayı sonuca çevirir (asla fırlatmaz).</summary>
    private static ValueTask<Outcome<TResult>> InvokeSync<TResult, TCallback>(
        Func<AegisContext, TCallback, TResult> invoker, AegisContext context, TCallback callback)
    {
        try
        {
            return new ValueTask<Outcome<TResult>>(Outcome<TResult>.FromResult(invoker(context, callback)));
        }
        catch (Exception ex)
        {
            return new ValueTask<Outcome<TResult>>(Outcome<TResult>.FromException(ex));
        }
    }

    private TResult ExecuteSync<TResult, TCallback>(
        Func<AegisContext, TCallback, TResult> invoker, TCallback callback, AegisContext? context, CancellationToken cancellationToken) =>
        UnwrapSync(ExecuteCoreAsync(
            static (ctx, s) => InvokeSync(s.Callback, ctx, s.State),
            new UserCallback<Func<AegisContext, TCallback, TResult>, TCallback>(invoker, callback),
            context,
            cancellationToken));

    /// <summary>Senkron, değer döndüren işlemi çalıştırır.</summary>
    public TResult Execute<TResult>(Func<AegisContext, TResult> callback, AegisContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return ExecuteSync(static (ctx, cb) => cb(ctx), callback, context, default);
    }

    /// <summary>Senkron, değer döndürmeyen işlemi çalıştırır.</summary>
    public void Execute(Action<AegisContext> callback, AegisContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ExecuteSync(static (ctx, cb) => { cb(ctx); return true; }, callback, context, default);
    }

    /// <summary>Senkron, durumlu (closure'suz) biçim.</summary>
    public TResult Execute<TResult, TState>(Func<AegisContext, TState, TResult> callback, TState state, AegisContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return ExecuteSync(
            static (ctx, s) => s.Callback(ctx, s.State),
            new UserCallback<Func<AegisContext, TState, TResult>, TState>(callback, state),
            context,
            default);
    }

    /// <summary>Senkron, dönüş değeri olmayan durumlu biçim.</summary>
    public void Execute<TState>(Action<AegisContext, TState> callback, TState state, AegisContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ExecuteSync(
            static (ctx, s) => { s.Callback(ctx, s.State); return true; },
            new UserCallback<Action<AegisContext, TState>, TState>(callback, state),
            context,
            default);
    }

    /// <summary>Senkron <see cref="CancellationToken"/> biçimi.</summary>
    public TResult Execute<TResult>(Func<CancellationToken, TResult> callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return ExecuteSync(static (ctx, cb) => cb(ctx.CancellationToken), callback, null, cancellationToken);
    }

    /// <summary>Senkron, dönüş değeri olmayan <see cref="CancellationToken"/> biçimi.</summary>
    public void Execute(Action<CancellationToken> callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ExecuteSync(static (ctx, cb) => { cb(ctx.CancellationToken); return true; }, callback, null, cancellationToken);
    }

    /// <summary>En basit senkron biçim (Polly: <c>Execute(() =&gt; ...)</c>).</summary>
    public TResult Execute<TResult>(Func<TResult> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return ExecuteSync(static (_, cb) => cb(), callback, null, default);
    }

    /// <summary>En basit senkron, dönüş değeri olmayan biçim (Polly: <c>Execute(() =&gt; ...)</c>).</summary>
    public void Execute(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ExecuteSync(static (_, cb) => { cb(); return true; }, callback, null, default);
    }
}
