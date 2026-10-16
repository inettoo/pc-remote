using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PcRemote.Capture;
using PcRemote.Input;
using PcRemote.Protocol;

namespace PcRemote.Host;

internal static class DesktopAgent
{
    public static async Task RunAsync(string pipeName, uint serviceProcessId, IScreenCapture? captureOverride = null, IInputSink? inputOverride = null, Func<bool>? desktopReady = null)
    {
        if (!pipeName.StartsWith("PcRemote-", StringComparison.Ordinal) || pipeName.Length != 41)
            throw new InvalidDataException("Invalid desktop pipe name.");
        using var lifetime = new CancellationTokenSource();
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000, lifetime.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint serverId) || serverId != serviceProcessId)
            throw new IOException("Desktop pipe server mismatch.");
        IInputSink input = inputOverride ?? new WindowsInput();
        IScreenCapture capture = captureOverride ?? new PrimaryScreenCapture();
        desktopReady ??= WindowsInput.IsDefaultDesktop;
        bool wasUnavailable = false;
        async Task FramesAsync()
        {
            while (true)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                if (!desktopReady())
                {
                    await input.ReleaseAllAsync(CancellationToken.None);
                    wasUnavailable = true;
                    await FrameCodec.WriteAsync(pipe, DesktopMessages.Message("DesktopUnavailable", new SessionNotice("桌面已锁定或处于安全桌面，等待恢复。")), lifetime.Token);
                    await Task.Delay(1000, lifetime.Token);
                    continue;
                }
                if (wasUnavailable) { await input.ReleaseAllAsync(CancellationToken.None); wasUnavailable = false; }
                try
                {
                    var frame = await capture.CaptureAsync(lifetime.Token);
                    var image = new DesktopImage(frame.Width, frame.Height, frame.EncodedBytes.ToArray());
                    DesktopMessages.Validate(image);
                    await FrameCodec.WriteAsync(pipe, DesktopMessages.Message("ScreenFrame", image), lifetime.Token);
                }
                catch (Win32Exception)
                {
                    await FrameCodec.WriteAsync(pipe, DesktopMessages.Message("DesktopUnavailable", new SessionNotice("当前桌面暂不可采集。")), lifetime.Token);
                }
                await Task.Delay(100, lifetime.Token);
            }
        }

        async Task InputAsync()
        {
            while (true)
            {
                var message = await FrameCodec.ReadAsync(pipe, lifetime.Token);
                if (message.Type == "Disconnect") return;
                if (message.Type != "Input") throw new InvalidDataException("Unexpected desktop input message.");
                var value = DesktopMessages.Read<RemoteInput>(message);
                DesktopMessages.Validate(value);
                await input.ApplyAsync(value, lifetime.Token);
            }
        }

        Task frames = FramesAsync(), events = InputAsync();
        try { await await Task.WhenAny(frames, events); }
        catch (EndOfStreamException) { /* Service ended this user-session agent. */ }
        finally
        {
            lifetime.Cancel();
            try { await Task.WhenAll(frames, events); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (EndOfStreamException) { }
            finally { await input.ReleaseAllAsync(CancellationToken.None); }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
