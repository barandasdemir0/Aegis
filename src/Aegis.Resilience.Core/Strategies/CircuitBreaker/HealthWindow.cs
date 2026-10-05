using System.Diagnostics;

namespace Aegis.Resilience.Core.Strategies.CircuitBreaker;

/// <summary>
/// Devre kesicinin kapalı durumdaki sağlık istatistiği: <see cref="BucketCount"/> dilimli kayan pencere.
/// Sabit (tumbling) pencere, sınıra denk gelen hata patlamalarını ikiye bölüp kaçırır; dilimli pencere
/// son <c>SamplingDuration</c> içindeki çağrıları (en fazla bir dilim hassasiyetle) her an toplar.
/// <c>SamplingCount</c> verilirse sayı tabanlı çalışır: son N çağrının sonucu halka tamponda tutulur (resilience4j COUNT_BASED).
/// Kilitsizdir; çağıran senkronizasyonu sağlar.
/// </summary>
internal sealed class HealthWindow
{
    internal const int BucketCount = 10;
    private const long EmptyIndex = -1;

    private readonly Bucket[] _buckets = new Bucket[BucketCount];
    private long _bucketTicks;
    private long _frequency = Stopwatch.Frequency;
    private TimeSpan _samplingDuration;

    // Sayı tabanlı pencere (null: zaman tabanlı). Bayraklar: 1 = hata, 2 = yavaş. Sayaçlar artımlı tutulur (kayıt ve özet O(1)).
    private const byte FailureFlag = 1;
    private const byte SlowFlag = 2;
    private byte[]? _ring;
    private int _ringNext;
    private int _ringCount;
    private int _ringFailures;
    private int _ringSlowCalls;

    private struct Bucket
    {
        public long Index;
        public int Successes;
        public int Failures;
        public int SlowCalls;
    }

    public HealthWindow(TimeSpan samplingDuration) => Configure(samplingDuration);

    /// <summary>Pencere süresini ayarlar; süre değiştiyse (canlı yapılandırma) eski istatistik geçersiz sayılıp temizlenir.</summary>
    public void Configure(TimeSpan samplingDuration)
    {
        // double -> long taşmasını önle (TimeSpan.MaxValue vb.).
        if (samplingDuration == _samplingDuration && _bucketTicks != 0)
        {
            return; // her kayıtta çağrılır; süre değişmediyse hesap yok
        }

        _samplingDuration = samplingDuration;
        var windowTicks = (long)Math.Min(samplingDuration.TotalSeconds * _frequency, long.MaxValue / 2.0);
        var bucketTicks = Math.Max(1, windowTicks / BucketCount);
        if (bucketTicks != _bucketTicks)
        {
            _bucketTicks = bucketTicks;
            Reset();
        }
    }

    /// <summary>Sayı tabanlı pencereyi açar/kapatır (null: zaman tabanlı); boyut değiştiyse istatistik temizlenir.</summary>
    public void ConfigureCount(int? samplingCount)
    {
        if (samplingCount is not { } count)
        {
            _ring = null;
            return;
        }

        if (_ring is null || _ring.Length != count)
        {
            _ring = new byte[count];
            ResetRing();
        }
    }


    /// <summary>Zaman damgası frekansını değiştirir (özel <see cref="TimeProvider"/>); istatistik sıfırlanır.</summary>
    public void UseFrequency(long frequency)
    {
        if (frequency != _frequency)
        {
            _frequency = frequency;
            _bucketTicks = 0; // Configure yeniden hesaplasın
            Configure(_samplingDuration);
        }
    }

    public void Record(long timestamp, bool isFailure, bool isSlow)
    {
        if (_ring is { } ring)
        {
            RecordInRing(ring, isFailure, isSlow);
            return;
        }

        var index = timestamp / _bucketTicks;
        ref var bucket = ref _buckets[index % BucketCount];
        if (bucket.Index != index)
        {
            bucket = new Bucket { Index = index };
        }

        if (isFailure)
        {
            bucket.Failures++;
        }
        else
        {
            bucket.Successes++;
        }

        if (isSlow)
        {
            bucket.SlowCalls++;
        }
    }

    public CircuitHealth Snapshot(long timestamp)
    {
        if (_ring is not null)
        {
            return new CircuitHealth(_ringCount - _ringFailures, _ringFailures, _ringSlowCalls);
        }

        var current = timestamp / _bucketTicks;
        int successes = 0, failures = 0, slowCalls = 0;
        foreach (var bucket in _buckets)
        {
            if (bucket.Index != EmptyIndex && bucket.Index <= current && current - bucket.Index < BucketCount)
            {
                successes += bucket.Successes;
                failures += bucket.Failures;
                slowCalls += bucket.SlowCalls;
            }
        }

        return new CircuitHealth(successes, failures, slowCalls);
    }

    public void Reset()
    {
        if (_ring is not null)
        {
            ResetRing();
        }

        for (var i = 0; i < _buckets.Length; i++)
        {
            _buckets[i] = new Bucket { Index = EmptyIndex };
        }
    }

    private void RecordInRing(byte[] ring, bool isFailure, bool isSlow)
    {
        if (_ringCount == ring.Length)
        {
            var evicted = ring[_ringNext]; // en eski sonuç pencereden düşer
            _ringFailures -= evicted & FailureFlag;
            _ringSlowCalls -= (evicted & SlowFlag) >> 1;
        }
        else
        {
            _ringCount++;
        }

        var flags = (byte)((isFailure ? FailureFlag : 0) | (isSlow ? SlowFlag : 0));
        ring[_ringNext] = flags;
        _ringFailures += flags & FailureFlag;
        _ringSlowCalls += (flags & SlowFlag) >> 1;
        _ringNext = (_ringNext + 1) % ring.Length;
    }

    private void ResetRing()
    {
        _ringNext = 0;
        _ringCount = 0;
        _ringFailures = 0;
        _ringSlowCalls = 0;
    }
}