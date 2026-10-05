namespace Aegis.Resilience.Core.Strategies.Cache;

/// <summary><see cref="AegisCacheLookup{T}"/> üreticileri.</summary>
public static class AegisCacheLookup
{
    /// <summary>İsabet.</summary>
    public static AegisCacheLookup<T> Hit<T>(T value) => new(value);

    /// <summary>Iskalama.</summary>
    public static AegisCacheLookup<T> Miss<T>() => default;
}
