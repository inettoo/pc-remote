using System.Net;
using System.Net.Sockets;
using PcRemote.Protocol;
using PcRemote.Transport;

internal static class DesktopTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        void Assert(bool condition) { if (!condition) throw new Exception("Desktop assertion failed."); }
        await check("desktop message bounds and input validation", () =>
        {
            foreach (var input in new[] { new RemoteInput(RemoteInputKind.Move, 65535, 65535), new RemoteInput(RemoteInputKind.KeyDown, Key: 65),
                new RemoteInput(RemoteInputKind.KeyUp, Key: 65), new RemoteInput(RemoteInputKind.Wheel, Delta: -120), new RemoteInput(RemoteInputKind.ReleaseAll) })
                DesktopMessages.Validate(input);
            foreach (var input in new[] { new RemoteInput((RemoteInputKind)999), new RemoteInput(RemoteInputKind.Move, -1, 0),
                new RemoteInput(RemoteInputKind.Move, 65536, 0), new RemoteInput(RemoteInputKind.KeyDown, Key: 0),
                new RemoteInput(RemoteInputKind.KeyUp, Key: 255), new RemoteInput(RemoteInputKind.Wheel, Delta: 1) })
            {
                try { DesktopMessages.Validate(input); }
                catch (InvalidDataException) { continue; }
                throw new Exception("Accepted invalid input.");
            }
            return Task.CompletedTask;
        });
        await check("bounded screen dimensions and JPEG payload", () =>
        {
            DesktopMessages.Validate(new DesktopImage(1280, 720, new byte[100]));
            foreach (var image in new[] { new DesktopImage(0, 720, new byte[100]), new DesktopImage(1921, 1080, new byte[100]),
                new DesktopImage(1280, 720, []), new DesktopImage(1280, 720, new byte[DesktopMessages.MaxJpegBytes + 1]) })
            {
                try { DesktopMessages.Validate(image); }
                catch (InvalidDataException) { continue; }
                throw new Exception("Accepted invalid image.");
            }
            try { DesktopMessages.Validate(new SessionNotice(new string('a', 257))); }
            catch (InvalidDataException) { return Task.CompletedTask; }
            throw new Exception("Accepted oversized notice.");
        });
        await check("desktop envelopes preserve typed input payload", async () =>
        {
            using var stream = new MemoryStream();
            var input = new RemoteInput(RemoteInputKind.LeftDown);
            await FrameCodec.WriteAsync(stream, DesktopMessages.Message("Input", input));
            stream.Position = 0;
            var received = await FrameCodec.ReadAsync(stream);
            Assert(received.Type == "Input" && DesktopMessages.Read<RemoteInput>(received) == input);
        });
        await check("file wire rejects oversized blocks and unsafe directory metadata", () =>
        {
            foreach (var block in new[] { new FileBlock(-1, new byte[1]), new FileBlock(0, new byte[PcRemote.Contracts.FileTransferLimits.ChunkBytes + 1]) })
            {
                try { FileMessages.Validate(block); }
                catch (InvalidDataException) { continue; }
                throw new Exception("Accepted invalid wire block.");
            }
            foreach (var item in new[] { new RemoteFileItem("../escape", "../escape", false, 0),
                new RemoteFileItem("safe.txt", "../safe.txt", false, 0), new RemoteFileItem("CON", "CON", false, 0),
                new RemoteFileItem("one.txt", "two.txt", false, 0) })
            {
                try { FileMessages.Validate(new FileDirectory("", [item], false)); }
                catch (InvalidDataException) { continue; }
                throw new Exception("Accepted unsafe wire metadata.");
            }
            return Task.CompletedTask;
        });
        await check("real TLS 1.3 loopback transports versioned frames", async () =>
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var certificate = DesktopTls.CreateCertificate();
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task server = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(cancellation.Token);
                await using var tls = await DesktopTls.ServerAsync(peer.GetStream(), certificate, cancellation.Token);
                var request = await FrameCodec.ReadAsync(tls, cancellation.Token);
                Assert(request.Type == "Ping");
                await FrameCodec.WriteAsync(tls, DesktopMessages.Message("DesktopUnavailable", new SessionNotice("Test desktop unavailable")), cancellation.Token);
            }, cancellation.Token);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellation.Token);
            System.Net.Security.SslStream secured;
            try { secured = await DesktopTls.ClientAsync(client.GetStream(), cancellation.Token); }
            catch { await server; throw; }
            await using var securedLifetime = secured;
            await FrameCodec.WriteAsync(secured, DesktopMessages.Message("Ping", new { }), cancellation.Token);
            var reply = await FrameCodec.ReadAsync(secured, cancellation.Token);
            Assert(reply.Type == "DesktopUnavailable" && DesktopMessages.Read<SessionNotice>(reply).Text == "Test desktop unavailable");
            await server;
        });
        await check("TLS cancellation closes pending handshake", async () =>
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var peer = await listener.AcceptTcpClientAsync();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try { await DesktopTls.ClientAsync(tcp.GetStream(), cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            throw new Exception("TLS handshake ignored cancellation.");
        });
    }
}
