using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Aegis.Resilience.Extensions.Caching;

/// <summary>Dağıtık depoya yazılan değerlerin serileştiricisi (Polly: <c>ICacheItemSerializer</c>).</summary>
public interface IAegisCacheSerializer
{
    /// <summary>Değeri bayt dizisine çevirir.</summary>
    byte[] Serialize<T>(T value);

    /// <summary>Bayt dizisinden değeri okur.</summary>
    T Deserialize<T>(byte[] data);
}
