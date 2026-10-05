using Aegis.Resilience.Extensions.DependencyInjection.Aop;

namespace Shop.Api;

/// <summary>
/// Eski bir iç sistem: kodda tek satır dayanıklılık yok; politika arayüz üzerinden AOP proxy ile uygulanır (<c>[AegisPolicy]</c>).
/// Senkron metot da korunur (eski kod tabanları).
/// </summary>
public interface ILegacyGateway
{
    [AegisPolicy("legacy")]
    Task<string> GetAsync(string id, CancellationToken cancellationToken);

    [AegisPolicy("legacy")]
    string Get(string id);
}

public sealed class LegacyGateway(IHttpClientFactory clients) : ILegacyGateway
{
    public async Task<string> GetAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await clients.CreateClient("legacy").GetAsync($"/legacy/{id}", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public string Get(string id)
    {
        using var response = clients.CreateClient("legacy").Send(new HttpRequestMessage(HttpMethod.Get, $"/legacy/{id}"));
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(response.Content.ReadAsStream());
        return reader.ReadToEnd();
    }
}
