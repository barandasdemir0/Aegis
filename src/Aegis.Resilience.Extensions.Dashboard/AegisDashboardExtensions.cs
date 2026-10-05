using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Strategies.CircuitBreaker;
using Aegis.Resilience.Extensions.DependencyInjection;

namespace Aegis.Resilience.Extensions.Dashboard;

/// <summary>
/// Aegis panosu ve durum uç noktaları. Uç noktalar <see cref="RequestDelegate"/> ile ve kaynak üretilen JSON bağlamıyla
/// yazılır: Native AOT ve trimming ile uyumludur (yansıma yok).
/// </summary>
public static class AegisDashboardExtensions
{
    private const string ActionHeader = "X-Aegis-Action";

    /// <summary>
    /// Sistemdeki tüm Aegis boru hatlarının ve devre kesicilerinin durumunu JSON olarak dönen endpoint'i haritalar.
    /// </summary>
    public static IEndpointConventionBuilder MapAegisStatus(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/aegis/status",
        string? authorizationPolicy = null)
    {
        var route = endpoints.MapGet(pattern, static context =>
        {
            var registry = context.RequestServices.GetRequiredService<IAegisPipelineRegistry>();
            return context.Response.WriteAsJsonAsync(BuildStatus(registry), DashboardJsonContext.Default.DashboardStatus);
        });

        return RequirePolicy(route, authorizationPolicy);
    }

    /// <summary>
    /// Canlı izlenebilen ve tarayıcıdan devre kesiciye manuel müdahale (Force Open / Reset) imkanı sunan
    /// sıfır bağımlılıklı karanlık mod Aegis Web Dashboard arayüzünü haritalar.
    /// </summary>
    public static IEndpointConventionBuilder MapAegisDashboard(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/aegis",
        string? authorizationPolicy = null,
        string? actionAuthorizationPolicy = null)
    {
        var basePath = pattern.TrimEnd('/');

        // 1. JSON durum uç noktası
        endpoints.MapAegisStatus(basePath + "/status", authorizationPolicy);

        // 2. Canlı müdahale (CSRF başlığı + ayrı yetki politikası). Yerel ve dağıtık devre kesicide çalışır.
        // Politika yoksa müdahale yalnızca yerel makineden kabul edilir (güvenli varsayılan; Hangfire panosu gibi): aksi halde ağa
        // erişen herkes üretimde devreleri açıp servisi durdurabilirdi.
        var effectiveActionPolicy = actionAuthorizationPolicy ?? authorizationPolicy;
        var localOnly = string.IsNullOrEmpty(effectiveActionPolicy);
        RequirePolicy(endpoints.MapPost(basePath + "/circuits/{name}/isolate", context => HandleCircuitActionAsync(context, isolate: true, localOnly)),
            effectiveActionPolicy);
        RequirePolicy(endpoints.MapPost(basePath + "/circuits/{name}/reset", context => HandleCircuitActionAsync(context, isolate: false, localOnly)),
            effectiveActionPolicy);

        // 3. HTML arayüz
        var page = endpoints.MapGet(pattern, static context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            return context.Response.WriteAsync(DashboardPage.Html);
        });

        return RequirePolicy(page, authorizationPolicy);
    }

    private static TBuilder RequirePolicy<TBuilder>(TBuilder builder, string? policy)
        where TBuilder : IEndpointConventionBuilder
    {
        if (!string.IsNullOrEmpty(policy))
        {
            builder.RequireAuthorization(policy);
        }

        return builder;
    }

    private static DashboardStatus BuildStatus(IAegisPipelineRegistry registry)
    {
        var pipelines = registry.GetAllPipelines();
        var result = pipelines
            .Select(p =>
            {
                var strategies = p.Value.Strategies
                    .Select(s => new StrategyStatus(s.Name, (s as IObservableCircuitState)?.LastKnownState.ToString()))
                    .ToList();
                var hasCircuitBreaker = strategies.Any(s => s.CircuitState is not null);
                var isAnyOpen = strategies.Any(s => s.CircuitState is nameof(CircuitState.Open) or nameof(CircuitState.Isolated));
                return new PipelineStatus(p.Key, hasCircuitBreaker, isAnyOpen ? "Degraded" : "Healthy", strategies);
            })
            .ToList();

        return new DashboardStatus(DateTimeOffset.UtcNow, pipelines.Count, result);
    }

    /// <summary>Isolate / Reset: tek işleyici (DRY). CSRF koruması: özel başlık zorunlu (AEGIS-107).</summary>
    private static async Task HandleCircuitActionAsync(HttpContext context, bool isolate, bool localOnly)
    {
        if (localOnly && !IsLocal(context))
        {
            await Results.Problem("Devre müdahalesi yetki politikası olmadan yalnızca yerel makineden yapılabilir (MapAegisDashboard: actionAuthorizationPolicy).",
                statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (!context.Request.Headers.TryGetValue(ActionHeader, out var header) || header != "true")
        {
            await Results.Problem($"Geçersiz veya eksik '{ActionHeader}' başlığı. CSRF koruması devrede.", statusCode: StatusCodes.Status403Forbidden)
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var name = context.Request.RouteValues["name"] as string ?? string.Empty;
        var registry = context.RequestServices.GetRequiredService<IAegisPipelineRegistry>();
        var circuits = registry.TryGetPipeline(name, out var pipeline) && pipeline is not null
            ? pipeline.Strategies.OfType<IManuallyControllableCircuit>().ToList()
            : [];

        if (circuits.Count == 0)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(
                new ActionResult(false, $"'{name}' boru hattında devre kesici bulunamadı."), DashboardJsonContext.Default.ActionResult).ConfigureAwait(false);
            return;
        }

        foreach (var circuit in circuits)
        {
            if (isolate)
            {
                await circuit.IsolateCircuitAsync(context.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                await circuit.CloseCircuitAsync(context.RequestAborted).ConfigureAwait(false);
            }
        }

        var message = isolate
            ? $"'{name}' devresi manuel olarak AÇILDI (Isolate edildi)."
            : $"'{name}' devresi başarıyla SIFIRLANDI (Closed durumuna getirildi).";
        await context.Response.WriteAsJsonAsync(new ActionResult(true, message), DashboardJsonContext.Default.ActionResult).ConfigureAwait(false);
    }

    // Uzak adres yoksa istek süreç içidir (TestServer); aksi halde geri döngü ya da sunucunun kendi adresi yerel sayılır.
    private static bool IsLocal(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        return remote is null || System.Net.IPAddress.IsLoopback(remote) || remote.Equals(context.Connection.LocalIpAddress);
    }
}
