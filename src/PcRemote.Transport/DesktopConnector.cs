using System.Net;
using System.Net.Sockets;
using PcRemote.Contracts;
using PcRemote.Protocol;

namespace PcRemote.Transport;

public interface IDesktopConnection : IAsyncDisposable
{
    Task SendAsync(Envelope message, CancellationToken cancellationToken);
    Task<Envelope> ReceiveAsync(CancellationToken cancellationToken);
}

public interface IDesktopConnector
{
    Task<IDesktopConnection> ConnectAsync(LanAdapter adapter, IPAddress remote, CancellationToken cancellationToken);
}

public sealed class TlsDesktopConnector : IDesktopConnector
{
    private const SocketOptionName WindowsIpUnicastInterface = (SocketOptionName)31;

    public async Task<IDesktopConnection> ConnectAsync(LanAdapter adapter, IPAddress remote, CancellationToken cancellationToken)
    {
        if (!LanNetwork.IsPeer(adapter, remote)) throw new IOException("Target is outside the direct LAN.");
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            client.Client.Bind(new IPEndPoint(adapter.Address, 0));
            client.Client.SetSocketOption(SocketOptionLevel.IP, WindowsIpUnicastInterface, IPAddress.HostToNetworkOrder(adapter.InterfaceIndex));
            client.NoDelay = true;
            await client.ConnectAsync(remote, DesktopMessages.Port, cancellationToken);
            var tls = await DesktopTls.ClientAsync(client.GetStream(), cancellationToken);
            return new StreamDesktopConnection(tls, client);
        }
        catch { client.Dispose(); throw; }
    }
}

public sealed class StreamDesktopConnection(Stream stream, IDisposable? owner = null) : IDesktopConnection
{
    public Task SendAsync(Envelope message, CancellationToken cancellationToken) => FrameCodec.WriteAsync(stream, message, cancellationToken);
    public Task<Envelope> ReceiveAsync(CancellationToken cancellationToken) => FrameCodec.ReadAsync(stream, cancellationToken);
    public async ValueTask DisposeAsync()
    {
        try { await stream.DisposeAsync(); }
        finally { owner?.Dispose(); }
    }
}
