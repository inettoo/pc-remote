using System.Net;

namespace PcRemote.Contracts;

public sealed record LanAdapter(string Id, string Name, IPAddress Address, int PrefixLength, int InterfaceIndex)
{
    public string DisplayName => $"{Name} · {Address}/{PrefixLength}";
}

public sealed record DiscoveredDevice(Guid Id, string Name, IPAddress Address, DateTimeOffset LastSeen)
{
    public string State => "已发现 · 远控尚未开放";
}

public interface INetworkEnvironment
{
    Task<IReadOnlyList<LanAdapter>> GetLanAdaptersAsync(CancellationToken cancellationToken);
    bool HasDirectRoute(LanAdapter adapter, IPAddress destination);
}

public interface IDeviceDiscovery
{
    Task RunAsync(LanAdapter adapter, IProgress<IReadOnlyList<DiscoveredDevice>> progress, CancellationToken cancellationToken);
}
