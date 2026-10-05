using System.Threading.RateLimiting;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.RateLimiting;

/// <summary>
/// <see cref="System.Threading.RateLimiting.RateLimiter"/> ile çalışan hız sınırlayıcı (Polly: <c>RateLimiterResilienceStrategy</c>).
/// İzin alınamazsa <see cref="Aegis.Resilience.Core.Exceptions.RateLimitRejectedException"/>; sınırlayıcının bildirdiği
/// <see cref="MetadataName.RetryAfter"/> istisnaya taşınır. İzin, geri çağrı bitince (hata olsa da) serbest bırakılır.
/// </summary>
public sealed class RateLimitingBridgeStrategy : AegisStrategy, IDisposable
{
    private readonly RateLimitingBridgeOptions _options;

    /// <inheritdoc />
    public override object? Options => _options;
    private readonly Func<AegisContext, CancellationToken, ValueTask<RateLimitLease>> _leaseFactory;

    public override string Name => "RateLimiter";

    public RateLimitingBridgeStrategy(RateLimitingBridgeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _leaseFactory = options.LeaseFactory ?? throw new ArgumentException(
            $"{nameof(RateLimitingBridgeOptions)}.{nameof(RateLimitingBridgeOptions.LeaseFactory)} zorunludur.", nameof(options));
    }

    protected override async ValueTask<Outcome<TResult>> ExecuteCoreAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        RateLimitLease lease;
        try
        {
            lease = await _leaseFactory(context, context.CancellationToken).ConfigureAwait(context.ContinueOnCapturedContext);
        }
        catch (Exception ex)
        {
            return Outcome<TResult>.FromException(ex); // iptal dahil: fırlatılmaz, en dışta bir kez fırlatılır
        }

        using (lease)
        {
            if (!lease.IsAcquired)
            {
                TimeSpan? retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : null;
                return await RateLimiterRejection.RejectAsync<TResult>(Telemetry, context, Name, _options.OnRejected,
                    $"Hız sınırı (System.Threading.RateLimiting) aşıldı: '{context.PipelineName ?? "default"}'.",
                    retryAfter, () => lease.GetAllMetadata().ToArray()).ConfigureAwait(context.ContinueOnCapturedContext);
            }

            return await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _options.OwnedLimiter?.Dispose();
}
