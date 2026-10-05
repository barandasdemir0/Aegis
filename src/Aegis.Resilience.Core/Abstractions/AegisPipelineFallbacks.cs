using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Abstractions;

/// <summary>
/// Ek çalıştırma biçimlerinin genel karşılıkları: yalnızca iki temel <c>IAegisPipeline.ExecuteAsync</c>
/// metoduna dayanır. Özel (kullanıcı yazımı) boru hatları için kullanılır; <c>AegisPipeline</c> bunların hepsini kendisi,
/// closure'suz ve tahsissiz uygular. Mantık tek yerdedir: .NET 8+ varsayılan arayüz üyeleri ve eski hedeflerdeki
/// genişletme metotları buraya yönlenir.
/// </summary>
internal static class AegisPipelineFallbacks
{
    public static ValueTask<TResult> ExecuteAsync<TResult, TState>(
        IAegisPipeline pipeline, Func<AegisContext, TState, ValueTask<TResult>> callback, TState state, AegisContext? context) =>
        pipeline.ExecuteAsync(ctx => callback(ctx, state), context);

    public static ValueTask ExecuteAsync<TState>(
        IAegisPipeline pipeline, Func<AegisContext, TState, ValueTask> callback, TState state, AegisContext? context) =>
        pipeline.ExecuteAsync(ctx => callback(ctx, state), context);

    public static ValueTask<TResult> ExecuteAsync<TResult>(
        IAegisPipeline pipeline, Func<CancellationToken, ValueTask<TResult>> callback, CancellationToken cancellationToken) =>
        pipeline.ExecuteAsync(ctx => callback(ctx.CancellationToken), new AegisContext(cancellationToken));

    public static ValueTask ExecuteAsync(
        IAegisPipeline pipeline, Func<CancellationToken, ValueTask> callback, CancellationToken cancellationToken) =>
        pipeline.ExecuteAsync(ctx => callback(ctx.CancellationToken), new AegisContext(cancellationToken));

    public static ValueTask<TResult> ExecuteAsync<TResult, TState>(
        IAegisPipeline pipeline, Func<TState, CancellationToken, ValueTask<TResult>> callback, TState state, CancellationToken cancellationToken) =>
        pipeline.ExecuteAsync(ctx => callback(state, ctx.CancellationToken), new AegisContext(cancellationToken));

    public static ValueTask ExecuteAsync<TState>(
        IAegisPipeline pipeline, Func<TState, CancellationToken, ValueTask> callback, TState state, CancellationToken cancellationToken) =>
        pipeline.ExecuteAsync(ctx => callback(state, ctx.CancellationToken), new AegisContext(cancellationToken));

    public static async ValueTask<Outcome<TResult>> ExecuteOutcomeAsync<TResult, TState>(
        IAegisPipeline pipeline, Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback, TState state, AegisContext? context)
    {
        try
        {
            return Outcome<TResult>.FromResult(await pipeline.ExecuteAsync(
                async ctx => (await callback(ctx, state).ConfigureAwait(false)).GetResultOrThrow(),
                context).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return Outcome<TResult>.FromException(ex);
        }
    }

    public static async ValueTask<Outcome<TResult>> ExecuteOutcomeAsync<TResult>(
        IAegisPipeline pipeline, Func<AegisContext, ValueTask<TResult>> callback, AegisContext? context)
    {
        try
        {
            return Outcome<TResult>.FromResult(await pipeline.ExecuteAsync(callback, context).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return Outcome<TResult>.FromException(ex);
        }
    }

    public static TResult Execute<TResult>(IAegisPipeline pipeline, Func<AegisContext, TResult> callback, AegisContext? context) =>
        pipeline.ExecuteAsync(ctx => new ValueTask<TResult>(callback(ctx)), context).AsTask().GetAwaiter().GetResult();

    public static void Execute(IAegisPipeline pipeline, Action<AegisContext> callback, AegisContext? context) =>
        pipeline.ExecuteAsync(ctx => { callback(ctx); return default; }, context).AsTask().GetAwaiter().GetResult();

    public static TResult Execute<TResult, TState>(IAegisPipeline pipeline, Func<AegisContext, TState, TResult> callback, TState state, AegisContext? context) =>
        Execute(pipeline, ctx => callback(ctx, state), context);

    public static void Execute<TState>(IAegisPipeline pipeline, Action<AegisContext, TState> callback, TState state, AegisContext? context) =>
        Execute(pipeline, ctx => callback(ctx, state), context);

    public static TResult Execute<TResult>(IAegisPipeline pipeline, Func<CancellationToken, TResult> callback, CancellationToken cancellationToken) =>
        Execute(pipeline, ctx => callback(ctx.CancellationToken), new AegisContext(cancellationToken));

    public static void Execute(IAegisPipeline pipeline, Action<CancellationToken> callback, CancellationToken cancellationToken) =>
        Execute(pipeline, ctx => callback(ctx.CancellationToken), new AegisContext(cancellationToken));

    public static TResult Execute<TResult>(IAegisPipeline pipeline, Func<TResult> callback) =>
        Execute(pipeline, _ => callback(), context: null);

    public static void Execute(IAegisPipeline pipeline, Action callback) =>
        Execute(pipeline, _ => callback(), context: null);
}
