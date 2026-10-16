using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using PcRemote.Contracts;
using PcRemote.Transport;

namespace PcRemote.Windows;

public sealed class WindowsNetworkEnvironment : INetworkEnvironment
{
    public Task<IReadOnlyList<LanAdapter>> GetLanAdaptersAsync(CancellationToken cancellationToken)
        => Task.Run<IReadOnlyList<LanAdapter>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new List<LanAdapter>();
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!network.Supports(NetworkInterfaceComponent.IPv4) || network.OperationalStatus != OperationalStatus.Up
                    || network.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)) continue;
                var properties = network.GetIPProperties();
                int index = properties.GetIPv4Properties()?.Index ?? 0;
                foreach (var address in properties.UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var adapter = new LanAdapter(network.Id, network.Name, address.Address, address.PrefixLength, index);
                    if (LanNetwork.IsUsableAdapter(adapter)) result.Add(adapter);
                }
            }
            return result;
        }, cancellationToken);

    public bool HasDirectRoute(LanAdapter adapter, IPAddress destination)
    {
        if (!LanNetwork.IsPeer(adapter, destination)) return false;
        return GetBestRoute(BitConverter.ToUInt32(destination.GetAddressBytes()),
            BitConverter.ToUInt32(adapter.Address.GetAddressBytes()), out var route) == 0
            && route.InterfaceIndex == adapter.InterfaceIndex && route.NextHop == 0 && route.Type == 3;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestRoute(uint destination, uint source, out RouteRow route);

    [StructLayout(LayoutKind.Sequential)]
    private struct RouteRow
    {
        public uint Destination, Mask, Policy, NextHop;
        public int InterfaceIndex;
        public uint Type, Protocol, Age, NextHopAs, Metric1, Metric2, Metric3, Metric4, Metric5;
    }
}

