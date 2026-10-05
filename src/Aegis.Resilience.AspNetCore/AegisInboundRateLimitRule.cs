using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Distributed.Abstractions;

namespace Aegis.Resilience.AspNetCore;

/// <summary>
/// Gelen istek hız sınırlama kuralı (AspNetCoreRateLimit: <c>RateLimitRule</c>). Alanları ortak tabandadır
/// (<see cref="InboundRateLimitRule"/>); bu tür, mevcut API ve yapılandırma bağlaması için korunur.
/// </summary>
public sealed class AegisInboundRateLimitRule : InboundRateLimitRule;
