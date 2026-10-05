namespace Aegis.Resilience.Core.Context;

/// <summary>
/// <see cref="AegisContext"/> özellikleri için tipli anahtar (Polly: <c>ResiliencePropertyKey&lt;TValue&gt;</c>).
/// Anahtar ile değer tipi birlikte taşınır; yanlış tiple okuma/yazma derleme anında yakalanır.
/// Aynı adı taşıyan string anahtarla da uyumludur (aynı sözlük kullanılır).
/// </summary>
/// <example>
/// <code>
/// static readonly AegisPropertyKey&lt;string&gt; TenantKey = new("tenant");
/// context.SetProperty(TenantKey, "acme");
/// if (context.TryGetProperty(TenantKey, out var tenant)) { ... }
/// </code>
/// </example>
public readonly struct AegisPropertyKey<TValue> : IEquatable<AegisPropertyKey<TValue>>
{
    public AegisPropertyKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        Key = key;
    }

    /// <summary>Anahtarın adı.</summary>
    public string Key { get; }

    public bool Equals(AegisPropertyKey<TValue> other) => string.Equals(Key, other.Key, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is AegisPropertyKey<TValue> other && Equals(other);

    public override int GetHashCode() => Key is null ? 0 : StringComparer.Ordinal.GetHashCode(Key);

    public override string ToString() => Key;

    public static bool operator ==(AegisPropertyKey<TValue> left, AegisPropertyKey<TValue> right) => left.Equals(right);

    public static bool operator !=(AegisPropertyKey<TValue> left, AegisPropertyKey<TValue> right) => !left.Equals(right);
}
