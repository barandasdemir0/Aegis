using System.Diagnostics;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Strategies.RateLimiter;

/// <summary>
/// Segmentli Kayan Pencere (Segmented Sliding Window) tabanlı hız sınırlayıcı stratejisi.
/// Belirli bir zaman penceresini alt dilimlere bölerek zamanın akışıyla birlikte eski dilimleri kademeli olarak devre dışı bırakır.
/// </summary>
public sealed class SlidingWindowRateLimiterStrategy : AegisStrategy
{
    private readonly SlidingWindowRateLimiterOptions _staticOptions;

    /// <inheritdoc />
    public override object? Options => _staticOptions;
    private readonly AegisLock _lock = new();

    private readonly int _segmentCount;
    private readonly long[] _segmentIndices;
    private readonly int[] _segmentCounts;

    private SlidingWindowRateLimiterOptions? _lastGoodOptions;

    /// <summary>Canlı seçenekleri doğrulayarak çözer; geçersizse son geçerli seçeneklerle devam eder (AEGIS-145).</summary>
    private SlidingWindowRateLimiterOptions ResolveOptions() => DynamicOptionsResolver.Resolve(_staticOptions, _staticOptions.OptionsProvider, static o => o.Validate(), ref _lastGoodOptions);

    public override string Name => "SlidingWindowRateLimiter";

    public SlidingWindowRateLimiterStrategy(SlidingWindowRateLimiterOptions options)
    {
        _staticOptions = options ?? throw new ArgumentNullException(nameof(options));
        _staticOptions.Validate(); // fail-fast: geçersiz yapılandırma kurulum anında bildirilir (AEGIS-130)
        _segmentCount = Math.Max(1, options.SegmentsPerWindow);
        _segmentIndices = new long[_segmentCount];
        _segmentCounts = new int[_segmentCount];
    }

    protected override async ValueTask<Outcome<TResult>> ExecuteCoreAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        if (context.CancellationToken.IsCancellationRequested)
        {
            return Outcome<TResult>.FromException(new OperationCanceledException(context.CancellationToken));
        }

        var options = ResolveOptions();
        var acquired = TryAcquire(options);

        if (!acquired)
        {
            if (options.QueueTimeout > TimeSpan.Zero)
            {
                var waitStart = GetTimestamp();
                while (!acquired)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();

                    if (GetElapsedTime(waitStart) >= options.QueueTimeout)
                    {
                        break;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider, context.CancellationToken).ConfigureAwait(context.ContinueOnCapturedContext);
                    acquired = TryAcquire(options);
                }
            }

            if (!acquired)
            {
                // En erken kapasite, mevcut dilimin bitişinde (en eski dilim pencereden düşünce) açılır.
                var windowMs = Math.Max(1, (long)options.Window.TotalMilliseconds);
                var segmentMs = Math.Max(1, windowMs / _segmentCount);
                var nowMs = (long)TimeProvider.GetElapsedTime(0).TotalMilliseconds;
                var retryAfter = TimeSpan.FromMilliseconds(segmentMs - (nowMs % segmentMs));

                return await RateLimiterRejection.RejectAsync<TResult>(Telemetry, context, Name, options.OnRejected,
                    $"Kayan pencere hız kotası aşıldı: '{context.PipelineName ?? "default"}' boru hattında {options.Window.TotalSeconds:F0} saniyelik kayan pencerede en fazla {options.PermitLimit} isteğe izin verilmektedir.",
                    retryAfter).ConfigureAwait(context.ContinueOnCapturedContext);
            }
        }

        return await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
    }

    private bool TryAcquire(SlidingWindowRateLimiterOptions options)
    {
        lock (_lock)
        {
            var nowMs = (long)TimeProvider.GetElapsedTime(0).TotalMilliseconds;
            var windowMs = Math.Max(1, (long)options.Window.TotalMilliseconds);
            var segmentDurationMs = Math.Max(1, windowMs / _segmentCount);
            var currentSegmentIndex = nowMs / segmentDurationMs;

            // Toplam geçerli istek sayısını hesapla
            var totalInWindow = 0;
            for (var i = 0; i < _segmentCount; i++)
            {
                if (_segmentIndices[i] > 0 && currentSegmentIndex - _segmentIndices[i] < _segmentCount)
                {
                    totalInWindow += _segmentCounts[i];
                }
            }

            if (totalInWindow >= options.PermitLimit)
            {
                return false;
            }

            // İzin ver: Mevcut segmentin slotunu bul
            var slot = (int)(currentSegmentIndex % _segmentCount);
            if (_segmentIndices[slot] != currentSegmentIndex)
            {
                _segmentIndices[slot] = currentSegmentIndex;
                _segmentCounts[slot] = 1;
            }
            else
            {
                _segmentCounts[slot]++;
            }

            return true;
        }
    }
}
