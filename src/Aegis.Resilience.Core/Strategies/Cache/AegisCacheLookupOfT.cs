namespace Aegis.Resilience.Core.Strategies.Cache;

/// <summary>Depo okumasının sonucu: <see cref="AegisCacheLookup.Hit{T}"/> ya da <c>default</c> (ıskalama).</summary>
public readonly struct AegisCacheLookup<T>
{
    internal AegisCacheLookup(T value)
    {
        Found = true;
        Value = value;
    }

    /// <summary>Değer bulundu mu.</summary>
    public bool Found { get; }

    /// <summary>Bulunan değer (ıskalamada varsayılan).</summary>
    public T? Value { get; }
}
