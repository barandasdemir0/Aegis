using System.Net.Http;
using Aegis.Resilience.Core.Context;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>
/// HttpRequestMessage üzerinde AegisContext bağlamını saklamak ve okumak için uzantı metotları.
/// İstek boyunca CorrelationId ve telemetri bilgilerinin şeffaf olarak taşınmasını sağlar.
/// </summary>
public static class HttpRequestMessageExtensions
{
    private const string ContextKey = "Aegis.Context";

    /// <summary>
    /// HTTP isteği ile ilişkili mevcut AegisContext'i döner, yoksa yeni bir tane oluşturup isteğe bağlar.
    /// </summary>
    public static AegisContext GetOrCreateAegisContext(this HttpRequestMessage request, string? pipelineName = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!HttpRequestMessageCompat.TryGetProperty<AegisContext>(request, ContextKey, out var context) || context is null)
        {
            context = new AegisContext(CancellationToken.None, pipelineName);
            HttpRequestMessageCompat.SetProperty(request, ContextKey, context);
        }

        AttachRequest(context, request);
        return context;
    }

    /// <summary>
    /// İşleyici yolu (Microsoft <c>ResilienceHandler</c> ile aynı kural): istekte kullanıcının bağlamı varsa o kullanılır;
    /// yoksa havuzdan kiralanır ve istek bitince <see cref="ReleaseAegisContext"/> ile iade edilir (istek başına tahsis yok).
    /// </summary>
    internal static AegisContext RentAegisContext(this HttpRequestMessage request, string? pipelineName, CancellationToken cancellationToken, out bool owned)
    {
        if (HttpRequestMessageCompat.TryGetProperty<AegisContext>(request, ContextKey, out var existing) && existing is not null)
        {
            owned = false;
            existing.CancellationToken = cancellationToken;
            AttachRequest(existing, request);
            return existing;
        }

        owned = true;
        var context = AegisContextPool.Rent(cancellationToken, pipelineName);
        context.SetProperty(AegisContextKeys.HttpRequest, request);
        HttpRequestMessageCompat.SetProperty(request, ContextKey, context);
        return context;
    }

    // İstek bağlama iliştirilir (telemetri: request.name / request.dependency.name; GetRequestMessage); kullanıcı bağlamı da kapsanır.
    private static void AttachRequest(AegisContext context, HttpRequestMessage request)
    {
        if (!context.TryGetProperty<HttpRequestMessage>(AegisContextKeys.HttpRequest, out _))
        {
            context.SetProperty(AegisContextKeys.HttpRequest, request);
        }
    }

    /// <summary>Kiralanan bağlamı istekten ayırır ve havuza iade eder; kullanıcının bağlamına dokunmaz.</summary>
    internal static void ReleaseAegisContext(this HttpRequestMessage request, AegisContext context, bool owned)
    {
        if (owned)
        {
            HttpRequestMessageCompat.RemoveProperty(request, ContextKey);
            // İşleyicinin yazdığı tek anahtar önce silinir: bağlam çoğu istekte boş döner ve havuzdaki temizlik numaralandırıcı
            // ayırmaz (eşzamanlı sözlükte tam temizlik istek başına ~64 B ayırıyordu).
            context.Properties.Remove(AegisContextKeys.HttpRequest);
            AegisContextPool.Return(context);
        }
    }

    /// <summary>
    /// Bağlamın ait olduğu HTTP isteği (Microsoft: <c>ResilienceContext.GetRequestMessage()</c>); ör. bildirimde veya koşulda
    /// URI ya da başlığa göre karar vermek için. Aegis HTTP işleyicileri dışında oluşturulan bağlamda null.
    /// </summary>
    public static HttpRequestMessage? GetRequestMessage(this AegisContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.TryGetProperty<HttpRequestMessage>(AegisContextKeys.HttpRequest, out var request) ? request : null;
    }

    /// <summary>
    /// HTTP isteği ile ilişkili AegisContext'i döner. Bulunamazsa null döner.
    /// </summary>
    public static AegisContext? GetAegisContext(this HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return HttpRequestMessageCompat.TryGetProperty<AegisContext>(request, ContextKey, out var context) ? context : null;
    }

    /// <summary>
    /// Belirtilen AegisContext'i HTTP isteği seçeneklerine ekler.
    /// </summary>
    public static void SetAegisContext(this HttpRequestMessage request, AegisContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        HttpRequestMessageCompat.SetProperty(request, ContextKey, context);
    }
}
