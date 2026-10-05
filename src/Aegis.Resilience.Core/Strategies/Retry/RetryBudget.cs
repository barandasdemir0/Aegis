namespace Aegis.Resilience.Core.Strategies.Retry;

/// <summary>
/// Yeniden deneme bütçesi (gRPC A6 "retry throttling" algoritması; Envoy <c>retry_budget</c> ve Finagle <c>RetryBudget</c> ile aynı
/// amaç). Bağımlılık bozulduğunda her istemcinin yeniden denemesi trafiği katlar (retry fırtınası); bütçe, hata oranı yükselince
/// yeniden denemeleri ve ek hedging denemelerini kendiliğinden durdurur, sağlık dönünce yeniden açar.
/// <para>
/// Jeton sayısı <see cref="MaxTokens"/>'tan başlar. Hata sayılan her sonuç 1 jeton düşürür, her başarı <see cref="TokenRatio"/>
/// kadar ekler (üst sınır <see cref="MaxTokens"/>). Jeton sayısı <see cref="MaxTokens"/>'ın yarısına ya da altına inince yeniden
/// deneme yapılmaz; çağıran ilk hatayı beklemeden alır. Aynı örnek birden çok boru hattında paylaşılabilir (iş parçacığı güvenli).
/// </para>
/// </summary>
public sealed class RetryBudget
{
    // Jetonlar binde bir hassasiyetle tamsayıda tutulur: kilitsiz (Interlocked) ve kesirli TokenRatio desteklenir.
    private const int Scale = 1000;
    private readonly int _maxScaled;
    private readonly int _ratioScaled;
    private int _tokensScaled;

    /// <summary>Bütçeyi oluşturur.</summary>
    /// <param name="maxTokens">En fazla jeton (gRPC: 0 &lt; maxTokens ≤ 1000). Varsayılan 10.</param>
    /// <param name="tokenRatio">Her başarının eklediği jeton (gRPC: 0 &lt; tokenRatio). Varsayılan 0,1 (10 başarı = 1 hata telafisi).</param>
    public RetryBudget(double maxTokens = 10, double tokenRatio = 0.1)
    {
        if (maxTokens <= 0 || maxTokens > 1000 || double.IsNaN(maxTokens))
        {
            throw new ArgumentOutOfRangeException(nameof(maxTokens), maxTokens, "RetryBudget.MaxTokens (0, 1000] aralığında olmalıdır.");
        }

        if (tokenRatio <= 0 || double.IsNaN(tokenRatio) || double.IsInfinity(tokenRatio))
        {
            throw new ArgumentOutOfRangeException(nameof(tokenRatio), tokenRatio, "RetryBudget.TokenRatio sıfırdan büyük olmalıdır.");
        }

        MaxTokens = maxTokens;
        TokenRatio = tokenRatio;
        _maxScaled = (int)Math.Round(maxTokens * Scale);
        _ratioScaled = Math.Max(1, (int)Math.Round(tokenRatio * Scale));
        _tokensScaled = _maxScaled;
    }

    /// <summary>En fazla jeton.</summary>
    public double MaxTokens { get; }

    /// <summary>Her başarının eklediği jeton.</summary>
    public double TokenRatio { get; }

    /// <summary>Şu anki jeton sayısı (gözlem ve test için).</summary>
    public double Tokens => Volatile.Read(ref _tokensScaled) / (double)Scale;

    /// <summary>Yeniden deneme (ya da ek hedging denemesi) yapılabilir mi: jetonlar yarının üstündeyse.</summary>
    public bool CanRetry => Volatile.Read(ref _tokensScaled) > _maxScaled / 2;

    /// <summary>Hata sayılan bir sonucu kaydeder (1 jeton düşer, sıfırın altına inmez).</summary>
    public void RecordFailure() => Add(-Scale);

    /// <summary>Başarılı bir sonucu kaydeder (<see cref="TokenRatio"/> eklenir, <see cref="MaxTokens"/>'ı aşmaz).</summary>
    public void RecordSuccess() => Add(_ratioScaled);

    private void Add(int delta)
    {
        var current = Volatile.Read(ref _tokensScaled);
        while (true)
        {
            var next = Math.Max(0, Math.Min(_maxScaled, current + delta));
            if (next == current)
            {
                return;
            }

            var observed = Interlocked.CompareExchange(ref _tokensScaled, next, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
