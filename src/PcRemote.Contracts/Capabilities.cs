namespace PcRemote.Contracts;

[Flags]
public enum SessionPermission { None = 0, View = 1, Control = 2, Files = 4 }
public sealed record Device(Guid Id, string Name, string Address);
public sealed record ConnectionRequest(Guid SessionId, Device Controller, SessionPermission Requested);
public sealed record ScreenFrame(int Width, int Height, ReadOnlyMemory<byte> EncodedBytes);
public sealed record InputEvent(int VirtualKey, bool IsDown);
public sealed record FileEntry(string RelativePath, long Length, bool IsDirectory);
public interface IDeviceDiscovery
{
    IAsyncEnumerable<Device> DiscoverAsync(CancellationToken cancellationToken);
}
public interface ISessionAuthorization
{
    Task<SessionPermission> AuthorizeAsync(ConnectionRequest request, CancellationToken cancellationToken);
}
