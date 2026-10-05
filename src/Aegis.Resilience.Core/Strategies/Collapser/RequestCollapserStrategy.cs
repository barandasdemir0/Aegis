using System.Collections.Concurrent;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Telemetry;

namespace Aegis.Resilience.Core.Strategies.Collapser;

/// <summary>
/// Mükerrer eşzamanlı istekleri tek bir yürütmeye indirgeyen (Request Collapser / Singleflight) stratejisi.
/// Cache Stampede ve Thundering Herd problemlerini önler; aynı anda gelen N isteğin tek bir
/// arka uç sorgusu üzerinden sonucu paylaşmasını sağlar.
/// </summary>
public sealed class RequestCollapserStrategy : AegisStrategy
{
    private readonly RequestCollapserOptions _staticOptions;

    /// <inheritdoc />
    public override object? Options => _staticOptions;
    private readonly ConcurrentDictionary<(string Key, Type ResultType), Lazy<Task<object?>>> _inFlight = new();

    private RequestCollapserOptions? _lastGoodOptions;

    /// <summary>Canlı seçenekleri doğrulayarak çözer; geçersizse son geçerli seçeneklerle devam eder (AEGIS-145).</summary>
    private RequestCollapserOptions ResolveOptions() => DynamicOptionsResolver.Resolve(_staticOptions, _staticOptions.OptionsProvider, static o => o.Validate(), ref _lastGoodOptions);

    public override string Name => "RequestCollapser";

    public RequestCollapserStrategy(RequestCollapserOptions options)
    {
        _staticOptions = options ?? throw new ArgumentNullException(nameof(options));
        _staticOptions.Validate(); // fail-fast: anahtar seçici zorunludur (1.0.5)
    }

    protected override async ValueTask<Outcome<TResult>> ExecuteCoreAsync<TResult, TState>(
        Func<AegisContext, TState, ValueTask<Outcome<TResult>>> callback,
        AegisContext context,
        TState state)
    {
        var options = ResolveOptions();
        var key = options.KeySelector!(context);

        if (key is null or "")
        {
            return await callback(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
        }

        // Paylaşılan Task, herhangi bir çağıranın CancellationToken'ından bağımsız olmalıdır.
        // Aksi halde ilk çağıranın iptali tüm bekleyenleri çökertir (Cancellation Isolation).
        // GetOrAdd yarışında öksüz Task.Run başlatılmasını önlemek için Lazy<Task> kullanılır (AEGIS-101).
        Lazy<Task<object?>>? createdLazy = null;
        // Sonuç tipi anahtarın parçasıdır: aynı anahtarla farklı tipte sonuç bekleyen çağrılar asla aynı Task'i paylaşıp InvalidCastException almaz.
        var lazy = _inFlight.GetOrAdd((key, typeof(TResult)), k =>
        {
            createdLazy = new Lazy<Task<object?>>(() =>
            {
                // Capture the winning caller's properties before it can cancel and
                // its context can be returned to the pool.
                var isolatedContext = context.CreateChild(CancellationToken.None);
                return Task.Run(async () =>
                {
                    try
                    {
                        // Sonuç (değer ya da istisna) tek kez kutulanıp tüm bekleyenlerle paylaşılır
                        return (object?)await callback(isolatedContext, state).ConfigureAwait(isolatedContext.ContinueOnCapturedContext);
                    }
                    finally
                    {
                        // Atomik kaldırma: Yalnızca bu Lazy Task hala sözlükte mevcutsa kaldır.
                        // Yeni eklenmiş olabilecek bir sonraki görevi asla kazara silme (Race condition fix - AEGIS-006 / AEGIS-101).
                        ((ICollection<KeyValuePair<(string Key, Type ResultType), Lazy<Task<object?>>>>)_inFlight).Remove(
                            new KeyValuePair<(string Key, Type ResultType), Lazy<Task<object?>>>(k, createdLazy!));
                    }
                });
            }, LazyThreadSafetyMode.ExecutionAndPublication);

            return createdLazy;
        });

        if (!ReferenceEquals(lazy, createdLazy))
        {
            // Bu çağrı uçuştaki bir isteğe katıldı (yeni çağrı yapılmadı).
            Telemetry.Report(AegisEventNames.OnRequestCollapsed, AegisEventSeverity.Debug, context);
        }

        // Her çağıran kendi CancellationToken'ı ile bekler.
        // Biri iptal ederse arka plandaki ortak görev diğerleri için çalışmaya devam ederken çağıran hemen döner.
        var task = lazy.Value;
        var shared = await task.WaitAsync(context.CancellationToken).ConfigureAwait(context.ContinueOnCapturedContext);

        return (Outcome<TResult>)shared!;
    }
}
