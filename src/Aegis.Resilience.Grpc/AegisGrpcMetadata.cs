namespace Aegis.Resilience.Grpc;

/// <summary>gRPC dayanıklılık metadata anahtarları (gRPC A6).</summary>
public static class AegisGrpcMetadata
{
    /// <summary>
    /// Yeniden denemelerde istemcinin gönderdiği önceki deneme sayısı (ilk çağrıda yok, ilk yeniden denemede 1). Sunucu yeniden
    /// denenen çağrıyı buradan tanır.
    /// </summary>
    public const string PreviousAttempts = "grpc-previous-rpc-attempts";

    /// <summary>
    /// Sunucunun istemciye yeniden deneme bildirimi (trailer, milisaniye). Sıfır ya da pozitif: tam bu kadar bekleyip yeniden dene.
    /// Negatif ya da ayrıştırılamaz: yeniden deneme.
    /// </summary>
    public const string RetryPushback = "grpc-retry-pushback-ms";
}
