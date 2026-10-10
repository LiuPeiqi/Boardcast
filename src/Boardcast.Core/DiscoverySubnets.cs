using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Boardcast.Core;

/// <summary>Private IPv4 ranges explicitly learned through an authenticated IP connection or approved share.</summary>
public static class DiscoverySubnets
{
    public const int MaximumCount = 32;
    public const int MinimumPrefix = 16;
    public static List<DiscoverySubnet> Normalize(IEnumerable<DiscoverySubnet>? values, bool allowEmpty = true)
    {
        if (values is null) throw new InvalidDataException("网段列表不能为空。");
        var result = new List<DiscoverySubnet>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in values)
        {
            if (item is null || item.Port is < 1024 or > 65535) throw new InvalidDataException("网段端口必须在 1024–65535 之间。");
            var range = Parse(item.Cidr);
            var normalized = new DiscoverySubnet { Cidr = range.Cidr, Port = item.Port };
            if (!seen.Add(Key(normalized))) continue;
            if (result.Count == MaximumCount) throw new InvalidDataException("最多保存或分享 32 个网段。");
            result.Add(normalized);
        }
        if (!allowEmpty && result.Count == 0) throw new InvalidDataException("请选择要分享的网段。");
        return result;
    }
    public static string Key(DiscoverySubnet subnet) => subnet.Cidr + ":" + subnet.Port;
    public static PrivateIpv4Range Parse(string? cidr, int minimumPrefix = MinimumPrefix)
    {
        var parts = cidr?.Split('/');
        if (parts is not { Length: 2 } || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            !int.TryParse(parts[1], out int prefix) || prefix < minimumPrefix || prefix > 32)
            throw new InvalidDataException($"网段需要使用私有 IPv4 CIDR /{minimumPrefix}–/32 格式。");
        uint value = ToUInt(ip), mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        uint network = value & mask, last = network | ~mask;
        if (!IsPrivate(ToAddress(network)) || !IsPrivate(ToAddress(last))) throw new InvalidDataException("只支持 10、172.16–31、192.168 私有 IPv4 网段。");
        return new PrivateIpv4Range(network, prefix);
    }
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168;
    }
    public static List<DiscoverySubnet> FromConnectedPeer(PeerInfo peer)
    {
        if (!IPAddress.TryParse(peer.Address, out var address) || !IsPrivate(address)) return [];
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var actual = new List<DiscoverySubnet>();
        foreach (var subnet in peer.DiscoverySubnets ?? [])
        {
            try
            {
                var range = Parse(subnet.Cidr, minimumPrefix: 8);
                if (range.Contains(address) && range.Prefix >= MinimumPrefix && subnet.Port == peer.Port)
                    actual.Add(new() { Cidr = range.Cidr, Port = subnet.Port });
            }
            catch (InvalidDataException) { }
        }
        if (actual.Count > 0) return Normalize(actual.OrderByDescending(subnet => Parse(subnet.Cidr).Prefix).Take(1));
        var fallback = new PrivateIpv4Range(ToUInt(address) & 0xffffff00, 24);
        return [new DiscoverySubnet { Cidr = fallback.Cidr, Port = peer.Port }];
    }
    public static List<DiscoverySubnet> LocalInterfaces(int port, bool advertiseActual = false)
    {
        var found = new List<DiscoverySubnet>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var item in nic.GetIPProperties().UnicastAddresses)
                {
                    if (!IsPrivate(item.Address)) continue;
                    int prefix = item.PrefixLength;
                    if (prefix is < 8 or > 32) continue;
                    if (!advertiseActual && prefix < MinimumPrefix) prefix = 24;
                    uint mask = uint.MaxValue << (32 - prefix);
                    var range = new PrivateIpv4Range(ToUInt(item.Address) & mask, prefix);
                    if (!IsPrivate(ToAddress(range.Network)) || !IsPrivate(ToAddress(range.Last))) continue;
                    found.Add(new() { Cidr = range.Cidr, Port = port });
                }
            }
        }
        catch (NetworkInformationException) { }
        return found.DistinctBy(Key).Take(MaximumCount).ToList();
    }
    internal static uint ToUInt(IPAddress address) { var b = address.GetAddressBytes(); return (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3]; }
    internal static IPAddress ToAddress(uint value) => new(new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
}

public sealed record PrivateIpv4Range(uint Network, int Prefix)
{
    public uint Last => Network | (Prefix == 32 ? 0 : uint.MaxValue >> Prefix);
    public string Cidr => DiscoverySubnets.ToAddress(Network) + "/" + Prefix;
    public bool Contains(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork && DiscoverySubnets.ToUInt(address) >= Network && DiscoverySubnets.ToUInt(address) <= Last;
    public IEnumerable<IPAddress> Hosts()
    {
        ulong first = Prefix < 31 ? (ulong)Network + 1 : Network;
        ulong last = Prefix < 31 ? (ulong)Last - 1 : Last;
        for (ulong host = first; host <= last; host++) yield return DiscoverySubnets.ToAddress((uint)host);
    }
}
