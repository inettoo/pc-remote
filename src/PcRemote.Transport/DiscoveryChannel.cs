using System.Net;
using System.Net.Sockets;
using PcRemote.Contracts;
using PcRemote.Protocol;

namespace PcRemote.Transport;

public sealed record DiscoveryDatagram(byte[] Data, IPAddress Source, int InterfaceIndex, bool Truncated = false);

public interface IDiscoveryChannel : IDisposable
{
    ValueTask SendAsync(byte[] data, CancellationToken cancellationToken);
    ValueTask<DiscoveryDatagram> ReceiveAsync(CancellationToken cancellationToken);
}

public interface IDiscoveryChannelFactory
{
    IDiscoveryChannel Open(LanAdapter adapter);
}

public sealed class UdpDiscoveryChannelFactory : IDiscoveryChannelFactory
{
    public IDiscoveryChannel Open(LanAdapter adapter) => new UdpDiscoveryChannel(adapter);
}

internal sealed class UdpDiscoveryChannel : IDiscoveryChannel
{
    private const SocketOptionName WindowsIpUnicastInterface = (SocketOptionName)31;
    private readonly Socket receiver;

    private readonly IPEndPoint broadcast;

    public UdpDiscoveryChannel(LanAdapter adapter)
    {
        broadcast = new IPEndPoint(LanNetwork.Broadcast(adapter), DiscoveryCodec.Port);
        receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            // Controller uses an ephemeral source port; replies return to this socket.
            receiver.ExclusiveAddressUse = true;
            receiver.ReceiveBufferSize = 16 * 1024;
            receiver.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            receiver.Bind(new IPEndPoint(adapter.Address, 0));
            receiver.EnableBroadcast = true;
            receiver.SetSocketOption(SocketOptionLevel.IP, WindowsIpUnicastInterface, IPAddress.HostToNetworkOrder(adapter.InterfaceIndex));
            receiver.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, 1);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public async ValueTask SendAsync(byte[] data, CancellationToken cancellationToken)
        => await receiver.SendToAsync(data.AsMemory(), SocketFlags.None, broadcast, cancellationToken);

    public async ValueTask<DiscoveryDatagram> ReceiveAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[DiscoveryCodec.MaxDatagramBytes + 1];
        try
        {
            var result = await receiver.ReceiveMessageFromAsync(buffer.AsMemory(), SocketFlags.None,
                new IPEndPoint(IPAddress.Any, 0), cancellationToken);
            return new DiscoveryDatagram(buffer[..result.ReceivedBytes], ((IPEndPoint)result.RemoteEndPoint).Address,
                result.PacketInformation.Interface, (result.SocketFlags & SocketFlags.Truncated) != 0);
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.MessageSize)
        {
            // Windows reports oversized UDP messages as WSAEMSGSIZE. Drop without parsing.
            return new DiscoveryDatagram([], IPAddress.Any, 0, true);
        }
    }

    public void Dispose()
    {
        receiver.Dispose();
    }
}

