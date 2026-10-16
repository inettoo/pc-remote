using System.Net;
using System.Text.Json;
using PcRemote.Contracts;
using PcRemote.Protocol;

namespace PcRemote.Transport;

public sealed class DiscoveryResponder(INetworkEnvironment environment, Guid instanceId, string displayName)
{
    public byte[]? Reply(ReadOnlySpan<byte> data, IPAddress source, int interfaceIndex, IReadOnlyList<LanAdapter> adapters)
    {
        var adapter = adapters.FirstOrDefault(candidate => candidate.InterfaceIndex == interfaceIndex && LanNetwork.IsPeer(candidate, source));
        if (adapter is null || !environment.HasDirectRoute(adapter, source)) return null;
        try
        {
            var message = DiscoveryCodec.Decode(data);
            return message.Kind == "probe" ? DiscoveryCodec.Encode(instanceId, displayName, message.ScanId) : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return null;
        }
    }
}
