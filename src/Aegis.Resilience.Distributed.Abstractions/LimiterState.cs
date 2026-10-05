using System.Collections.Concurrent;

namespace Aegis.Resilience.Distributed.Abstractions;

/// <summary>Bir anahtarın sınırlayıcı durumu (tüm algoritmalar için ortak alanlar).</summary>
internal sealed class LimiterState
{
    public double Tokens = -1; // -1: kova henüz başlatılmadı
    public long LastRefillMs;
    public long WindowId = -1;
    public int CurrentCount;
    public int PreviousCount;
    public long ExpiresAtMs;
}
