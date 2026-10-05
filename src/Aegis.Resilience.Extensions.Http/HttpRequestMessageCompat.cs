using System.Net.Http;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// İstek üst verisi (sürüm, başlıklar, istek özellikleri) için tek nokta. .NET 5+ <c>HttpRequestMessage.Options</c> ile eski
/// hedeflerdeki <c>HttpRequestMessage.Properties</c> farkı yalnızca burada ele alınır; kopyalama mantığı da tek yerdedir.
/// </summary>
internal static class HttpRequestMessageCompat
{
    /// <summary>Gövde hariç kopya: yöntem, adres, sürüm (ve .NET 5+ sürüm politikası), başlıklar ve istek özellikleri.</summary>
    public static HttpRequestMessage CopyWithoutContent(HttpRequestMessage source, Uri? requestUri) =>
        CopyWithoutContent(source, requestUri, int.MaxValue);

    /// <summary>
    /// Gövde hariç kopya; başlıklardan yalnızca ilk <paramref name="maxHeaders"/> tanesi alınır (başlıklar ekleme sırasıyla
    /// saklanır). Yeniden denemede iç işleyicilerin önceki denemede eklediği başlıklar böylece klona taşınmaz.
    /// </summary>
    public static HttpRequestMessage CopyWithoutContent(HttpRequestMessage source, Uri? requestUri, int maxHeaders)
    {
        var clone = new HttpRequestMessage(source.Method, requestUri)
        {
            Version = source.Version,
#if !AEGIS_LEGACY
            VersionPolicy = source.VersionPolicy
#endif
        };

        var copied = 0;
        foreach (var header in source.Headers)
        {
            if (copied++ >= maxHeaders)
            {
                break;
            }

            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

#if AEGIS_LEGACY
        foreach (var property in source.Properties)
        {
            clone.Properties[property.Key] = property.Value;
        }
#else
        foreach (var option in source.Options)
        {
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        }
#endif

        return clone;
    }

    public static bool TryGetProperty<T>(HttpRequestMessage request, string key, out T? value)
    {
#if AEGIS_LEGACY
        if (request.Properties.TryGetValue(key, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
#else
        return request.Options.TryGetValue(new HttpRequestOptionsKey<T>(key), out value);
#endif
    }

    public static void RemoveProperty(HttpRequestMessage request, string key)
    {
#if AEGIS_LEGACY
        request.Properties.Remove(key);
#else
        ((IDictionary<string, object?>)request.Options).Remove(key);
#endif
    }

    public static void SetProperty<T>(HttpRequestMessage request, string key, T value)
    {
#if AEGIS_LEGACY
        request.Properties[key] = value;
#else
        request.Options.Set(new HttpRequestOptionsKey<T>(key), value);
#endif
    }
}
