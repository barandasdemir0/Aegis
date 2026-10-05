using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Extensions.DependencyInjection;

namespace Aegis.Resilience.Extensions.Http;

public static class HttpClientBuilderExtensions
{
    /// <summary>
    /// HttpClient üzerine daha önce IServiceCollection ile kaydedilmiş adlandırılmış bir Aegis boru hattını ekler.
    /// </summary>
    public static IHttpClientBuilder AddAegisResilienceHandler(
        this IHttpClientBuilder builder,
        string pipelineName,
        bool handleHttpFailureStatuses = true,
        bool allowNonIdempotentRetry = false)
    {
        ArgumentNullException.ThrowIfNull(pipelineName);

        builder.AddHttpMessageHandler(sp =>
        {
            var registry = sp.GetRequiredService<IAegisPipelineRegistry>();
            var pipeline = registry.GetPipeline(pipelineName);
            return new AegisResilienceHandler(pipeline, handleHttpFailureStatuses, allowNonIdempotentRetry);
        });

        return builder;
    }

    /// <summary>
    /// HttpClient üzerine inline olarak yapılandırılmış yeni bir Aegis boru hattı ekler.
    /// <para>
    /// Boru hattı yalnızca BİR KEZ kurulur ve tüm işleyici (handler) yenilemeleri arasında paylaşılır.
    /// Aksi halde <c>HandlerLifetime</c> (varsayılan 2 dakika) dolduğunda Circuit Breaker sayaçları sıfırlanır
    /// ve zamanlayıcı/semafor gibi kaynaklar sızardı (AEGIS-116).
    /// </para>
    /// </summary>
    public static IHttpClientBuilder AddAegisResilienceHandler(
        this IHttpClientBuilder builder,
        Action<IAegisPipelineBuilder> configure,
        bool handleHttpFailureStatuses = true,
        bool allowNonIdempotentRetry = false)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var sharedPipeline = new Lazy<IAegisPipeline>(
            () =>
            {
                var pipelineBuilder = new AegisPipelineBuilder(builder.Name + "_AegisPipeline");
                configure(pipelineBuilder);
                return pipelineBuilder.Build();
            },
            LazyThreadSafetyMode.ExecutionAndPublication);

        builder.AddHttpMessageHandler(_ =>
            new AegisResilienceHandler(sharedPipeline.Value, handleHttpFailureStatuses, allowNonIdempotentRetry));

