#if AEGIS_LEGACY
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Pipeline;

namespace Aegis.Resilience.Core.Abstractions;

/// <summary>
/// .NET Framework / netstandard2.0: <see cref="IAegisPipeline"/>'ın ek çalıştırma biçimleri (çalışma zamanı varsayılan arayüz
/// üyesi desteklemediği için genişletme metodu). İmzalar .NET 8+'daki arayüz üyeleriyle aynıdır; çağıran kod değişmez.
/// Somut <see cref="AegisPipeline"/> kendi closure'suz uygulamasına, diğerleri genel karşılığa yönlenir.
/// </summary>
public static class AegisPipelineExecutionExtensions
{
    public static ValueTask<TResult> ExecuteAsync<TResult, TState>(
        this IAegisPipeline pipeline, Func<AegisContext, TState, ValueTask<TResult>> callback, TState state, AegisContext? context = null) =>
        pipeline is AegisPipeline concrete
            ? concrete.ExecuteAsync(callback, state, context)
            : AegisPipelineFallbacks.ExecuteAsync(pipeline, callback, state, context);

    public static ValueTask ExecuteAsync<TState>(
        this IAegisPipeline pipeline, Func<AegisContext, TState, ValueTask> callback, TState state, AegisContext? context = null) =>
        pipeline is AegisPipeline concrete
            ? concrete.ExecuteAsync(callback, state, context)
            : AegisPipelineFallbacks.ExecuteAsync(pipeline, callback, state, context);

    public static ValueTask<TResult> ExecuteAsync<TResult>(
        this IAegisPipeline pipeline, Func<CancellationToken, ValueTask<TResult>> callback, CancellationToken cancellationToken) =>
        pipeline is AegisPipeline concrete
            ? concrete.ExecuteAsync(callback, cancellationToken)
            : AegisPipelineFallbacks.ExecuteAsync(pipeline, callback, cancellationToken);

    public static ValueTask ExecuteAsync(
        this IAegisPipeline pipeline, Func<CancellationToken, ValueTask> callback, CancellationToken cancellationToken) =>
        pipeline is AegisPipeline concrete
            ? concrete.ExecuteAsync(callback, cancellationToken)
            : AegisPipelineFallbacks.ExecuteAsync(pipeline, callback, cancellationToken);

    public static ValueTask<TResult> ExecuteAsync<TResult, TState>(
        this IAegisPipeline pipeline, Func<TState, CancellationToken, ValueTask<TResult>> callback, TState state, CancellationToken cancellationToken) =>
        pipeline is AegisPipeline concrete
            ? concrete.ExecuteAsync(callback, state, cancellationToken)
            : AegisPipelineFallbacks.ExecuteAsync(pipeline, callback, state, cancellationToken);

    public static ValueTask ExecuteAsync<TState>(
        this IAegisPipeline pipeline, Func<TState, CancellationToken, ValueTask> callback, TState state, CancellationToken cancellationToken) =>
        pipeline is AegisPipeline concrete
            ? concrete.ExecuteAsync(callback, state, cancellationToken)
            : AegisPipelineFallbacks.ExecuteAsync(pipeline, callback, state, cancellationToken);

    public static ValueTask<Outcome<TResult>> ExecuteOutcomeAsync<TResult, TState>(
        this IAegisPipeline pipeline, Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback, TState state, AegisContext? context = null) =>
        pipeline is AegisPipeline concrete
            ? concrete.ExecuteOutcomeAsync(callback, state, context)
            : AegisPipelineFallbacks.ExecuteOutcomeAsync(pipeline, callback, state, context);

    public static ValueTask<Outcome<TResult>> ExecuteOutcomeAsync<TResult>(
        this IAegisPipeline pipeline, Func<AegisContext, ValueTask<TResult>> callback, AegisContext? context = null) =>
        pipeline is AegisPipeline concrete
            ? concrete.ExecuteOutcomeAsync(callback, context)
            : AegisPipelineFallbacks.ExecuteOutcomeAsync(pipeline, callback, context);

    public static TResult Execute<TResult>(this IAegisPipeline pipeline, Func<AegisContext, TResult> callback, AegisContext? context = null) =>
        pipeline is AegisPipeline concrete ? concrete.Execute(callback, context) : AegisPipelineFallbacks.Execute(pipeline, callback, context);

    public static void Execute(this IAegisPipeline pipeline, Action<AegisContext> callback, AegisContext? context = null)
    {
        if (pipeline is AegisPipeline concrete)
        {
            concrete.Execute(callback, context);
        }
        else
        {
            AegisPipelineFallbacks.Execute(pipeline, callback, context);
        }
    }

    public static TResult Execute<TResult, TState>(
        this IAegisPipeline pipeline, Func<AegisContext, TState, TResult> callback, TState state, AegisContext? context = null) =>
        pipeline is AegisPipeline concrete ? concrete.Execute(callback, state, context) : AegisPipelineFallbacks.Execute(pipeline, callback, state, context);

    public static void Execute<TState>(this IAegisPipeline pipeline, Action<AegisContext, TState> callback, TState state, AegisContext? context = null)
    {
        if (pipeline is AegisPipeline concrete)
        {
            concrete.Execute(callback, state, context);
        }
        else
        {
            AegisPipelineFallbacks.Execute(pipeline, callback, state, context);
        }
    }

    public static TResult Execute<TResult>(this IAegisPipeline pipeline, Func<CancellationToken, TResult> callback, CancellationToken cancellationToken) =>
        pipeline is AegisPipeline concrete ? concrete.Execute(callback, cancellationToken) : AegisPipelineFallbacks.Execute(pipeline, callback, cancellationToken);

    public static void Execute(this IAegisPipeline pipeline, Action<CancellationToken> callback, CancellationToken cancellationToken)
    {
        if (pipeline is AegisPipeline concrete)
        {
            concrete.Execute(callback, cancellationToken);
        }
        else
        {
            AegisPipelineFallbacks.Execute(pipeline, callback, cancellationToken);
        }
    }

    public static TResult Execute<TResult>(this IAegisPipeline pipeline, Func<TResult> callback) =>
        pipeline is AegisPipeline concrete ? concrete.Execute(callback) : AegisPipelineFallbacks.Execute(pipeline, callback);

    public static void Execute(this IAegisPipeline pipeline, Action callback)
    {
        if (pipeline is AegisPipeline concrete)
        {
            concrete.Execute(callback);
        }
        else
        {
            AegisPipelineFallbacks.Execute(pipeline, callback);
        }
    }
}
#endif
