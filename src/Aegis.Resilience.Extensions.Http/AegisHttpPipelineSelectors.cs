using System.Net.Http;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>Hazır boru hattı seçicileri: isteği anahtar başına boru hattına eşler (Microsoft: <c>SelectPipelineByAuthority</c>).</summary>
public static class AegisHttpPipelineSelectors
{
    /// <summary>Hedef authority (şema + host + port) başına ayrı boru hattı; adressiz istekte tek, paylaşılan anahtar.</summary>
    public static Func<HttpRequestMessage, string> ByAuthority { get; } =
        static request => request.RequestUri?.GetLeftPart(UriPartial.Authority) ?? string.Empty;
}
