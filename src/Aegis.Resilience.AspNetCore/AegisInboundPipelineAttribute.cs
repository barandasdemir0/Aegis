using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Aegis.Resilience.Core.Abstractions;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Exceptions;
using Aegis.Resilience.Core.Pipeline;
using Aegis.Resilience.Core.Strategies.Hedging;
using Aegis.Resilience.Core.Strategies.Retry;
using Aegis.Resilience.Extensions.DependencyInjection;

namespace Aegis.Resilience.AspNetCore;

/// <summary>
/// Uç noktayı adlandırılmış bir Aegis boru hattından geçirir (gelen istek için eşzamanlılık sınırı, zaman aşımı, devre
/// kesici, hız sınırı). Denetleyici veya eyleme <c>[AegisInboundPipeline("ad")]</c>; minimal API'de
/// <c>.RequireAegisPipeline("ad")</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AegisInboundPipelineAttribute(string pipelineName) : Attribute
{
    /// <summary>DI'da kayıtlı boru hattının adı.</summary>
    public string PipelineName { get; } = pipelineName ?? throw new ArgumentNullException(nameof(pipelineName));
}
