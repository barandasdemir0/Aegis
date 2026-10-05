using System.Net.Http;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>Bağlantı kurma zaman aşımını deneme içinde geçici HTTP hatası olarak taşır (yalnızca yürütücü içinde görünür).</summary>
internal sealed class HttpConnectionTimeoutException(OperationCanceledException inner)
    : HttpRequestException("HTTP bağlantısı kurulamadı: bağlantı zaman aşımı.", inner);
