namespace PcRemote.Service;

// Reserved boundary only; no Windows Service or IPC implementation is installed.
public interface IUserSessionBridge
{
    Task DisconnectAsync(Guid sessionId, CancellationToken cancellationToken);
}
