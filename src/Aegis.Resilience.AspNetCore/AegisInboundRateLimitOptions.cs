using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Distributed.Abstractions;

namespace Aegis.Resilience.AspNetCore;

/// <summary>
/// Gelen istek hız sınırlama seçenekleri (AspNetCoreRateLimit / WebApiThrottle eşdeğeri). Sayaçlar
/// <see cref="IDistributedRateLimitStore"/>'dadır: DI'da kayıtlı değilse bellek içi (tek sunucu), Redis deposu kayıtlıysa
/// (<c>AddAegisRedisRateLimitStore</c>) küme genelinde ortak kota. <c>appsettings.json</c>'dan bağlanabilir.
/// </summary>
public sealed class AegisInboundRateLimitOptions : InboundRateLimitOptionsBase
{
    /// <summary>Kurallar (sırayla değerlendirilir).</summary>
    public List<AegisInboundRateLimitRule> Rules { get; set; } = [];

    /// <summary>Red durum kodu (varsayılan 429 Too Many Requests).</summary>
    public int RejectionStatusCode { get; set; } = StatusCodes.Status429TooManyRequests;

    /// <summary>
    /// İsteği bölüme eşler (varsayılan: istemci IP'si). Null dönerse istek <c>"anonim"</c> bölümüne düşer. Hazır seçiciler:
    /// <see cref="PartitionByClientIp"/>, <see cref="PartitionByHeader"/>, <see cref="PartitionByUser"/>.
    /// </summary>
    public Func<HttpContext, string?> PartitionKeySelector { get; set; } = ClientIp;

    /// <summary>
    /// Özel red yanıtı (durum kodu ve başlıklar yazıldıktan sonra çağrılır). Null ise gövdesiz yanıt.
    /// </summary>
    public Func<HttpContext, AegisInboundRateLimitRejection, Task>? OnRejected { get; set; }

    /// <summary>İstemci IP'sine göre bölümle (proxy arkasında <c>UseForwardedHeaders</c> ile birlikte kullanın).</summary>
    public AegisInboundRateLimitOptions PartitionByClientIp()
    {
        PartitionKeySelector = ClientIp;
        return this;
    }

    /// <summary>Başlığa göre bölümle (ör. <c>"X-ClientId"</c>, API anahtarı); başlık yoksa IP'ye düşer.</summary>
    public AegisInboundRateLimitOptions PartitionByHeader(string headerName)
    {
        ArgumentException.ThrowIfNullOrEmpty(headerName);
        PartitionKeySelector = context => context.Request.Headers.TryGetValue(headerName, out var value) && value.Count > 0
            ? value.ToString()
            : ClientIp(context);
        return this;
    }

    /// <summary>Kimliği doğrulanmış kullanıcıya göre bölümle (<see cref="ClaimTypes.NameIdentifier"/>); anonimde IP'ye düşer.</summary>
    public AegisInboundRateLimitOptions PartitionByUser()
    {
        PartitionKeySelector = context => context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? ClientIp(context);
        return this;
    }

    /// <summary>Kural ekler (akıcı kullanım).</summary>
    public AegisInboundRateLimitOptions AddRule(string endpoint, int limit, TimeSpan period,
        DistributedRateLimitAlgorithm algorithm = DistributedRateLimitAlgorithm.FixedWindow)
    {
        Rules.Add(new AegisInboundRateLimitRule { Endpoint = endpoint, Limit = limit, Period = period, Algorithm = algorithm });
        return this;
    }

    /// <summary>Kuralları, desenleri ve ağ tanımlarını doğrular (fail-fast).</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(PartitionKeySelector);
        AegisOptionsValidator.InRange(RejectionStatusCode, 400, 599, nameof(AegisInboundRateLimitOptions));
        ValidateRules(Rules, nameof(AegisInboundRateLimitOptions));
    }

    private static string? ClientIp(HttpContext context) => context.Connection.RemoteIpAddress?.ToString();
}
