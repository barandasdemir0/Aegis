using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Aegis.Resilience.Core.Pipeline;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// Microsoft.Extensions.Http.Resilience eşdeğeri standart işleyiciler: seçenek nesnesiyle veya <c>appsettings.json</c>
/// bölümüyle yapılandırma, tutarlılık doğrulaması, yapılandırma değişince yeniden yükleme, authority başına boru hattı,
/// standart hedging + yönlendirme grupları.
/// </summary>
public static class StandardHandlerExtensions
{
    /// <summary>
    /// Standart dayanıklılık işleyicisi (Microsoft: <c>AddStandardResilienceHandler(Action)</c>). Varsayılanlar Microsoft
    /// ile aynıdır; seçenekler kurulumda doğrulanır. Hedef host başına ayrı devre için <c>o.SelectPipelineByAuthority()</c>.
    /// </summary>
    public static IHttpClientBuilder AddStandardAegisHandler(this IHttpClientBuilder builder, Action<AegisHttpStandardResilienceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return AddStandard(builder, section: null, configure);
    }

    /// <summary>
    /// Standart dayanıklılık işleyicisini yapılandırma bölümünden kurar (Microsoft: <c>AddStandardResilienceHandler(IConfigurationSection)</c>).
    /// Bölüm değişince (ör. appsettings.json düzenlendi, reloadOnChange) boru hatları yeniden kurulur; uçuştaki istekler
    /// eski boru hatlarıyla tamamlanır. Geçersiz yeni yapılandırma yüklenmez, eski yapılandırma çalışmaya devam eder.
    /// </summary>
    /// <param name="builder">HttpClient kurucu.</param>
    /// <param name="section">Ör. <c>configuration.GetSection("Aegis:OdemeServisi")</c>. Alt bölümler: RateLimiter, TotalRequestTimeout, Retry, CircuitBreaker, AttemptTimeout.</param>
    /// <param name="configure">Bağlamadan sonra koddan ek ayar (ör. koşullar, bildirimler, <c>SelectPipelineByAuthority()</c>).</param>
    public static IHttpClientBuilder AddStandardAegisHandler(
        this IHttpClientBuilder builder,
        IConfigurationSection section,
        Action<AegisHttpStandardResilienceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(section);
        return AddStandard(builder, section, configure);
    }

    /// <summary>
    /// Standart dayanıklılık işleyicisini DI servisleriyle yapılandırır (Microsoft: <c>AddStandardResilienceHandler().Configure((o, sp) =&gt; ...)</c>);
    /// ör. bildirimde <c>ILogger</c>, koşulda bir servis. Seçenekler kapsayıcı başına bir kez, ilk istemci oluşturulurken kurulur
    /// ve doğrulanır; boru hattı kapsayıcıyla birlikte dispose edilir.
    /// </summary>
    public static IHttpClientBuilder AddStandardAegisHandler(
        this IHttpClientBuilder builder,
        Action<AegisHttpStandardResilienceOptions, IServiceProvider> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var name = builder.Name + "_AegisStandard";
        UseHandlerTimeoutOnly(builder);
        var added = HttpClientBuilderExtensions.AddPerServiceProvider(builder,
            sp => new Reloadable<StandardHandlerRuntime>(() => new StandardHandlerRuntime(name, Load(section: null, o => configure(o, sp)))),
            runtime => new ReloadableResilienceHandler<StandardHandlerRuntime>(runtime));
        EnsureSingleStandardHandler(added);
        return added;
    }

    /// <summary>
    /// Standart hedging işleyicisi (Microsoft: <c>AddStandardHedgingHandler</c>): toplam zaman aşımı + hedging + uç nokta
    /// başına eşzamanlılık / devre kesici / deneme zaman aşımı, sıralı veya ağırlıklı yönlendirme grupları.
    /// </summary>
    public static IHttpClientBuilder AddStandardAegisHedgingHandler(this IHttpClientBuilder builder, Action<AegisHttpStandardHedgingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return AddHedging(builder, section: null, configure);
    }

    /// <summary>
    /// Standart hedging işleyicisini yapılandırma bölümünden kurar (gruplar ve uç noktalar dahil). Bölüm değişince
    /// yeniden kurulur; uçuştaki istekler eski yapılandırmayla tamamlanır.
    /// </summary>
    public static IHttpClientBuilder AddStandardAegisHedgingHandler(
        this IHttpClientBuilder builder,
        IConfigurationSection section,
        Action<AegisHttpStandardHedgingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(section);
        return AddHedging(builder, section, configure);
    }

    /// <summary>
    /// Standart hedging işleyicisini DI servisleriyle yapılandırır (bkz. <see cref="AddStandardAegisHandler(IHttpClientBuilder, Action{AegisHttpStandardResilienceOptions, IServiceProvider})"/>).
    /// </summary>
    public static IHttpClientBuilder AddStandardAegisHedgingHandler(
        this IHttpClientBuilder builder,
        Action<AegisHttpStandardHedgingOptions, IServiceProvider> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var name = builder.Name + "_AegisHedging";
        UseHandlerTimeoutOnly(builder);
        var added = HttpClientBuilderExtensions.AddPerServiceProvider(builder,
            sp => new Reloadable<HedgingHandlerRuntime>(() => new HedgingHandlerRuntime(name, Load(section: null, o => configure(o, sp)))),
            runtime => new AegisStandardHedgingHandler(runtime));
        EnsureSingleStandardHandler(added);
        return added;
    }

