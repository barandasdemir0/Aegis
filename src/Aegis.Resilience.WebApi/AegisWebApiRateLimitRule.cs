using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Web;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Distributed.Abstractions;

namespace Aegis.Resilience.WebApi;

/// <summary>
/// Gelen istek hız sınırlama kuralı (WebApiThrottle: <c>EndpointRateLimits</c> / <c>RateLimits</c>). Alanları ortak tabandadır
/// (<see cref="InboundRateLimitRule"/>); bu tür, mevcut API için korunur.
/// </summary>
public sealed class AegisWebApiRateLimitRule : InboundRateLimitRule;
