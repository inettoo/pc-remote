using System.IO.Pipes;
using PcRemote.Capture;
using PcRemote.Contracts;
using PcRemote.Host;
using PcRemote.Input;
using PcRemote.Protocol;

internal static class AgentTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("real agent pipe transfers synthetic frame and releases input on disconnect", async () =>
        {
            string name = "PcRemote-" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var capture = new FakeCapture();
            var input = new FakeInput();
            Task agent = DesktopAgent.RunAsync(name, (uint)Environment.ProcessId, capture, input, () => true);
            await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var frame = await FrameCodec.ReadAsync(server).WaitAsync(TimeSpan.FromSeconds(5));
            if (frame.Type != "ScreenFrame" || DesktopMessages.Read<DesktopImage>(frame).Width != 2) throw new Exception("Missing agent frame.");
            await FrameCodec.WriteAsync(server, DesktopMessages.Message("Input", new RemoteInput(RemoteInputKind.KeyDown, Key: 65)));
            await FrameCodec.WriteAsync(server, DesktopMessages.Message("Disconnect", new { }));
            await agent.WaitAsync(TimeSpan.FromSeconds(5));
            if (input.Applied != 1 || input.Released != 1 || capture.Calls < 1) throw new Exception("Agent lifecycle failed.");
        });
        await check("agent pipe rejects unknown commands before input adapter", async () =>
        {
            string name = "PcRemote-" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var input = new FakeInput();
            Task agent = DesktopAgent.RunAsync(name, (uint)Environment.ProcessId, new FakeCapture(), input, () => true);
            await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await FrameCodec.ReadAsync(server).WaitAsync(TimeSpan.FromSeconds(5));
            await FrameCodec.WriteAsync(server, DesktopMessages.Message("UnsupportedCommand", new { }));
            try { await agent.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (InvalidDataException)
            {
                if (input.Applied != 0 || input.Released != 1) throw new Exception("Untrusted command reached input.");
                return;
            }
            throw new Exception("Agent accepted unknown command.");
        });
        await check("agent pipe rejects invalid key before input adapter", async () =>
        {
            string name = "PcRemote-" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var input = new FakeInput();
            Task agent = DesktopAgent.RunAsync(name, (uint)Environment.ProcessId, new FakeCapture(), input, () => true);
            await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await FrameCodec.ReadAsync(server).WaitAsync(TimeSpan.FromSeconds(5));
            await FrameCodec.WriteAsync(server, DesktopMessages.Message("Input", new RemoteInput(RemoteInputKind.KeyDown, Key: 999)));
            try { await agent.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (InvalidDataException)
            {
                if (input.Applied != 0 || input.Released != 1) throw new Exception("Invalid key reached input.");
                return;
            }
            throw new Exception("Agent accepted invalid key.");
        });
        await check("agent verifies native pipe server PID", async () =>
        {
            string name = "PcRemote-" + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var input = new FakeInput();
            Task agent = DesktopAgent.RunAsync(name, checked((uint)Environment.ProcessId + 1), new FakeCapture(), input, () => true);
            await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
            try { await agent.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (IOException)
            {
                if (input.Applied != 0) throw new Exception("Input occurred before PID verification.");
                return;
            }
            throw new Exception("Agent accepted wrong server PID.");
        });
    }

    private sealed class FakeCapture : IScreenCapture
    {
        public int Calls;
        public Task<ScreenFrame> CaptureAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Calls);
            // Protocol bytes only: this test never captures a real desktop or decodes an image.
            return Task.FromResult(new ScreenFrame(2, 2, new byte[] { 255, 216, 0, 0, 255, 217 }));
        }
    }
    private sealed class FakeInput : IInputSink
    {
        public int Applied, Released;
        public Task ApplyAsync(RemoteInput input, CancellationToken cancellationToken) { Interlocked.Increment(ref Applied); return Task.CompletedTask; }
        public Task ReleaseAllAsync(CancellationToken cancellationToken) { Interlocked.Increment(ref Released); return Task.CompletedTask; }
    }
}
