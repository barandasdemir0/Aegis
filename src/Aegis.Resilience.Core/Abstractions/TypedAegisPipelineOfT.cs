using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Abstractions;

/// <summary>Tipsiz boru hattını tipli arayüzle sunan ince sarmalayıcı (ek tahsis yok; tüm çağrılar doğrudan iletilir).</summary>
internal sealed class TypedAegisPipeline<TResult>(IAegisPipeline pipeline) : IAegisPipeline<TResult>
{
    public string Name => pipeline.Name;

    public IReadOnlyList<IAegisStrategy> Strategies => pipeline.Strategies;

    public IAegisPipeline Untyped => pipeline;

    public ValueTask<TResult> ExecuteAsync(Func<AegisContext, ValueTask<TResult>> callback, AegisContext? context = null) =>
        pipeline.ExecuteAsync(callback, context);

    public ValueTask<TResult> ExecuteAsync<TState>(Func<AegisContext, TState, ValueTask<TResult>> callback, TState state, AegisContext? context = null) =>
        pipeline.ExecuteAsync(callback, state, context);

    public ValueTask<TResult> ExecuteAsync(Func<CancellationToken, ValueTask<TResult>> callback, CancellationToken cancellationToken) =>
        pipeline.ExecuteAsync(callback, cancellationToken);

    public ValueTask<TResult> ExecuteAsync<TState>(Func<TState, CancellationToken, ValueTask<TResult>> callback, TState state, CancellationToken cancellationToken) =>
        pipeline.ExecuteAsync(callback, state, cancellationToken);

    public ValueTask<Outcome<TResult>> ExecuteOutcomeAsync(Func<AegisContext, ValueTask<TResult>> callback, AegisContext? context = null) =>
        pipeline.ExecuteOutcomeAsync(callback, context);

    public ValueTask<Outcome<TResult>> ExecuteOutcomeAsync<TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback, TState state, AegisContext? context = null) =>
        pipeline.ExecuteOutcomeAsync(callback, state, context);

    public TResult Execute(Func<AegisContext, TResult> callback, AegisContext? context = null) => pipeline.Execute(callback, context);

    public TResult Execute<TState>(Func<AegisContext, TState, TResult> callback, TState state, AegisContext? context = null) =>
        pipeline.Execute(callback, state, context);

    public TResult Execute(Func<CancellationToken, TResult> callback, CancellationToken cancellationToken) =>
        pipeline.Execute(callback, cancellationToken);

    public TResult Execute(Func<TResult> callback) => pipeline.Execute(callback);

    public void Dispose() => pipeline.Dispose();
}
