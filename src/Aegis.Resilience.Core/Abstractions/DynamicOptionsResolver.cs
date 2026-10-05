namespace Aegis.Resilience.Core.Abstractions;

/// <summary>
/// Canlı (hot-reload) seçenekleri güvenli biçimde çözer (AEGIS-145).
/// <para>
/// Strateji kurucusunda statik seçenekler <c>Validate()</c> ile doğrulanır; ancak <c>OptionsProvider</c> ile her
/// çağrıda gelen DİNAMİK seçenekler doğrulanmıyordu. Yapılandırma sunucusundan gelen tek bir yazım hatası
/// (<c>PermitLimit = 0</c>, <c>FailureRatio = 1.5</c>) sessizce yanlış davranışa dönüşüyordu. Polly'nin reload
/// semantiği uygulanır: geçersiz dinamik seçenekler ATILIR ve <b>son geçerli</b> seçeneklerle devam edilir —
/// hatalı bir yapılandırma yayını üretim trafiğini ne bozar ne de düşürür. Sağlayıcı istisna fırlatırsa da aynı
/// şekilde son geçerli seçenekler kullanılır.
/// </para>
/// </summary>
public static class DynamicOptionsResolver
{
    public static TOptions Resolve<TOptions>(TOptions staticOptions, Func<TOptions>? provider, Action<TOptions> validate, ref TOptions? lastGood)
        where TOptions : class
    {
        if (provider == null)
        {
            return staticOptions;
        }

        TOptions? candidate;
        try
        {
            candidate = provider();
        }
        catch
        {
            return Volatile.Read(ref lastGood) ?? staticOptions; // sağlayıcı patladı: son geçerliyle devam
        }

        if (candidate == null)
        {
            return staticOptions;
        }

        if (ReferenceEquals(candidate, Volatile.Read(ref lastGood)))
        {
            return candidate; // aynı örnek zaten doğrulanmıştı: hızlı yol
        }

        try
        {
            validate(candidate);
        }
        catch (ArgumentException)
        {
            return Volatile.Read(ref lastGood) ?? staticOptions; // geçersiz canlı yapılandırma: son geçerliyle devam
        }

        Volatile.Write(ref lastGood, candidate);
        return candidate;
    }
}
