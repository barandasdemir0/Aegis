using System.Text.RegularExpressions;

namespace Aegis.Resilience.Distributed.Abstractions;

/// <summary>
/// <c>"[YÖNTEM:]yol"</c> biçimli uç nokta deseni (<c>*</c> joker). Açılışta bir kez derlenir; eşleştirme yöntemde sıralı
/// karşılaştırma, yolda önceden derlenmiş düzenli ifadedir (büyük/küçük harf duyarsız).
/// </summary>
public sealed class InboundEndpointPattern
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private readonly string? _method;
    private readonly Regex? _path;

    private InboundEndpointPattern(string? method, Regex? path)
    {
        _method = method;
        _path = path;
    }

    /// <summary>Deseni ayrıştırır; geçersizse açıklayıcı istisna (fail-fast).</summary>
    public static InboundEndpointPattern Parse(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ArgumentException("Uç nokta deseni boş olamaz (hepsi için \"*\").", nameof(pattern));
        }

        var separator = pattern.IndexOf(':');
        var method = separator < 0 ? "*" : pattern.Substring(0, separator).Trim();
        var path = separator < 0 ? pattern.Trim() : pattern.Substring(separator + 1).Trim();
        if (path.Length == 0 || (path != "*" && path[0] != '/'))
        {
            throw new ArgumentException(
                $"Geçersiz uç nokta deseni '{pattern}': yol '/' ile başlamalı ya da \"*\" olmalı (ör. \"GET:/api/urun/*\").", nameof(pattern));
        }

        return new InboundEndpointPattern(
            method == "*" ? null : method.ToUpperInvariant(),
            path == "*" ? null : new Regex(
                "^" + Regex.Escape(path).Replace("\\*", ".*") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout));
    }

    /// <summary>İstek yöntemi ve yolu desene uyuyor mu.</summary>
    public bool Matches(string method, string path) =>
        (_method is null || string.Equals(_method, method, StringComparison.OrdinalIgnoreCase)) &&
        (_path is null || _path.IsMatch(path));
}
