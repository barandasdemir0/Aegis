using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Extensions.DependencyInjection;

namespace Aegis.Resilience.Extensions.Dashboard;

/// <summary>Kaynak üretilen JSON bağlamı (AOT için yansımasız serileştirme; alan adları camelCase — arayüz bunları okur).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DashboardStatus))]
[JsonSerializable(typeof(ActionResult))]
internal sealed partial class DashboardJsonContext : JsonSerializerContext;
