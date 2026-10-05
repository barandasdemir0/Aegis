using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Aegis.Resilience.Extensions.Caching;

/// <summary>
/// System.Text.Json serileştiricisi. Tür bilgisi verilen <see cref="JsonSerializerOptions"/>'ın çözümleyicisinden alınır:
/// kaynak üreticili bir <c>JsonSerializerContext</c> ile Native AOT ve trimming güvenlidir.
/// Örnek: <c>new SystemTextJsonCacheSerializer(UygulamaJsonContext.Default.Options)</c>.
/// </summary>
public sealed class SystemTextJsonCacheSerializer : IAegisCacheSerializer
{
    private readonly JsonSerializerOptions _options;

    /// <param name="options">Tür çözümleyicisi (<see cref="JsonSerializerOptions.TypeInfoResolver"/>) tanımlı seçenekler.</param>
    public SystemTextJsonCacheSerializer(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.TypeInfoResolver is null)
        {
            throw new ArgumentException(
                "JsonSerializerOptions.TypeInfoResolver tanımlı olmalı (ör. bir JsonSerializerContext). Yansıma tabanlı " +
                "serileştirme için SystemTextJsonCacheSerializer.CreateReflectionBased() kullanın.", nameof(options));
        }

        options.MakeReadOnly();
        _options = options;
    }

    /// <summary>
    /// Yansıma tabanlı serileştirici (kaynak üretici bağlamı gerekmez). Native AOT ve trimming ile uyumsuzdur; bu
    /// uygulamalarda kaynak üreticili bağlamla oluşturun.
    /// </summary>
    [RequiresUnreferencedCode("Yansıma tabanlı JSON serileştirme trimming ile uyumsuzdur; JsonSerializerContext kullanın.")]
    [RequiresDynamicCode("Yansıma tabanlı JSON serileştirme Native AOT ile uyumsuzdur; JsonSerializerContext kullanın.")]
    public static SystemTextJsonCacheSerializer CreateReflectionBased(JsonSerializerOptions? options = null)
    {
        var configured = new JsonSerializerOptions(options ?? JsonSerializerOptions.Default)
        {
            TypeInfoResolver = options?.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver()
        };
        return new SystemTextJsonCacheSerializer(configured);
    }

    /// <inheritdoc />
    public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, TypeInfo<T>());

    /// <inheritdoc />
    public T Deserialize<T>(byte[] data) => JsonSerializer.Deserialize(data, TypeInfo<T>())!;

    private JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)_options.GetTypeInfo(typeof(T));
}