        return builder;
    }

    /// <summary>
    /// DI bağlamıyla kurulan özel boru hattı ekler (Microsoft: <c>AddResilienceHandler(name, (builder, context) =&gt; ...)</c>).
    /// Bağlam; servis sağlayıcıyı, seçenekleri (<c>GetOptions</c>), seçenek değişince yeniden kurmayı (<c>EnableReloads</c>),
    /// dispose bildirimini (<c>OnPipelineDisposed</c>) ve HTTP kurallarını (<c>DisableRetryFor</c>, <c>AllowNonIdempotentRetry</c>)
    /// sunar. DI telemetrisi (otomatik <c>ILogger</c> günlüğü) uygulanır.
    /// <para>
    /// Boru hattı servis sağlayıcı başına bir kez kurulur, işleyici yenilemeleri (HandlerLifetime) arasında paylaşılır
    /// (devre durumu korunur) ve sağlayıcı dispose edilince dispose edilir.
    /// </para>
    /// </summary>
    public static IHttpClientBuilder AddAegisResilienceHandler(
        this IHttpClientBuilder builder,
        Action<IAegisPipelineBuilder, AegisHttpHandlerContext> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var clientName = builder.Name;
        return AddPerServiceProvider(builder,
            serviceProvider => CreateReloadable(reload =>
                ContextualHandlerRuntime.Build(serviceProvider, clientName, instanceName: null, configure, reload)),
            runtime => new ReloadableResilienceHandler<ContextualHandlerRuntime>(runtime));
    }

    /// <summary>
    /// İstek anahtarı başına ayrı DI bağlamlı boru hattı ekler (Microsoft: <c>AddResilienceHandler(...).SelectPipelineBy(...)</c>).
    /// Her anahtarın devresi, kotası ve sınırı ayrıdır; ör. bir host çökünce diğer hostlara giden istekler etkilenmez.
    /// Yapılandırma her anahtar için kendi bağlamıyla çağrılır (<see cref="AegisHttpHandlerContext.InstanceName"/> = anahtar).
    /// Hedef authority için <c>selectPipelineBy: AegisHttpPipelineSelectors.ByAuthority</c>.
    /// </summary>
    /// <param name="builder">HttpClient kurucu.</param>
    /// <param name="configure">Anahtar başına boru hattı yapılandırması.</param>
    /// <param name="selectPipelineBy">İsteği boru hattı anahtarına eşler.</param>
    /// <param name="maxPipelines">Kurulabilecek en fazla anahtar (kardinalite koruması; varsayılan 1000).</param>
    public static IHttpClientBuilder AddAegisResilienceHandler(
        this IHttpClientBuilder builder,
        Action<IAegisPipelineBuilder, AegisHttpHandlerContext> configure,
        Func<HttpRequestMessage, string> selectPipelineBy,
        int maxPipelines = 1000)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(selectPipelineBy);
        AegisOptionsValidator.AtLeast(maxPipelines, 1, nameof(AddAegisResilienceHandler), nameof(maxPipelines));

        var clientName = builder.Name;
        return AddPerServiceProvider(builder,
            serviceProvider => CreateReloadable(reload => new KeyedContextualHandlerRuntime(
                key => ContextualHandlerRuntime.Build(serviceProvider, clientName, key, configure, reload), selectPipelineBy, maxPipelines)),
            runtime => new ReloadableResilienceHandler<KeyedContextualHandlerRuntime>(runtime));
    }

    // Nesil, bağlamın EnableReloads aboneliklerinin tetikleyeceği yeniden kurma eylemini alır.
    private static Reloadable<TRuntime> CreateReloadable<TRuntime>(Func<Action, TRuntime> build)
        where TRuntime : class, IDisposable
    {
        Reloadable<TRuntime>? runtime = null;
        runtime = new Reloadable<TRuntime>(() => build(() => runtime?.Reload()));
        return runtime;
    }

    /// <summary>
    /// İşleyici neslini servis sağlayıcı başına tekil kurar (anahtarlı singleton): işleyici yenilemeleri (HandlerLifetime)
    /// arasında paylaşılır (devre durumu korunur) ve sağlayıcıyla birlikte dispose edilir. Kayıt başına ayrı anahtar
    /// kullanılır: aynı istemciye birden çok işleyici eklenebilir.
    /// </summary>
    internal static IHttpClientBuilder AddPerServiceProvider<TRuntime>(
        IHttpClientBuilder builder, Func<IServiceProvider, Reloadable<TRuntime>> createRuntime, Func<Reloadable<TRuntime>, DelegatingHandler> createHandler)
        where TRuntime : class, IDisposable
    {
        var key = new object();
        builder.Services.AddKeyedSingleton(key, (serviceProvider, _) => createRuntime(serviceProvider));
        builder.AddHttpMessageHandler(serviceProvider => createHandler(serviceProvider.GetRequiredKeyedService<Reloadable<TRuntime>>(key)));
        return builder;
    }

    /// <summary>
    /// Bu çağrıdan ÖNCE eklenmiş tüm Aegis işleyicilerini istemciden kaldırır (Microsoft: <c>RemoveAllResilienceHandlers</c>).
    /// Tipik kullanım: <c>services.ConfigureHttpClientDefaults(b =&gt; b.AddStandardAegisHandler(...))</c> ile tüm istemcilere
    /// eklenen işleyiciyi tek bir istemciden çıkarmak ya da ona farklı bir işleyici vermek. Aegis dışındaki işleyicilere
    /// dokunulmaz; bu çağrıdan sonra eklenen Aegis işleyicileri korunur.
    /// </summary>
    public static IHttpClientBuilder RemoveAllAegisHandlers(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureAdditionalHttpMessageHandlers(static (handlers, _) =>
        {
            for (var i = handlers.Count - 1; i >= 0; i--)
            {
                if (handlers[i] is AegisDelegatingHandler)
                {
                    handlers.RemoveAt(i);
                }
            }
        });

        return builder;
    }

    /// <summary>
    /// HttpClient üzerine mikroservis dünyasında kanıtlanmış 5 standart stratejiyi (Total Timeout, Concurrency, Retry+Jitter, Circuit Breaker, Attempt Timeout) ekler.
    /// </summary>
    public static IHttpClientBuilder AddStandardAegisHandler(
        this IHttpClientBuilder builder,
        TimeSpan? totalTimeout = null,
        int maxConcurrency = 100,
        int retryAttempts = 3,
        TimeSpan? attemptTimeout = null,
        bool handleHttpFailureStatuses = true,
        bool allowNonIdempotentRetry = false)
    {
        return builder.AddAegisResilienceHandler(pBuilder =>
        {
            pBuilder.AddStandardResilience(
                totalTimeout ?? TimeSpan.FromSeconds(30),
                maxConcurrency,
                retryAttempts,
                attemptTimeout ?? TimeSpan.FromSeconds(5));
        }, handleHttpFailureStatuses, allowNonIdempotentRetry);
    }

    /// <summary>
    /// İstek atılan HTTP mesajını inceleyerek (Host, URI, Header) IAegisPipelineRegistry üzerinden
    /// boru hattını dinamik olarak seçen işleyiciyi ekler.
    /// </summary>
    public static IHttpClientBuilder AddAegisDynamicHandler(
        this IHttpClientBuilder builder,
        Func<HttpRequestMessage, string> pipelineSelector,
        bool handleHttpFailureStatuses = true,
        bool allowNonIdempotentRetry = false)
    {
        ArgumentNullException.ThrowIfNull(pipelineSelector);

        builder.AddHttpMessageHandler(sp =>
        {
            var registry = sp.GetRequiredService<IAegisPipelineRegistry>();
            return new AegisDynamicResilienceHandler(registry, pipelineSelector, handleHttpFailureStatuses, allowNonIdempotentRetry);
        });

        return builder;
    }

    /// <summary>
    /// İstek atılan hedef servisin Host/Authority adresine (Örn: "api.com") göre
    /// IAegisPipelineRegistry içerisindeki boru hattını otomatik seçen dinamik işleyiciyi ekler.
    /// </summary>
    public static IHttpClientBuilder AddAegisHandlerByHost(
        this IHttpClientBuilder builder,
        string fallbackPipelineName = "Default",
        bool handleHttpFailureStatuses = true)
    {
        return builder.AddAegisDynamicHandler(req =>
        {
            return req.RequestUri?.Host ?? fallbackPipelineName;
        }, handleHttpFailureStatuses);
    }

    /// <summary>
    /// Çoklu uç nokta / çoklu bölge arasında gecikmeyi önlemek için paralel spekülatif istekler atan MultiEndpointHedgingHandler işleyicisini ekler.
    /// </summary>
    public static IHttpClientBuilder AddMultiEndpointHedgingHandler(
        this IHttpClientBuilder builder,
        Action<MultiEndpointHedgingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new MultiEndpointHedgingOptions();
        configure(options);

        builder.AddHttpMessageHandler(() => new MultiEndpointHedgingHandler(options));
        return builder;
    }

    /// <summary>
    /// Gelen HTTP isteklerini belirlenen ağırlıklara göre farklı uç noktalara dağıtan WeightedCanaryHandler işleyicisini ekler.
    /// </summary>
    public static IHttpClientBuilder AddWeightedCanaryHandler(
        this IHttpClientBuilder builder,
        Action<WeightedCanaryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new WeightedCanaryOptions();
        configure(options);

        builder.AddHttpMessageHandler(() => new WeightedCanaryHandler(options));
        return builder;
    }

    /// <summary>
    /// HTTP istek gövdesini (HttpContent) belleğe tamponlayarak Retry ve Hedging akışlarında
    /// gövdenin güvenle tekrar oynatılabilmesini sağlayan HttpRequestReplayHandler işleyicisini ekler.
    /// </summary>
    public static IHttpClientBuilder AddHttpRequestReplayHandler(
        this IHttpClientBuilder builder,
        long maxRequestBodySize = HttpRequestReplayHandler.DefaultMaxRequestBodySize)
    {
        builder.AddHttpMessageHandler(() => new HttpRequestReplayHandler(maxRequestBodySize));
        return builder;
    }
}
