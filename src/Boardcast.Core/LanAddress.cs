using System.Net;
using System.Net.Sockets;
namespace Boardcast.Core;
public static class LanAddress
{
    public static bool IsAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        byte[] b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254;
        return address.IsIPv6LinkLocal || (b[0] & 0xfe) == 0xfc;
    }
    public static IPAddress Parse(string value)
    {
        if (!IPAddress.TryParse(value, out var address) || !IsAllowed(address))
            throw new InvalidDataException("Only literal LAN or loopback IP addresses are allowed.");
        return address;
    }
}
