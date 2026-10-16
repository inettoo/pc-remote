using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using PcRemote.Protocol;
using PcRemote.Windows;

if (args.Length != 2) return 2;
string artifacts = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts")) + Path.DirectorySeparatorChar;
string host = Path.GetFullPath(args[0]), result = Path.GetFullPath(args[1]);
if (!host.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase) || !result.StartsWith(artifacts, StringComparison.OrdinalIgnoreCase)) return 2;
object report;
int exitCode = 0;
try
{
    using var identity = WindowsIdentity.GetCurrent();
    if (!identity.IsSystem) throw new InvalidOperationException("Agent probe requires the CI LocalSystem task.");
    uint session = Native.WTSGetActiveConsoleSessionId();
    if (session is 0 or uint.MaxValue)
    {
        report = new { status = "skipped", reason = "No active console user on this runner." };
    }
    else
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var user = DesktopUserContext.OpenActiveConsole();
        string name = "PcRemote-" + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(user.UserSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        await using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, security);
        using var agent = user.Launch(host, name, Environment.ProcessId);
        try
        {
            await pipe.WaitForConnectionAsync(cancellation.Token);
            if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint processId) || processId != agent.ProcessId)
                throw new IOException("Spawned agent PID did not match the pipe client.");
            var message = await FrameCodec.ReadAsync(pipe, cancellation.Token);
            if (message.Type == "ScreenFrame")
            {
                var image = DesktopMessages.Read<DesktopImage>(message);
                DesktopMessages.Validate(image);
                bool jpeg = image.Jpeg[0] == 255 && image.Jpeg[1] == 216;
                if (!jpeg) throw new IOException("Agent did not return a JPEG frame.");
                report = new { status = "passed", wts = true, pipe = true, screen = "captured", image.Width, image.Height };
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(image.Jpeg);
            }
            else if (message.Type == "DesktopUnavailable")
            {
                DesktopMessages.Validate(DesktopMessages.Read<SessionNotice>(message));
                report = new { status = "passed", wts = true, pipe = true, screen = "skipped-unavailable" };
            }
            else throw new IOException("Unexpected agent startup message.");
            // No keyboard/mouse messages are sent by this probe.
            await FrameCodec.WriteAsync(pipe, DesktopMessages.Message("Disconnect", new { }), cancellation.Token);
        }
        finally
        {
            await pipe.DisposeAsync();
            await agent.WaitForExitAsync();
        }
    }
}
catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1008)
{
    report = new { status = "skipped", reason = "No logged-in console user token on this runner." };
}
catch (Exception exception)
{
    report = new { status = "failed", error = exception.GetType().Name, reason = exception.Message };
    exitCode = 1;
}
await File.WriteAllTextAsync(result + ".new", JsonSerializer.Serialize(report));
File.Move(result + ".new", result, true);
return exitCode;

internal static class Native
{
    [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
}
