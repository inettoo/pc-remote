using System.Net.Sockets;
using System.Text.Json;
using PcRemote.Contracts;
using PcRemote.Protocol;

namespace PcRemote.Transport;

public sealed class UdpDeviceDiscovery(
    INetworkEnvironment environment,
    IDiscoveryChannelFactory channels,
    TimeProvider? timeProvider = null) : IDeviceDiscovery
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async Task RunAsync(LanAdapter adapter, IProgress<IReadOnlyList<DiscoveredDevice>> progress, CancellationToken cancellationToken)
    {
        await EnsureCurrentAsync(adapter, cancellationToken);
        Guid instanceId = Guid.NewGuid();
        byte[] announcement = DiscoveryCodec.EncodeProbe(instanceId);
        using var channel = channels.Open(adapter);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var peers = new PeerDirectory();
        object gate = new();
        long budgetWindow = time.GetTimestamp();
        int packetCount = 0;

        async Task AnnounceAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), time);
            do
            {
                await EnsureCurrentAsync(adapter, lifetime.Token);
                await channel.SendAsync(announcement, lifetime.Token);
                lock (gate) progress.Report(peers.Snapshot(time.GetUtcNow()));
            } while (await timer.WaitForNextTickAsync(lifetime.Token));
        }

        async Task ReceiveAsync()
        {
            while (true)
            {
                DiscoveryDatagram packet = await channel.ReceiveAsync(lifetime.Token);
                if (time.GetElapsedTime(budgetWindow) >= TimeSpan.FromSeconds(1))
                {
                    budgetWindow = time.GetTimestamp();
                    packetCount = 0;
                }
                if (++packetCount > 64)
                {
                    await Task.Delay(10, lifetime.Token);
                    continue;
                }
                if (packet.Truncated || packet.InterfaceIndex != adapter.InterfaceIndex
                    || !LanNetwork.IsPeer(adapter, packet.Source) || !environment.HasDirectRoute(adapter, packet.Source)) continue;
                try
                {
                    var decoded = DiscoveryCodec.Decode(packet.Data);
                    if (decoded.Kind != "host" || decoded.ScanId != instanceId) continue;
                    lock (gate)
                    {
                        if (peers.Observe(decoded, packet.Source, instanceId, time.GetUtcNow()))
                            progress.Report(peers.Snapshot(time.GetUtcNow()));
                    }
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException)
                {
                    // Untrusted malformed datagrams are expected and never become device rows.
                }
            }
        }

        Task announce = AnnounceAsync(), receive = ReceiveAsync();
        try
        {
            Task completed = await Task.WhenAny(announce, receive);
            await completed;
        }
        finally
        {
            lifetime.Cancel();
            // Observe both tasks and dispose sockets even when one worker faults.
            try { await Task.WhenAll(announce, receive); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }

    public async Task EnsureCurrentAsync(LanAdapter adapter, CancellationToken cancellationToken)
    {
        if (!LanNetwork.IsUsableAdapter(adapter)
            || !(await environment.GetLanAdaptersAsync(cancellationToken)).Contains(adapter))
            throw new IOException("局域网连接已变化，正在重新识别。");
    }
}
