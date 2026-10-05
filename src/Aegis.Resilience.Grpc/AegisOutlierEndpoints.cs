#if NET
using System.Globalization;
using System.Net;
using Grpc.Net.Client.Balancer;

namespace Aegis.Resilience.Grpc;

/// <summary>Seçilen uç nokta (istek seçeneğinde taşınır) ve havuzdaki uç nokta sayısı.</summary>
internal sealed record AegisOutlierEndpoint(string Key, int KnownEndpoints);

/// <summary>Uç nokta anahtarı ve istekte taşınan seçim bilgisi.</summary>
internal static class AegisOutlierEndpoints
{
    public static readonly HttpRequestOptionsKey<AegisOutlierEndpoint> RequestKey = new("Aegis.Resilience.Grpc.OutlierEndpoint");

    public static string KeyOf(BalancerAddress address) => address.EndPoint switch
    {
        DnsEndPoint dns => string.Create(CultureInfo.InvariantCulture, $"{dns.Host}:{dns.Port}"),
        var other => other.ToString() ?? string.Empty
    };
}
#endif
