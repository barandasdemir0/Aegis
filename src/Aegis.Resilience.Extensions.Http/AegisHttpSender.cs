using System.Net.Http;

namespace Aegis.Resilience.Extensions.Http;

/// <summary>Zincirdeki bir sonraki işleyiciye gönderim (async ya da senkron <c>Send</c> köprüsü).</summary>
public delegate Task<HttpResponseMessage> AegisHttpSender(HttpRequestMessage request, CancellationToken cancellationToken);
