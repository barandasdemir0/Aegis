using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Aegis.Resilience.Core.Telemetry;
using Aegis.Resilience.Distributed.Abstractions;

namespace Aegis.Resilience.AspNetCore;

/// <summary>
/// Gelen istek hız sınırlama ara katmanı. Kural değerlendirmesi ortak motordadır (<see cref="InboundRateLimitEngine{TRule}"/>);
/// bu sınıf yalnızca ASP.NET Core isteğini motora uyarlar ve yanıtı yazar. Biri reddederse uç nokta çalışmaz,
/// <see cref="AegisInboundRateLimitOptions.RejectionStatusCode"/> ve <c>Retry-After</c> döner.
/// </summary>
internal sealed partial class AegisInboundRateLimitingMiddleware
{
    private const string AnonymousPartition = "anonim";

    private readonly RequestDelegate _next;
    private readonly IDistributedRateLimitStore _store;
    private readonly ILogger _logger;
    private volatile Plan _plan;

    public AegisInboundRateLimitingMiddleware(
        RequestDelegate next,
        IOptionsMonitor<AegisInboundRateLimitOptions> options,
        IDistributedRateLimitStore store,
        ILogger<AegisInboundRateLimitingMiddleware> logger)
    {
        _next = next;
        _store = store;
        _logger = logger;
        _plan = Plan.Compile(options.CurrentValue); // geçersiz yapılandırma uygulama açılırken bildirilir
        _ = options.OnChange(Reload); // ara katman uygulama ömrü boyunca yaşar; abonelik de öyle
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var plan = _plan;
        var request = context.Request;
        var path = request.Path.Value ?? "/";
        var partition = plan.Options.PartitionKeySelector(context) ?? AnonymousPartition;
        if (!plan.Engine.HasRules || plan.Engine.IsExempt(request.Method, path, partition, context.Connection.RemoteIpAddress))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var decision = await plan.Engine.AcquireAsync(_store, request.Method, path, partition, context.RequestAborted).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            await RejectAsync(context, plan.Options, decision.Rule!, partition, decision.RetryAfter).ConfigureAwait(false);
            return;
        }

        if (plan.Options.EmitRateLimitHeaders && decision.Rule is { } tightest)
        {
            WriteLimitHeaders(context.Response, tightest.Limit, decision.Remaining);
        }

        await _next(context).ConfigureAwait(false);
    }

    private static async Task RejectAsync(
        HttpContext context, AegisInboundRateLimitOptions options, AegisInboundRateLimitRule rule, string partition, TimeSpan? retryAfter)
    {
        AegisTelemetry.RateLimitRejectionsTotal.Add(1, new KeyValuePair<string, object?>("pipeline", "inbound"));

        var response = context.Response;
        InboundRejection.Write(context, options.RejectionStatusCode, InboundRejection.GrpcResourceExhausted,
            $"Hız sınırı aşıldı ({rule.Endpoint}: {rule.Limit} / {rule.Period}).", options.EmitRateLimitHeaders ? retryAfter : null);
        if (options.EmitRateLimitHeaders)
        {
            WriteLimitHeaders(response, rule.Limit, remaining: 0);
        }

        if (options.OnRejected is { } onRejected)
        {
            await onRejected(context, new AegisInboundRateLimitRejection(rule, partition, retryAfter)).ConfigureAwait(false);
        }
    }

    // IETF "RateLimit header fields for HTTP" taslağındaki adlar.
    private static void WriteLimitHeaders(HttpResponse response, int limit, int remaining)
    {
        response.Headers["RateLimit-Limit"] = limit.ToString(CultureInfo.InvariantCulture);
        response.Headers["RateLimit-Remaining"] = Math.Max(0, remaining).ToString(CultureInfo.InvariantCulture);
    }

    private void Reload(AegisInboundRateLimitOptions options)
    {
        try
        {
            _plan = Plan.Compile(options);
        }
#pragma warning disable CA1031 // Geçersiz yeni yapılandırma yüklenmez; eski kurallar çalışmaya devam eder.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogInvalidReload(_logger, ex);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Aegis gelen istek hız sınırı yapılandırması geçersiz; önceki kurallar kullanılmaya devam ediyor.")]
    private static partial void LogInvalidReload(ILogger logger, Exception exception);

    /// <summary>Seçeneklerin doğrulanmış ve derlenmiş hali (yeniden yüklemede tek seferde değiştirilir).</summary>
    private sealed class Plan(AegisInboundRateLimitOptions options)
    {
        public AegisInboundRateLimitOptions Options { get; } = options;

        public InboundRateLimitEngine<AegisInboundRateLimitRule> Engine { get; } = new(options.Rules, options);

        public static Plan Compile(AegisInboundRateLimitOptions options)
        {
            options.Validate();
            return new Plan(options);
        }
    }
}
