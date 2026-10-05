using System.Globalization;
using System.Net.Http;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// HTTP 429 (Too Many Requests) ve 503 (Service Unavailable) yanıtlarında sunucu tarafından iletilen
/// 'Retry-After' başlığını (delta-saniye veya RFC 1123 HTTP-Date) güvenle ayrıştıran yardımcı sınıf (Microsoft.Extensions.Http.Resilience paritesi).
/// </summary>
public static class HttpRetryAfterHelper
{
    public const string RetryAfterPropertyKey = Aegis.Resilience.Core.Context.AegisContextKeys.RetryAfterDelay;

    /// <summary>
    /// HttpResponseMessage başlıklarından Retry-After süresini ayrıştırmaya çalışır.
    /// </summary>
    public static bool TryParse(HttpResponseMessage? response, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (response?.Headers.RetryAfter == null)
        {
            return false;
        }

        var retryAfter = response.Headers.RetryAfter;

        // 1. Delta-Seconds (örn: Retry-After: 30)
        if (retryAfter.Delta.HasValue)
        {
            delay = retryAfter.Delta.Value > TimeSpan.Zero ? retryAfter.Delta.Value : TimeSpan.Zero;
            return true;
        }

        // 2. HTTP-Date (örn: Retry-After: Fri, 31 Dec 2026 23:59:59 GMT)
        if (retryAfter.Date.HasValue)
        {
            var diff = retryAfter.Date.Value - DateTimeOffset.UtcNow;
            delay = diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Raw Retry-After dizesinden (saniye veya HTTP-Date) ayrıştırma yapar.
    /// </summary>
    public static bool TryParseRaw(string? rawValue, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        // 1. Tamsayı saniye kontrolü
        if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
        {
            delay = TimeSpan.FromSeconds(seconds);
            return true;
        }

        // 2. Tarih kontrolü (RFC 1123 / IMF-fixdate)
        if (DateTimeOffset.TryParse(rawValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var targetDate))
        {
            var diff = targetDate - DateTimeOffset.UtcNow;
            delay = diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
            return true;
        }

        return false;
    }
}
