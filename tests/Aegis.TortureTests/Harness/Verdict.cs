namespace Aegis.TortureTests.Harness;

/// <summary>Bir kütüphanenin bir senaryo turundaki sonucu.</summary>
public enum VerdictKind
{
    Pass,
    Fail,
    NotSupported
}

/// <summary>Senaryo turu sonucu: geçti, kaldı (nedeniyle) ya da kütüphane bu yeteneği sunmuyor.</summary>
public readonly record struct Verdict(VerdictKind Kind, string? Reason = null)
{
    public static Verdict Pass { get; } = new(VerdictKind.Pass);

    public static Verdict Fail(string reason) => new(VerdictKind.Fail, reason);

    public static Verdict NotSupported(string reason) => new(VerdictKind.NotSupported, reason);
}

/// <summary>Senaryo içinde değişmez (invariant) ihlali; tur "kaldı" sayılır.</summary>
public sealed class InvariantViolationException(string message) : Exception(message);

/// <summary>Senaryo yardımcıları: değişmez denetimi.</summary>
public static class Invariant
{
    public static void That(bool condition, string violation)
    {
        if (!condition)
        {
            throw new InvariantViolationException(violation);
        }
    }
}
