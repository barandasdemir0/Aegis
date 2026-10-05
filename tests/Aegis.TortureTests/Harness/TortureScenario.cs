namespace Aegis.TortureTests.Harness;

/// <summary>Karşılaştırılan kütüphaneler.</summary>
public enum Library
{
    Aegis,
    Polly,
    Microsoft
}

/// <summary>
/// Bir batırma senaryosu: aynı saldırı her kütüphaneye kendi en iyi API'siyle uygulanır. Bir kütüphane yeteneği hiç
/// sunmuyorsa senaryo <see cref="Verdict.NotSupported"/> döner; bu "kaldı" değildir, matriste ayrıca gösterilir.
/// Değişmez ihlali <see cref="Invariant.That"/> ile bildirilir; beklenmeyen istisna ve kilitlenme de "kaldı" sayılır.
/// </summary>
public abstract class TortureScenario
{
    public abstract string Name { get; }

    /// <summary>Saldırının ve doğrulanan değişmezlerin kısa açıklaması (rapora yazılır).</summary>
    public abstract string Description { get; }

    /// <summary>Bir turun izin verilen en uzun süresi; aşılırsa kilitlenme sayılır.</summary>
    public virtual TimeSpan Budget => TimeSpan.FromSeconds(60);

    public abstract IReadOnlyList<Library> Libraries { get; }

    public abstract Task RunAsync(Library library, Random random);
}
