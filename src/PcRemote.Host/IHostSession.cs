using PcRemote.Contracts;

namespace PcRemote.Host;

public interface IHostSession : IAsyncDisposable
{
    SessionCapability AvailableCapabilities { get; }
    Task DisconnectAsync(CancellationToken cancellationToken);
}
