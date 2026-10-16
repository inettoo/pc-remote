using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PcRemote.Contracts;

namespace PcRemote.Transport;

public static class LanNetwork
{
    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    public static bool IsUsableAdapter(LanAdapter adapter)
    {
        if (adapter.PrefixLength is < 8 or > 30 || adapter.InterfaceIndex <= 0 || !IsPrivate(adapter.Address)) return false;
        uint host = Number(adapter.Address);
        uint mask = Mask(adapter.PrefixLength);
        return host != (host & mask) && host != (host | ~mask)
            && IsPrivate(Address(host & mask)) && IsPrivate(Address(host | ~mask));
    }

    public static bool IsPeer(LanAdapter adapter, IPAddress peer)
    {
        if (!IsUsableAdapter(adapter) || !IsPrivate(peer)) return false;
        uint local = Number(adapter.Address), remote = Number(peer), mask = Mask(adapter.PrefixLength);
        return remote != local && (remote & mask) == (local & mask)
            && remote != (local & mask) && remote != (local | ~mask);
    }

    public static IPAddress Broadcast(LanAdapter adapter)
    {
        if (!IsUsableAdapter(adapter)) throw new ArgumentException("Invalid private IPv4 adapter.", nameof(adapter));
        return Address(Number(adapter.Address) | ~Mask(adapter.PrefixLength));
    }

    public static bool TryParsePeer(LanAdapter adapter, string text, out IPAddress? peer)
    {
        peer = null;
        // Only canonical dotted decimal: no hostnames, shorthand, integer or octal forms.
        if (!IPAddress.TryParse(text, out var parsed) || parsed.ToString() != text || !IsPeer(adapter, parsed)) return false;
        peer = parsed;
        return true;
    }

    private static uint Number(IPAddress address) => BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
    private static uint Mask(int prefix) => uint.MaxValue << (32 - prefix);
    private static IPAddress Address(uint address)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, address);
        return new IPAddress(bytes);
    }
}