    /// <summary>
    /// Standart işleyicilerde zaman sınırını yalnızca işleyicinin <c>TotalRequestTimeout</c>'u belirler; <c>HttpClient.Timeout</c>
    /// sonsuza çekilir (Microsoft <c>AddStandardResilienceHandler</c> ile aynı). Aksi halde HttpClient'ın varsayılan 100 sn'si
    /// daha uzun bir toplam zaman aşımını sessizce keser ve her istekte gereksiz bir bağlı iptal kaynağı + zamanlayıcı kurulur.
    /// </summary>
    private static void UseHandlerTimeoutOnly(IHttpClientBuilder builder) =>
        builder.ConfigureHttpClient(static client => client.Timeout = Timeout.InfiniteTimeSpan);

    /// <summary>
    /// Bir istemcide en fazla bir standart Aegis işleyicisi (standart ya da standart hedging) olabilir. İkincisi (ör. Aspire
    /// <c>ConfigureHttpClientDefaults</c> + istemciye özel ekleme) zincirleri iç içe koyar ve fiziksel çağrıyı sessizce katlar
    /// (4 deneme × 4 deneme = 16 çağrı; Microsoft'ta bilinen tuzak). İstemci oluşturulurken açık hatayla durdurulur. İşleyicinin
    /// eklenmesinden SONRA kaydedilir; araya <c>RemoveAllAegisHandlers()</c> girerse yalnızca yeni işleyici sayılır.
    /// </summary>
    private static void EnsureSingleStandardHandler(IHttpClientBuilder builder) =>
        builder.ConfigureAdditionalHttpMessageHandlers(static (handlers, _) =>
        {
            if (handlers.Count(static h => h is ReloadableResilienceHandler<StandardHandlerRuntime> or AegisStandardHedgingHandler) > 1)
            {
                throw new InvalidOperationException(
                    "Bu HttpClient'a birden çok standart Aegis dayanıklılık işleyicisi eklendi (ör. Aspire ConfigureHttpClientDefaults ile " +
                    "varsayılan + istemciye özel AddStandardAegisHandler / AddStandardAegisHedgingHandler). İç içe zincirler fiziksel çağrı " +
                    "sayısını katlar. İstemciye özel ayardan önce RemoveAllAegisHandlers() çağırın ya da işleyiciyi tek yerde ekleyin.");
            }
        });

    private static IHttpClientBuilder AddStandard(
        IHttpClientBuilder builder, IConfigurationSection? section, Action<AegisHttpStandardResilienceOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var name = builder.Name + "_AegisStandard";
        var runtime = CreateReloadable(section, () => new StandardHandlerRuntime(name, Load(section, configure)));
        UseHandlerTimeoutOnly(builder);
        builder.AddHttpMessageHandler(() => new ReloadableResilienceHandler<StandardHandlerRuntime>(runtime));
        EnsureSingleStandardHandler(builder);
        return builder;
    }

    private static IHttpClientBuilder AddHedging(
        IHttpClientBuilder builder, IConfigurationSection? section, Action<AegisHttpStandardHedgingOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var name = builder.Name + "_AegisHedging";
        var runtime = CreateReloadable(section, () => new HedgingHandlerRuntime(name, Load(section, configure)));
        UseHandlerTimeoutOnly(builder);
        builder.AddHttpMessageHandler(() => new AegisStandardHedgingHandler(runtime));
        EnsureSingleStandardHandler(builder);
        return builder;
    }

    // Seçenekler: önce yapılandırma bölümü (varsa), sonra koddan ayar. Bağlama somut tiple yapılır: yapılandırma bağlama
    // kaynak üreticisi (EnableConfigurationBindingGenerator) yalnızca somut tipleri yakalayabilir; böylece Native AOT'de
    // yansıma kullanılmaz. Generic tek metot bu nedenle bilinçli olarak ikiye ayrıldı.
    private static AegisHttpStandardResilienceOptions Load(IConfigurationSection? section, Action<AegisHttpStandardResilienceOptions>? configure)
    {
        var options = new AegisHttpStandardResilienceOptions();
        if (section is not null)
        {
            section.Bind(options); // kaynak üreticisi yalnızca doğrudan çağrıyı yakalar ("?." ile yakalamaz)
        }

        configure?.Invoke(options);
        return options;
    }

    private static AegisHttpStandardHedgingOptions Load(IConfigurationSection? section, Action<AegisHttpStandardHedgingOptions>? configure)
    {
        var options = new AegisHttpStandardHedgingOptions();
        if (section is not null)
        {
            section.Bind(options); // kaynak üreticisi yalnızca doğrudan çağrıyı yakalar ("?." ile yakalamaz)
        }

        configure?.Invoke(options);
        return options;
    }

    /// <summary>
    /// İlk nesli hemen kurar (geçersiz yapılandırma uygulama açılırken hata verir) ve bölüm verilmişse değişiklikte yeniden
    /// kurar. Nesil, işleyici yenilemeleri (HandlerLifetime) arasında paylaşılır: devre durumu korunur, kaynak sızmaz.
    /// </summary>
    private static Reloadable<TRuntime> CreateReloadable<TRuntime>(IConfigurationSection? section, Func<TRuntime> factory)
        where TRuntime : class, IDisposable
    {
        var reloadable = new Reloadable<TRuntime>(factory);
        if (section is not null)
        {
            reloadable.AddOwnedDisposable(ChangeToken.OnChange(section.GetReloadToken, () => reloadable.Reload()));
        }

        return reloadable;
    }
}
