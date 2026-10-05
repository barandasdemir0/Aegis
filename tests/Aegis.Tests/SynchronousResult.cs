namespace Aegis.Tests;

/// <summary>
/// Tahsis ölçen testler için: eşzamanlı tamamlanması gereken bir <see cref="ValueTask{TResult}"/>'ın tamamlandığını doğrular
/// ve sonucunu döner. <c>await</c> kullanılmaz; kullanılsaydı ölçüme test metodunun kendi async durum makinesi karışırdı.
/// Görev tamamlanmış olduğundan <c>Result</c> engellemez.
/// </summary>
internal static class SynchronousResult
{
    public static T Of<T>(ValueTask<T> pending)
    {
        Assert.True(pending.IsCompletedSuccessfully, "Görev eşzamanlı tamamlanmalıydı (sıfır tahsisli hızlı yol).");
        return pending.Result;
    }
}
