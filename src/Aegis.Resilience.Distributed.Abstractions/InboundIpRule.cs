using System.Net;
using System.Net.Sockets;

namespace Aegis.Resilience.Distributed.Abstractions;

/// <summary>
/// Tek IP ya da CIDR ağı. Bayt karşılaştırmasıyla çalışır (<c>IPNetwork</c> .NET Framework'te yoktur); IPv4'e eşlenmiş IPv6
/// adresleri IPv4 olarak değerlendirilir.
/// </summary>
public readonly struct InboundIpRule
{
    private readonly byte[] _network;
    private readonly int _prefixLength;

    private InboundIpRule(byte[] network, int prefixLength)
    {
        _network = network;
        _prefixLength = prefixLength;
    }

    /// <summary>Tek adres (<c>"10.0.0.5"</c>, <c>"::1"</c>) ya da ağ (<c>"10.0.0.0/8"</c>) ayrıştırır; geçersizse istisna.</summary>
    public static InboundIpRule Parse(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        var slash = value.IndexOf('/');
        var addressText = slash < 0 ? value : value.Substring(0, slash);
        if (IPAddress.TryParse(addressText, out var address))
        {
            var bytes = Normalize(address).GetAddressBytes();
            var maxPrefix = bytes.Length * 8;
            if (slash < 0)
            {
                return new InboundIpRule(bytes, maxPrefix);
            }

#if AEGIS_LEGACY
            var prefixText = value.Substring(slash + 1);
#else
            var prefixText = value.AsSpan(slash + 1);
#endif
            if (int.TryParse(prefixText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var prefix) &&
                prefix <= maxPrefix)
            {
                return new InboundIpRule(bytes, prefix);
            }
        }

        throw new ArgumentException($"Geçersiz IP ya da ağ: '{value}' (ör. \"10.0.0.5\", \"10.0.0.0/8\").", nameof(value));
    }

    /// <summary>Adres bu ağın içinde mi.</summary>
    public bool Contains(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var bytes = Normalize(address).GetAddressBytes();
        if (_network is null || bytes.Length != _network.Length)
        {
            return false;
        }

        var fullBytes = _prefixLength / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (bytes[i] != _network[i])
            {
                return false;
            }
        }

        var remainingBits = _prefixLength % 8;
        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (bytes[fullBytes] & mask) == (_network[fullBytes] & mask);
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
