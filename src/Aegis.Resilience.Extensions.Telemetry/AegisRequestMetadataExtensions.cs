using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.ExceptionSummarization;
using Microsoft.Extensions.Http.Diagnostics;
using Aegis.Resilience.Core.Context;
using Aegis.Resilience.Core.Telemetry;
using Aegis.Resilience.Extensions.DependencyInjection;

namespace Aegis.Resilience.Extensions.Telemetry;

/// <summary>Bağlama <see cref="RequestMetadata"/> iliştirme (Microsoft: <c>ResilienceContext.SetRequestMetadata</c>).</summary>
public static class AegisRequestMetadataExtensions
{
    private static readonly AegisPropertyKey<RequestMetadata> Key = new("Aegis.RequestMetadata");

    /// <summary>Çağrının işlem ve bağımlılık adını bağlama yazar (metriklerde <c>request.name</c> / <c>request.dependency.name</c>).</summary>
    public static void SetRequestMetadata(this AegisContext context, RequestMetadata requestMetadata)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requestMetadata);
        context.SetProperty(Key, requestMetadata);
    }

    /// <summary>
    /// Bağlamdaki <see cref="RequestMetadata"/>; yoksa bağlama iliştirilmiş HTTP isteğindeki (<c>request.SetRequestMetadata(...)</c>)
    /// üst veri; o da yoksa null.
    /// </summary>
    public static RequestMetadata? GetRequestMetadata(this AegisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TryGetProperty(Key, out var metadata))
        {
            return metadata;
        }

        return context.TryGetProperty<HttpRequestMessage>(AegisContextKeys.HttpRequest, out var request) && request is not null
            ? request.GetRequestMetadata()
            : null;
    }
}
