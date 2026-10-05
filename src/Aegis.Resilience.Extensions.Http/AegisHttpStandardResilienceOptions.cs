using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Core.Strategies.RateLimiter;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Core.Strategies.Timeout;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// Standart HTTP dayanıklılık seçenekleri (Microsoft: <c>HttpStandardResilienceOptions</c>). Varsayılanlar Microsoft ile
/// aynıdır. Zincir dıştan içe: eşzamanlılık sınırı → toplam zaman aşımı → retry → devre kesici → deneme zaman aşımı.
/// <c>appsettings.json</c>'dan bağlanabilir (<c>AddStandardAegisHandler(IConfigurationSection)</c>).
/// </summary>
public sealed class AegisHttpStandardResilienceOptions
{
    /// <summary>Eşzamanlılık sınırı (Microsoft varsayılanı: 1000 izin, kuyruk yok).</summary>
    public ConcurrencyLimiterOptions RateLimiter { get; set; } = new() { MaxConcurrentExecutions = 1000, QueueTimeout = TimeSpan.Zero };

    /// <summary>Tüm denemeler dahil toplam süre (varsayılan 30 sn).</summary>
    public TimeoutOptions TotalRequestTimeout { get; set; } = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Yeniden deneme (varsayılan 3 deneme, üstel, jitter, 2 sn taban).</summary>
    public RetryOptions Retry { get; set; } = new()
    {
        MaxRetryAttempts = 3,
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        Delay = TimeSpan.FromSeconds(2)
    };

    /// <summary>Devre kesici (varsayılan: %10 hata, 30 sn pencere, en az 100 çağrı, 5 sn açık).</summary>
    public CircuitBreakerOptions CircuitBreaker { get; set; } = new()
    {
        FailureRatio = 0.1,
        SamplingDuration = TimeSpan.FromSeconds(30),
        MinimumThroughput = 100,
        BreakDuration = TimeSpan.FromSeconds(5)
    };

    /// <summary>Tek denemenin süresi (varsayılan 10 sn).</summary>
    public TimeoutOptions AttemptTimeout { get; set; } = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>5xx, 408, 429 yanıtlarını geçici hata say (varsayılan: evet).</summary>
    public bool HandleHttpFailureStatuses { get; set; } = true;

    /// <summary>Idempotent olmayan (Idempotency-Key'siz POST/PATCH) istekleri de yeniden dene (varsayılan: hayır).</summary>
    public bool AllowNonIdempotentRetry { get; set; }

    /// <summary>
    /// Denemeler tükenince son geçici yanıtı (5xx, 408, 429) istisna yerine olduğu gibi döndür (Microsoft davranışı;
    /// varsayılan: hayır). Kapalıyken Aegis <c>HttpRequestException</c> (StatusCode dolu) fırlatır. Açıkken çağıran
    /// <c>response.StatusCode</c> ile karar verir; Microsoft'tan geçişte kod değişmeden çalışır. Devre açıkken ya da zaman
    /// aşımında yine istisna fırlatılır (Microsoft ile aynı).
    /// </summary>
    public bool ReturnFinalResponse { get; set; }

    /// <summary>Yeniden denemesi kapatılan yöntemler (<see cref="DisableRetryFor"/>).</summary>
    internal HashSet<HttpMethod> RetryDisabledMethods { get; } = [];

    /// <summary>
    /// Verilen yöntemlerde yeniden denemeyi tamamen kapatır (Microsoft: <c>Retry.DisableFor(...)</c>). Açık kapatma her
    /// kuraldan önce gelir: <c>Idempotency-Key</c> başlığı ve <see cref="AllowNonIdempotentRetry"/> bu yöntemleri açmaz.
    /// Örnek: <c>o.DisableRetryFor(HttpMethod.Delete)</c> ile silme isteği tek deneme yapar.
    /// </summary>
    public AegisHttpStandardResilienceOptions DisableRetryFor(params HttpMethod[] methods)
    {
        HttpHandlerRules.AddDisabled(RetryDisabledMethods, methods);
        return this;
    }

    /// <summary>
    /// Güvensiz yöntemlerde (POST, PATCH, PUT, DELETE, CONNECT) yeniden denemeyi kapatır (Microsoft:
    /// <c>Retry.DisableForUnsafeHttpMethods()</c>). Varsayılan Aegis davranışından daha katıdır: idempotent PUT/DELETE ve
    /// <c>Idempotency-Key</c> taşıyan POST da yeniden denenmez.
    /// </summary>
    public AegisHttpStandardResilienceOptions DisableRetryForUnsafeHttpMethods() => DisableRetryFor(HttpHandlerRules.UnsafeMethods);

    /// <summary>
    /// İsteği boru hattı anahtarına eşler (Microsoft: <c>SelectPipelineBy</c>). Her anahtar kendi devre kesicisini, kotasını
    /// ve eşzamanlılık sınırını alır; ör. bir hedef host çökünce diğer hostlara giden istekler etkilenmez. Null ise tüm
    /// istekler tek boru hattını paylaşır.
    /// </summary>
    public Func<HttpRequestMessage, string>? PipelineSelector { get; set; }

    /// <summary>Anahtar başına kurulabilecek en fazla boru hattı (kardinalite koruması; varsayılan 1000).</summary>
    public int MaxPipelines { get; set; } = 1000;

    /// <summary>Hedef authority (şema + host + port) başına ayrı boru hattı (Microsoft: <c>SelectPipelineByAuthority</c>).</summary>
    public AegisHttpStandardResilienceOptions SelectPipelineByAuthority()
    {
        PipelineSelector = AegisHttpPipelineSelectors.ByAuthority;
        return this;
    }

    /// <summary>İstek başına özel anahtarla ayrı boru hattı (ör. hedef servis adı, kiracı başlığı).</summary>
    public AegisHttpStandardResilienceOptions SelectPipelineBy(Func<HttpRequestMessage, string> selector)
    {
        PipelineSelector = selector ?? throw new ArgumentNullException(nameof(selector));
        return this;
    }

    /// <summary>
    /// Tüm seçenekleri ve aralarındaki tutarlılığı doğrular (Microsoft ile aynı kurallar):
    /// deneme zaman aşımı toplam zaman aşımından küçük olmalı; devre kesici örnekleme penceresi deneme zaman aşımının
    /// en az iki katı olmalı (aksi halde yavaş denemeler pencereye hiç düşmez).
    /// </summary>
    public void Validate()
    {
        RateLimiter.Validate();
        TotalRequestTimeout.Validate();
        Retry.Validate();
        CircuitBreaker.Validate();
        AttemptTimeout.Validate();
        Aegis.Resilience.Core.Abstractions.AegisOptionsValidator.AtLeast(MaxPipelines, 1, nameof(AegisHttpStandardResilienceOptions), nameof(MaxPipelines));
        StandardOptionsValidation.EnsureConsistency(TotalRequestTimeout.Timeout, AttemptTimeout.Timeout, CircuitBreaker.SamplingDuration,
            nameof(AegisHttpStandardResilienceOptions));

        if (PipelineSelector is not null && CircuitBreaker.StateProvider is not null)
        {
            throw new ArgumentException(
                $"{nameof(AegisHttpStandardResilienceOptions)}: StateProvider tek devreye bağlanır; anahtar başına boru hattıyla (PipelineSelector) " +
                "birlikte kullanılamaz. Durum için ManualControl veya health check kullanın.");
        }
    }
}
