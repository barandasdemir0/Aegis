namespace Aegis.Resilience.Core.Exceptions;

/// <summary>
/// Aegis kütüphanesine ait temel istisna sınıfı.
/// </summary>
public class AegisException : Exception
{
    public AegisException(string message) : base(message) { }
    public AegisException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>
    /// İstisnayı üreten boru hattı ve strateji (Polly: <c>ExecutionRejectedException.TelemetrySource</c>). Yerleşik
    /// stratejilerin redlerinde (devre, zaman aşımı, hız sınırı) doldurulur; diğer durumlarda null.
    /// </summary>
    public Telemetry.AegisTelemetrySource? TelemetrySource { get; init; }
}
