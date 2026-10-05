using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Exceptions;

namespace Aegis.TortureTests.Harness;

/// <summary>
/// Saldırı altındaki boru hattı: senaryo bir kez yazılır, her kütüphane kendi yerel API'siyle kurulup bu arayüzle
/// sürülür (aynı saldırı, aynı değişmezler).
/// </summary>
public interface ISubject : IAsyncDisposable
{
    ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> callback, CancellationToken cancellationToken);

    T Execute<T>(Func<CancellationToken, T> callback, CancellationToken cancellationToken);
}

public sealed class AegisSubject(IAegisPipeline pipeline) : ISubject
{
    public ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> callback, CancellationToken cancellationToken) =>
        pipeline.ExecuteAsync(callback, cancellationToken);

    public T Execute<T>(Func<CancellationToken, T> callback, CancellationToken cancellationToken) =>
        pipeline.Execute(callback, cancellationToken);

    public ValueTask DisposeAsync()
    {
        pipeline.Dispose();
        return default;
    }
}

/// <summary>Polly 8.8 boru hattı; Polly'de boru hattı kayıt defteri dışında dispose edilmez, kayıt defteri verilirse o bırakılır.</summary>
public sealed class PollySubject(ResiliencePipeline pipeline, IAsyncDisposable? owner = null) : ISubject
{
    public ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> callback, CancellationToken cancellationToken) =>
        pipeline.ExecuteAsync(callback, cancellationToken);

    public T Execute<T>(Func<CancellationToken, T> callback, CancellationToken cancellationToken) =>
        pipeline.Execute(callback, cancellationToken);

    public ValueTask DisposeAsync() => owner?.DisposeAsync() ?? default;
}

/// <summary>Kütüphaneler arası istisna sınıflandırması (her kütüphanenin kendi red/zaman aşımı tipleri).</summary>
public static class Failures
{
    public static bool IsTimeout(Exception ex) => ex is AegisTimeoutException or TimeoutRejectedException;

    public static bool IsBrokenCircuit(Exception ex) => ex is Aegis.Resilience.Core.Exceptions.BrokenCircuitException or Polly.CircuitBreaker.BrokenCircuitException;

    public static bool IsRejected(Exception ex) => ex is RateLimitRejectedException or RateLimiterRejectedException;

    /// <summary>Yapılandırmanın kurulumda reddi (geçerli bir savunma; çalışma anında patlamaktan iyidir).</summary>
    public static bool IsConfigurationRejection(Exception ex) =>
        ex is ArgumentException or System.ComponentModel.DataAnnotations.ValidationException;
}
