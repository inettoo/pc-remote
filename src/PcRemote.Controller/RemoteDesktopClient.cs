using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using PcRemote.Contracts;
using PcRemote.Protocol;
using PcRemote.Transport;

namespace PcRemote.Controller;

public sealed record DesktopUpdate(DesktopImage? Image, string Status, bool Connected);

public sealed class RemoteDesktopClient(INetworkEnvironment environment) : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<Envelope> input = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(64) { SingleReader = true });
    private Task? running;
    private bool disposed;
    private volatile bool connected;
    private long lastFrame;

    public Task RunAsync(IPAddress address, Action<DesktopUpdate> update)
        => running = RunCoreAsync(address, update);

    public bool TryInput(RemoteInput value)
    {
        DesktopMessages.Validate(value);
        long received = Interlocked.Read(ref lastFrame);
        if (!connected || (value.Kind != RemoteInputKind.ReleaseAll && (received == 0 || Stopwatch.GetElapsedTime(received) > TimeSpan.FromSeconds(2)))) return false;
        if (input.Writer.TryWrite(DesktopMessages.Message("Input", value))) return true;
        Stop(); // Never silently drop key-up events and leave remote keys pressed.
        return false;
    }

    public void Stop()
    {
        lock (stop) { if (!disposed) stop.Cancel(); }
    }

    private async Task RunCoreAsync(IPAddress address, Action<DesktopUpdate> update)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            update(new(null, "正在连接…", false));
            var adapter = (await environment.GetLanAdaptersAsync(stop.Token))
                .FirstOrDefault(candidate => LanNetwork.IsPeer(candidate, address) && environment.HasDirectRoute(candidate, address))
                ?? throw new IOException("设备已离开当前直接局域网。");
            client.Client.Bind(new IPEndPoint(adapter.Address, 0));
            client.Client.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(adapter.InterfaceIndex));
            client.NoDelay = true;
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            handshake.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(address, DesktopMessages.Port, handshake.Token);
            await using var tls = await DesktopTls.ClientAsync(client.GetStream(), handshake.Token);
            connected = true;
            update(new(null, "已连接，正在准备用户桌面…", true));

            async Task ReceiveAsync()
            {
                while (true)
                {
                    var message = await FrameCodec.ReadAsync(tls, stop.Token).WaitAsync(TimeSpan.FromSeconds(15), stop.Token);
                    if (message.Type == "ScreenFrame")
                    {
                        var image = DesktopMessages.Read<DesktopImage>(message);
                        DesktopMessages.Validate(image);
                        Interlocked.Exchange(ref lastFrame, Stopwatch.GetTimestamp());
                        update(new(image, $"{image.Width} × {image.Height} · LAN 直连", true));
                    }
                    else if (message.Type == "DesktopUnavailable")
                    {
                        Interlocked.Exchange(ref lastFrame, 0);
                        var notice = DesktopMessages.Read<SessionNotice>(message);
                        DesktopMessages.Validate(notice);
                        TryInput(new(RemoteInputKind.ReleaseAll));
                        update(new(null, notice.Text, true));
                    }
                    else throw new InvalidDataException("收到不支持的桌面消息。");
                }
            }

            async Task SendAsync()
            {
                await foreach (var message in input.Reader.ReadAllAsync(stop.Token))
                    await FrameCodec.WriteAsync(tls, message, stop.Token);
            }

            async Task HeartbeatAsync()
            {
                while (true)
                {
                    if (!input.Writer.TryWrite(DesktopMessages.Message("Ping", new { })))
                        throw new IOException("输入队列已满，已断开以释放远端按键。");
                    await Task.Delay(3000, stop.Token);
                    if (!(await environment.GetLanAdaptersAsync(stop.Token)).Contains(adapter) || !environment.HasDirectRoute(adapter, address))
                        throw new IOException("局域网连接已变化。");
                }
            }

            Task receive = ReceiveAsync(), send = SendAsync(), heartbeat = HeartbeatAsync();
            try { await await Task.WhenAny(receive, send, heartbeat); }
            finally
            {
                Stop();
                try { await Task.WhenAll(receive, send, heartbeat); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            update(new(null, "连接已断开。", false));
        }
        catch (Exception exception)
        {
            update(new(null, $"连接结束：{exception.Message} 请确认被控端版本一致且已有用户登录。", false));
        }
        finally
        {
            connected = false;
            Interlocked.Exchange(ref lastFrame, 0);
            client.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (running is not null) await running;
        lock (stop)
        {
            if (disposed) return;
            disposed = true;
            stop.Dispose();
        }
    }
}
