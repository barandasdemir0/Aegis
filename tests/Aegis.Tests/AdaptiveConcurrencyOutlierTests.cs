using Aegis.Resilience.Core.Strategies.RateLimiter;

namespace Aegis.Tests;

/// <summary>
/// AEGIS-127 regresyonu: Adaptif eşzamanlılık limiti, tek bir aykırı-hızlı örnek ya da soğuk başlangıç
/// yüzünden çökmemelidir. Senaryo, test host'ta GERÇEKTEN gözlemlenen ölçümlerin birebir kopyasıdır.
/// Zamanlamaya bağlı kalmadan, örnekler algoritmaya doğrudan beslenir (deterministik).
/// </summary>
public class AdaptiveConcurrencyOutlierTests
{
    private static AdaptiveConcurrencyStrategy Create(int warmup = 5) => new(new AdaptiveConcurrencyOptions
    {
        InitialConcurrency = 10,
        MinConcurrency = 2,
        MaxConcurrency = 50,
        SmoothingFactor = 0.2,
        WarmupSamples = warmup
    });

    [Fact]
    public void ColdStart_ThenSingleFastOutlier_ShouldNotCollapseLimit()
    {
        var s = Create();

        // Gözlemlenen gerçek dizi: 27ms soğuk JIT, 6.8ms erken tetiklenen timer, sonra kararlı ~15.6ms
        double[] observed = { 26.9, 6.8, 15.3, 15.5, 15.8, 24.0, 15.6, 15.7, 16.0, 12.7, 15.6, 15.7, 15.7, 15.6, 15.5, 15.8, 15.6, 15.5, 16.0, 15.7 };
        foreach (var rtt in observed)
        {
            s.RecordSampleForTesting(rtt);
        }

        // Düzeltmeden önce: limit 10 -> 5 -> 3 -> 2'ye çöküp orada kalıyordu.
        Assert.True(s.CurrentLimit >= 10, $"kararlı gecikmede limit çöktü: {s.CurrentLimit}");
    }

    [Fact]
    public void RepeatedOutliers_OnlyConfirmedMinimum_ShouldBeAdopted()
    {
        var s = Create(warmup: 1);

        for (var i = 0; i < 10; i++) s.RecordSampleForTesting(20); // kararlı 20ms
        var before = s.CurrentLimit;

        s.RecordSampleForTesting(2);   // tek aykırı hızlı örnek -> ADAY, kabul edilmez
        for (var i = 0; i < 10; i++) s.RecordSampleForTesting(20);

        Assert.True(s.CurrentLimit >= before, $"tek aykırı örnek limiti daralttı: {before} -> {s.CurrentLimit}");
    }

    [Fact]
    public void GenuineSpeedup_ConfirmedByTwoSamples_ShouldBeAdopted_AndRealSlowdownStillDetected()
    {
        var s = Create(warmup: 1);

        for (var i = 0; i < 10; i++) s.RecordSampleForTesting(20);
        s.RecordSampleForTesting(5);
        s.RecordSampleForTesting(5); // ardışık ikinci örnek -> yeni minimum GERÇEK, kabul edilir
        for (var i = 0; i < 20; i++) s.RecordSampleForTesting(5);
        var fastLimit = s.CurrentLimit;

        // Gerçek yavaşlama: 5ms -> 60ms. Limit gerçekten DARALMALI (algoritma işini yapmaya devam ediyor)
        for (var i = 0; i < 15; i++) s.RecordSampleForTesting(60);

        Assert.True(s.CurrentLimit < fastLimit, $"gerçek yavaşlama algılanmadı: {fastLimit} -> {s.CurrentLimit}");
        Assert.True(s.CurrentLimit >= 2, "MinConcurrency altına inilmemeli");
    }

    [Fact]
    public void ZeroRttSamples_ShouldNotFreezeAlgorithm()
    {
        // AEGIS-132: Senkron çağrılarda Stopwatch 0.0ms ölçebilir; minRtt=0 olunca "_minRttMs > 0" koruması
        // algoritmayı 5 dakika donduruyordu — hedef servis çökse bile limit daralmıyordu.
        var s = Create(warmup: 1);
        for (var i = 0; i < 10; i++) s.RecordSampleForTesting(0.0); // önbellek isabetleri: sıfır RTT
        var afterZeros = s.CurrentLimit;
        Assert.True(afterZeros >= 10, $"sıfır RTT'de limit düştü: {afterZeros}");

        for (var i = 0; i < 15; i++) s.RecordSampleForTesting(200.0); // hedef servis çöktü
        Assert.True(s.CurrentLimit < afterZeros, $"sıfır RTT örnekleri algoritmayı dondurdu: {afterZeros} -> {s.CurrentLimit}");
        Assert.Equal(2, s.CurrentLimit); // MinConcurrency tabanına inmeli
    }

    [Fact]
    public void TimerGranularityPhaseShift_ShouldNotCollapseLimit()
    {
        // AEGIS-143: Paketlenmiş showcase'te gözlemlenen GERÇEK dizi. curl istemcisi ile Task.Delay(5) bazen 0-2ms'de
        // dönüyor (iki ardışık ~1ms örnek minRtt'yi 1ms'ye "onaylıyor"); PowerShell istemcisi ile aynı çağrı timer
        // fazı gereği tutarlı ~14ms sürüyor. 5ms mutlak taban ile 14 > 1+5 => "14 kat yavaşlama" => limit 2'ye çöküyordu.
        var s = Create(warmup: 5);
        double[] curlPhase = { 19, 1, 14, 0.5, 18, 1, 11, 4, 1, 1, 18, 2, 16, 2, 15 };
        foreach (var r in curlPhase) s.RecordSampleForTesting(r);
        var afterCurl = s.CurrentLimit;
        Assert.True(afterCurl >= 10, $"jitter'lı hızlı çağrılarda limit düştü: {afterCurl}");

        double[] powershellPhase = Enumerable.Repeat(14.5, 30).ToArray();
        foreach (var r in powershellPhase) s.RecordSampleForTesting(r);

        Assert.True(s.CurrentLimit >= afterCurl, $"15ms'lik OS zamanlayıcı gürültüsü 'yavaşlama' sanıldı: {afterCurl} -> {s.CurrentLimit}");
    }

    [Fact]
    public void DuringWarmup_LimitShouldNotChange()
    {
        var s = Create(warmup: 5);
        s.RecordSampleForTesting(100); // soğuk başlangıç
        s.RecordSampleForTesting(3);
        s.RecordSampleForTesting(50);
        s.RecordSampleForTesting(3);
        Assert.Equal(10, s.CurrentLimit); // 4 örnek < 5 ısınma -> dokunulmadı
    }
}
