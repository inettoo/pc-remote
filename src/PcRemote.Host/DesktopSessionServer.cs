using System.ComponentModel;
using System.IO.Pipes;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using PcRemote.Contracts;
using PcRemote.Protocol;
using PcRemote.Transport;
using PcRemote.Windows;

namespace PcRemote.Host;

internal sealed class DesktopSessionServer(INetworkEnvironment environment, Action<Exception> reportError)
{
    private int active;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var certificate = DesktopTls.CreateCertificate();
        using var listener = new TcpListener(IPAddress.Any, DesktopMessages.Port);
        listener.Start(4);
        var sessions = new List<Task>();
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                if (Interlocked.CompareExchange(ref active, 1, 0) != 0) { client.Dispose(); continue; }
                Task session = HandleSafelyAsync(client, certificate, cancellationToken);
                sessions.RemoveAll(task => task.IsCompleted);
                sessions.Add(session);
            }
        }
        finally
        {
            listener.Stop();
            await Task.WhenAll(sessions);
        }
    }

    private async Task HandleSafelyAsync(TcpClient client, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                var local = ((IPEndPoint)client.Client.LocalEndPoint!).Address;
                var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                var adapters = await environment.GetLanAdaptersAsync(cancellationToken);
                var adapter = adapters.FirstOrDefault(candidate => candidate.Address.Equals(local) && LanNetwork.IsPeer(candidate, remote));
                if (adapter is null || !environment.HasDirectRoute(adapter, remote)) return;
                client.NoDelay = true;
                client.SendBufferSize = 64 * 1024;
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshake.CancelAfter(TimeSpan.FromSeconds(10));
                await using var tls = await DesktopTls.ServerAsync(client.GetStream(), certificate, handshake.Token);
                using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                await BridgeDesktopAsync(tls, adapter, remote, session);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                try { reportError(exception); }
                catch (Exception logError) { System.Diagnostics.Trace.TraceError($"Session and event logging failed: {exception.Message}; {logError.Message}"); }
            }
            finally { Interlocked.Exchange(ref active, 0); }
        }
    }

    private async Task BridgeDesktopAsync(SslStream tls, LanAdapter adapter, IPAddress remote, CancellationTokenSource lifetime)
    {
        using var user = DesktopUserContext.OpenActiveConsole();
        string pipeName = "PcRemote-" + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(user.UserSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, security);
        string executable = Environment.ProcessPath ?? throw new IOException("Missing host executable.");
        // Fixed executable and generated pipe name; never launch a path or command supplied by the network.
        using var agent = user.Launch(executable, pipeName, Environment.ProcessId);
        try
        {
            await pipe.WaitForConnectionAsync(lifetime.Token).WaitAsync(TimeSpan.FromSeconds(10), lifetime.Token);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint processId) || processId != agent.ProcessId)
                throw new IOException("Unexpected desktop agent process.");

            async Task FromDesktopAsync()
            {
                while (true)
                {
                    var message = await FrameCodec.ReadAsync(pipe, lifetime.Token).WaitAsync(TimeSpan.FromSeconds(10), lifetime.Token);
                    if (message.Type is not ("ScreenFrame" or "DesktopUnavailable")) throw new InvalidDataException("Unexpected agent message.");
                    if (message.Type == "ScreenFrame") DesktopMessages.Validate(DesktopMessages.Read<DesktopImage>(message));
                    else DesktopMessages.Validate(DesktopMessages.Read<SessionNotice>(message));
                    await FrameCodec.WriteAsync(tls, message, lifetime.Token);
                }
            }

            async Task ToDesktopAsync()
            {
                while (true)
                {
                    var message = await FrameCodec.ReadAsync(tls, lifetime.Token).WaitAsync(TimeSpan.FromSeconds(10), lifetime.Token);
                    if (message.Type == "Disconnect") return;
                    if (message.Type == "Ping") continue;
                    if (message.Type != "Input") throw new InvalidDataException("Unexpected controller message.");
                    DesktopMessages.Validate(DesktopMessages.Read<RemoteInput>(message));
                    await FrameCodec.WriteAsync(pipe, message, lifetime.Token);
                }
            }

            async Task CheckLinkAsync()
            {
                while (true)
                {
                    await Task.Delay(2000, lifetime.Token);
                    if (!user.IsActive || !(await environment.GetLanAdaptersAsync(lifetime.Token)).Contains(adapter) || !environment.HasDirectRoute(adapter, remote))
                        throw new IOException("LAN route changed.");
                }
            }

            Task frames = FromDesktopAsync(), input = ToDesktopAsync(), link = CheckLinkAsync();
            try { await await Task.WhenAny(frames, input, link); }
            finally
            {
                lifetime.Cancel();
                try { await Task.WhenAll(frames, input, link); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }
        }
        finally
        {
            lifetime.Cancel();
            await pipe.DisposeAsync();
            await agent.WaitForExitAsync();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
}
