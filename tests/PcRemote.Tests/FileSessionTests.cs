using System.IO.Pipes;
using System.Net;
using PcRemote.Capture;
using PcRemote.Contracts;
using PcRemote.Controller;
using PcRemote.FileService;
using PcRemote.Host;
using PcRemote.Input;
using PcRemote.Protocol;
using PcRemote.Transport;

internal static class FileSessionTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        string testBase = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "work", "session-file-tests"));
        string workspace = Path.GetFullPath(Path.Combine(testBase, Guid.NewGuid().ToString("N")));
        if (!workspace.StartsWith(testBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test path.");
        Directory.CreateDirectory(workspace);
        string shared = Path.Combine(workspace, "shared");
        Directory.CreateDirectory(shared);
        await using var session = new TestSession(shared);
        try
        {
            await session.StartAsync();
            byte[] content = System.Security.Cryptography.RandomNumberGenerator.GetBytes(300_000);
            string source = Path.Combine(workspace, "source.bin");
            await File.WriteAllBytesAsync(source, content);
            await check("client-agent file upload, listing and download round trip over real pipe", async () =>
            {
                await session.Client.UploadFileAsync(source, "uploaded.bin", new InlineProgress(_ => { }), CancellationToken.None);
                var listing = await session.Client.ListFilesAsync("", CancellationToken.None);
                if (!listing.Items.Any(item => item.Name == "uploaded.bin" && item.Length == content.Length)) throw new Exception("Uploaded file not listed.");
                string target = Path.Combine(workspace, "download.bin");
                await session.Client.DownloadFileAsync("uploaded.bin", target, false, new InlineProgress(_ => { }), CancellationToken.None);
                byte[] downloaded = await File.ReadAllBytesAsync(target);
                if (!content.SequenceEqual(downloaded)) throw new Exception("Round trip content mismatch.");
            });
            await check("client cancellation cleans remote partial upload and keeps session usable", async () =>
            {
                using var cancellation = new CancellationTokenSource();
                try
                {
                    await session.Client.UploadFileAsync(source, "cancelled.bin", new InlineProgress(value =>
                    {
                        if (value.Completed >= FileTransferLimits.ChunkBytes) cancellation.Cancel();
                    }), cancellation.Token);
                    throw new Exception("Transfer did not cancel.");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                await session.Client.ListFilesAsync("", CancellationToken.None);
                if (File.Exists(Path.Combine(shared, "cancelled.bin")) || Directory.GetFiles(shared, "*.partial").Length != 0)
                    throw new Exception("Canceled remote partial leaked.");
            });
            await check("client rejects corrupt download receipt without exposing partial destination", async () =>
            {
                session.Connector.CorruptNextReceipt = true;
                string target = Path.Combine(workspace, "corrupt.bin");
                try
                {
                    await session.Client.DownloadFileAsync("uploaded.bin", target, false, new InlineProgress(_ => { }), CancellationToken.None);
                    throw new Exception("Corrupt receipt was accepted.");
                }
                catch (InvalidDataException) { }
                if (File.Exists(target) || Directory.GetFiles(workspace, "*.partial").Length != 0) throw new Exception("Corrupt destination leaked.");
                await session.Client.ListFilesAsync("", CancellationToken.None);
            });
            await check("remote upload name conflict never replaces original content", async () =>
            {
                try
                {
                    await session.Client.UploadFileAsync(source, "uploaded.bin", new InlineProgress(_ => { }), CancellationToken.None);
                    throw new Exception("Name conflict was accepted.");
                }
                catch (IOException) { }
                byte[] original = await File.ReadAllBytesAsync(Path.Combine(shared, "uploaded.bin"));
                if (!content.SequenceEqual(original)) throw new Exception("Original file changed.");
            });
        }
        finally
        {
            await session.DisposeAsync();
            string resolved = Path.GetFullPath(workspace);
            if (!resolved.StartsWith(testBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new Exception("Unsafe test cleanup.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class TestSession : IAsyncDisposable
    {
        private readonly string pipeName = "PcRemote-" + Guid.NewGuid().ToString("N");
        private readonly NamedPipeServerStream pipe;
        private readonly ISharedFiles files;
        private Task? agent;
        private bool disposed;
        public TestConnector Connector { get; }
        public RemoteDesktopClient Client { get; }

        public TestSession(string shared)
        {
            pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 64 * 1024, 64 * 1024);
            Connector = new TestConnector(pipe);
            Client = new RemoteDesktopClient(new TestEnvironment(), Connector);
            files = new RootedFiles(shared);
        }

        public async Task StartAsync()
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            agent = DesktopAgent.RunAsync(pipeName, (uint)Environment.ProcessId, new TestCapture(), new TestInput(), () => true, files);
            _ = Client.RunAsync(IPAddress.Parse("192.168.88.20"), update => { if (update.Image is not null) ready.TrySetResult(); });
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            await Client.DisposeAsync();
            if (agent is not null) await agent.WaitAsync(TimeSpan.FromSeconds(5));
            await pipe.DisposeAsync();
        }
    }

    internal sealed class TestConnector(NamedPipeServerStream pipe) : IDesktopConnector
    {
        public volatile bool CorruptNextReceipt;
        public async Task<IDesktopConnection> ConnectAsync(LanAdapter adapter, IPAddress remote, CancellationToken cancellationToken)
        {
            await pipe.WaitForConnectionAsync(cancellationToken);
            return new PipeConnection(pipe, this);
        }
    }

    private sealed class PipeConnection(NamedPipeServerStream pipe, TestConnector connector) : IDesktopConnection
    {
        // The real service bridge consumes heartbeat messages before forwarding input to its agent.
        public Task SendAsync(Envelope message, CancellationToken cancellationToken) => message.Type == "Ping"
            ? Task.CompletedTask : FrameCodec.WriteAsync(pipe, message, cancellationToken);
        public async Task<Envelope> ReceiveAsync(CancellationToken cancellationToken)
        {
            var message = await FrameCodec.ReadAsync(pipe, cancellationToken);
            if (message.Type == "FileComplete" && connector.CorruptNextReceipt)
            {
                connector.CorruptNextReceipt = false;
                var receipt = DesktopMessages.Read<FileReceipt>(message);
                return DesktopMessages.Message("FileComplete", receipt with { Sha256 = new string('0', 64) }, message.RequestId);
            }
            return message;
        }
        public ValueTask DisposeAsync() => pipe.DisposeAsync();
    }

    private sealed class TestEnvironment : INetworkEnvironment
    {
        private static readonly LanAdapter Adapter = new("test", "test", IPAddress.Parse("192.168.88.10"), 24, 7);
        public Task<IReadOnlyList<LanAdapter>> GetLanAdaptersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LanAdapter>>([Adapter]);
        public bool HasDirectRoute(LanAdapter adapter, IPAddress destination) => true;
    }
    private sealed class InlineProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }
    private sealed class TestCapture : IScreenCapture
    {
        public Task<ScreenFrame> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(new ScreenFrame(2, 2, new byte[] { 255, 216, 0, 0, 255, 217 }));
    }
    private sealed class TestInput : IInputSink
    {
        public Task ApplyAsync(RemoteInput input, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReleaseAllAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
