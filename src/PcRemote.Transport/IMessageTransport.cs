using PcRemote.Protocol;

namespace PcRemote.Transport;

public interface IMessageTransport : IAsyncDisposable
{
    Task SendAsync(Envelope message, CancellationToken cancellationToken);
    Task<Envelope> ReceiveAsync(CancellationToken cancellationToken);
}
