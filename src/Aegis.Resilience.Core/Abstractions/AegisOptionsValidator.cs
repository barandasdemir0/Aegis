using System.Runtime.CompilerServices;

namespace Aegis.Resilience.Core.Abstractions;

/// <summary>
/// Strateji seçeneklerinin erken (fail-fast) doğrulanması için yardımcılar (AEGIS-130).
/// <para>
/// Önceki sürümlerde geçersiz değerler <c>Math.Max(1, x)</c> gibi ifadelerle SESSİZCE düzeltiliyordu:
/// <c>PermitLimit = 0</c> ("her şeyi engelle") 1'e, <c>Timeout = TimeSpan.FromTicks(500)</c> (yanlışlıkla
/// ms yerine tick) "zaman aşımı yok"a dönüşüyordu. Kullanıcı yapılandırma hatasını üretimde, yanlış davranış
/// olarak öğreniyordu. Artık geçersiz yapılandırma boru hattı KURULURKEN açık bir
/// <see cref="ArgumentOutOfRangeException"/> ile bildirilir — Polly ve .NET rate limiter'larıyla aynı yaklaşım.
/// </para>
/// </summary>
public static class AegisOptionsValidator
{
    /// <summary>Değer en az <paramref name="min"/> olmalıdır.</summary>
    public static void AtLeast(int value, int min, string optionsType, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value < min)
        {
            throw new ArgumentOutOfRangeException(name, value,
                $"{optionsType}.{Simplify(name)} en az {min} olmalıdır (verilen: {value}).");
        }
    }

    /// <summary>Değer sıfırdan büyük bir süre olmalıdır.</summary>
    public static void Positive(TimeSpan value, string optionsType, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, value,
                $"{optionsType}.{Simplify(name)} sıfırdan büyük olmalıdır (verilen: {value}). " +
                "İpucu: TimeSpan.FromMilliseconds/FromSeconds kullandığınızdan emin olun.");
        }
    }

    /// <summary>Değer sıfırdan büyük bir süre ya da <see cref="Timeout.InfiniteTimeSpan"/> olmalıdır.</summary>
    public static void PositiveOrInfinite(TimeSpan value, string optionsType, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value != Timeout.InfiniteTimeSpan && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, value,
                $"{optionsType}.{Simplify(name)} sıfırdan büyük ya da Timeout.InfiniteTimeSpan olmalıdır (verilen: {value}).");
        }
    }

    /// <summary>Değer negatif olmayan bir süre olmalıdır (sıfır: özellik devre dışı).</summary>
    public static void NonNegative(TimeSpan value, string optionsType, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, value,
                $"{optionsType}.{Simplify(name)} negatif olamaz (verilen: {value}).");
        }
    }

    /// <summary>Değer negatif olmayan bir tam sayı olmalıdır.</summary>
    public static void NonNegative(int value, string optionsType, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value,
                $"{optionsType}.{Simplify(name)} negatif olamaz (verilen: {value}).");
        }
    }

    /// <summary>Değer [<paramref name="min"/>, <paramref name="max"/>] aralığında sonlu bir sayı olmalıdır.</summary>
    public static void InRange(double value, double min, double max, string optionsType, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, value,
                $"{optionsType}.{Simplify(name)} {min} ile {max} arasında sonlu bir sayı olmalıdır (verilen: {value}).");
        }
    }

    // "options.PermitLimit" -> "PermitLimit"
    private static string Simplify(string? expression)
    {
        if (expression is null or "")
        {
            return "<bilinmiyor>";
        }

        var dot = expression.LastIndexOf('.');
        return dot >= 0 ? expression.Substring(dot + 1) : expression;
    }
}
