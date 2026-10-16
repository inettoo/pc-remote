using System.Net;
using PcRemote.Contracts;
using PcRemote.Protocol;

namespace PcRemote.Transport;

public sealed class PeerDirectory
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(7);
    public const int Capacity = 128;
    private readonly Dictionary<IPAddress, DiscoveredDevice> devices = [];

    public bool Observe(DiscoveryAnnouncement announcement, IPAddress source, Guid localId, DateTimeOffset now)
    {
        if (announcement.InstanceId == localId) return false;
        if (!devices.ContainsKey(source) && devices.Count >= Capacity) return false;
        devices[source] = new DiscoveredDevice(announcement.InstanceId, announcement.Name, source, now);
        return true;
    }

    public IReadOnlyList<DiscoveredDevice> Snapshot(DateTimeOffset now)
    {
        foreach (var key in devices.Where(pair => now - pair.Value.LastSeen >= Lifetime).Select(pair => pair.Key).ToArray())
            devices.Remove(key);
        return devices.Values.OrderBy(device => device.Name, StringComparer.Ordinal).ThenBy(device => device.Address.ToString(), StringComparer.Ordinal).ToArray();
    }
}
