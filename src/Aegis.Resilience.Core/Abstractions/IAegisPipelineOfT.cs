using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Core.Abstractions;

/// <summary>
/// Tek bir sonuç tipine bağlı boru hattı (Polly: <c>ResiliencePipeline&lt;T&gt;</c>). Yalnızca <typeparamref name="TResult"/>
/// dönen işlemleri kabul eder; yanlış tipte kullanım derleme anında yakalanır. <c>builder.Build&lt;TResult&gt;()</c> ile oluşturulur
/// ve aynı sıfır tahsisli çekirdeği kullanır.
/// </summary>
public interface IAegisPipeline<TResult> : IDisposable
{
    string Name { get; }

    IReadOnlyList<IAegisStrategy> Strategies { get; }

    /// <summary>Tipsiz boru hattı (farklı tipte çağrılar veya mevcut API'lerle birlikte kullanım için).</summary>
    IAegisPipeline Untyped { get; }

    ValueTask<TResult> ExecuteAsync(Func<AegisContext, ValueTask<TResult>> callback, AegisContext? context = null);

    ValueTask<TResult> ExecuteAsync<TState>(Func<AegisContext, TState, ValueTask<TResult>> callback, TState state, AegisContext? context = null);

    ValueTask<TResult> ExecuteAsync(Func<CancellationToken, ValueTask<TResult>> callback, CancellationToken cancellationToken);

    ValueTask<TResult> ExecuteAsync<TState>(Func<TState, CancellationToken, ValueTask<TResult>> callback, TState state, CancellationToken cancellationToken);

    ValueTask<Outcome<TResult>> ExecuteOutcomeAsync(Func<AegisContext, ValueTask<TResult>> callback, AegisContext? context = null);

    ValueTask<Outcome<TResult>> ExecuteOutcomeAsync<TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback, TState state, AegisContext? context = null);

    TResult Execute(Func<AegisContext, TResult> callback, AegisContext? context = null);

    TResult Execute<TState>(Func<AegisContext, TState, TResult> callback, TState state, AegisContext? context = null);

    TResult Execute(Func<CancellationToken, TResult> callback, CancellationToken cancellationToken);

    TResult Execute(Func<TResult> callback);
}
