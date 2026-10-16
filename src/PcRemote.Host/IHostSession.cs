using PcRemote.Contracts;

namespace PcRemote.Host;

public interface IHostSession : IAsyncDisposable
{
    SessionPermission GrantedPermissions { get; }
    Task RevokeAsync(CancellationToken cancellationToken);
}
