using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Web;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Distributed.Abstractions;

namespace Aegis.Resilience.WebApi;

/// <summary>
/// Web API 2 gelen istek hız sınırlama seçenekleri (WebApiThrottle eşdeğeri; ASP.NET Core karşılığı
/// <c>AegisInboundRateLimitOptions</c>). Sayaçlar <see cref="IDistributedRateLimitStore"/>'dadır: verilmezse bellek içi
/// (tek sunucu), Redis deposu verilirse küme genelinde ortak kota.
/// </summary>
public sealed class AegisWebApiRateLimitOptions : InboundRateLimitOptionsBase
{
    /// <summary>Kurallar (sırayla değerlendirilir).</summary>
    public List<AegisWebApiRateLimitRule> Rules { get; set; } = [];

    /// <summary>Red durum kodu (varsayılan 429 Too Many Requests).</summary>
    public HttpStatusCode RejectionStatusCode { get; set; } = (HttpStatusCode)429;

    /// <summary>
    /// İsteği bölüme eşler (varsayılan: istemci IP'si). Null dönerse istek <c>"anonim"</c> bölümüne düşer. Hazır seçiciler:
    /// <see cref="PartitionByClientIp"/>, <see cref="PartitionByHeader"/>, <see cref="PartitionByUser"/>.
    /// </summary>
    public Func<HttpRequestMessage, string?> PartitionKeySelector { get; set; } = ClientIp;

    /// <summary>Özel red yanıtı (durum kodu ve başlıklar yazıldıktan sonra çağrılır; ör. gövde eklemek). Null ise gövdesiz yanıt.</summary>
    public Action<HttpRequestMessage, AegisWebApiRateLimitRejection, HttpResponseMessage>? OnRejected { get; set; }

    /// <summary>İstemci IP'sine göre bölümle (IIS / OWIN barındırma).</summary>
    public AegisWebApiRateLimitOptions PartitionByClientIp()
    {
        PartitionKeySelector = ClientIp;
        return this;
    }

    /// <summary>Başlığa göre bölümle (ör. <c>"X-ClientId"</c>, API anahtarı); başlık yoksa IP'ye düşer.</summary>
    public AegisWebApiRateLimitOptions PartitionByHeader(string headerName)
    {
        ArgumentException.ThrowIfNullOrEmpty(headerName);
        PartitionKeySelector = request => request.Headers.TryGetValues(headerName, out var values) && values.FirstOrDefault() is { Length: > 0 } value
            ? value
            : ClientIp(request);
        return this;
    }

    /// <summary>Kimliği doğrulanmış kullanıcıya göre bölümle (<see cref="ClaimTypes.NameIdentifier"/>); anonimde IP'ye düşer.</summary>
    public AegisWebApiRateLimitOptions PartitionByUser()
    {
        PartitionKeySelector = request =>
            (request.GetRequestContext()?.Principal as ClaimsPrincipal)?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? ClientIp(request);
        return this;
    }

    /// <summary>Kural ekler (akıcı kullanım).</summary>
    public AegisWebApiRateLimitOptions AddRule(string endpoint, int limit, TimeSpan period,
        DistributedRateLimitAlgorithm algorithm = DistributedRateLimitAlgorithm.FixedWindow)
    {
        Rules.Add(new AegisWebApiRateLimitRule { Endpoint = endpoint, Limit = limit, Period = period, Algorithm = algorithm });
        return this;
    }

    /// <summary>Kuralları, desenleri ve ağ tanımlarını doğrular (fail-fast).</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(PartitionKeySelector);
        AegisOptionsValidator.InRange((int)RejectionStatusCode, 400, 599, nameof(AegisWebApiRateLimitOptions));
        ValidateRules(Rules, nameof(AegisWebApiRateLimitOptions));
    }

    /// <summary>
    /// İstemci IP'si: IIS barındırmada <c>MS_HttpContext</c>, OWIN barındırmada <c>server.RemoteIpAddress</c>.
    /// Proxy arkasında gerçek IP için <see cref="PartitionKeySelector"/>'ı <c>X-Forwarded-For</c>'a göre özelleştirin.
    /// </summary>
    internal static string? ClientIp(HttpRequestMessage request)
    {
        var properties = request.Properties;
        if (properties.TryGetValue("MS_HttpContext", out var httpContext) && httpContext is HttpContextBase web)
        {
            return web.Request.UserHostAddress;
        }

        return properties.TryGetValue("MS_OwinEnvironment", out var owin) && owin is IDictionary<string, object> environment &&
               environment.TryGetValue("server.RemoteIpAddress", out var address)
            ? address as string
            : null;
    }
}
