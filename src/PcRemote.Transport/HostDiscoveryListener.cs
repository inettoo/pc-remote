using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PcRemote.Contracts;
using PcRemote.Protocol;

namespace PcRemote.Transport;

public sealed class HostDiscoveryListener(INetworkEnvironment environment, string displayName)
{
    private const SocketOptionName WindowsIpUnicastInterface = (SocketOptionName)31;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ExclusiveAddressUse = true;
        socket.ReceiveBufferSize = 16 * 1024;
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, 1);
        socket.Bind(new IPEndPoint(IPAddress.Any, DiscoveryCodec.Port));
        var responder = new DiscoveryResponder(environment, Guid.NewGuid(), displayName);
        IReadOnlyList<LanAdapter> adapters = [];
        long refreshed = 0, budgetWindow = Stopwatch.GetTimestamp();
        int packets = 0;
        byte[] buffer = new byte[DiscoveryCodec.MaxDatagramBytes + 1];
        while (true)
        {
            SocketReceiveMessageFromResult result;
            try
            {
                result = await socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken);
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.MessageSize) { continue; }
            if (Stopwatch.GetElapsedTime(budgetWindow) >= TimeSpan.FromSeconds(1))
            {
                budgetWindow = Stopwatch.GetTimestamp();
                packets = 0;
            }
            if (++packets > 64 || result.ReceivedBytes > DiscoveryCodec.MaxDatagramBytes
                || (result.SocketFlags & SocketFlags.Truncated) != 0) continue;
            if (refreshed == 0 || Stopwatch.GetElapsedTime(refreshed) >= TimeSpan.FromSeconds(2))
            {
                adapters = await environment.GetLanAdaptersAsync(cancellationToken);
                refreshed = Stopwatch.GetTimestamp();
            }
            var source = (IPEndPoint)result.RemoteEndPoint;
            byte[]? reply = responder.Reply(buffer.AsSpan(0, result.ReceivedBytes), source.Address, result.PacketInformation.Interface, adapters);
            if (reply is null || source.Port == 0) continue;
            socket.SetSocketOption(SocketOptionLevel.IP, WindowsIpUnicastInterface, IPAddress.HostToNetworkOrder(result.PacketInformation.Interface));
            await socket.SendToAsync(reply, SocketFlags.None, source, cancellationToken);
        }
    }
}
