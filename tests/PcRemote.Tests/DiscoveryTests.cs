using System.Net;
using System.Text;
using System.Threading.Channels;
using PcRemote.Contracts;
using PcRemote.Controller;
using PcRemote.Protocol;
using PcRemote.Transport;
using PcRemote.Windows;

internal static class DiscoveryTests
{
    private static readonly LanAdapter Adapter = new("test", "LAN", IPAddress.Parse("192.168.12.10"), 24, 7);

    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        void Assert(bool condition) { if (!condition) throw new Exception("Discovery assertion failed."); }
        await check("private IPv4 boundaries", () =>
        {
            foreach (string allowed in new[] { "10.0.0.1", "172.16.0.1", "172.31.255.254", "192.168.0.1" })
                Assert(LanNetwork.IsPrivate(IPAddress.Parse(allowed)));
            foreach (string denied in new[] { "8.8.8.8", "127.0.0.1", "169.254.1.1", "172.15.0.1", "172.32.0.1", "::1", "::ffff:192.168.12.1" })
                Assert(!LanNetwork.IsPrivate(IPAddress.Parse(denied)));
            return Task.CompletedTask;
        });
        await check("same subnet excludes self, network and broadcast", () =>
        {
            Assert(LanNetwork.IsPeer(Adapter, IPAddress.Parse("192.168.12.20")));
            foreach (string denied in new[] { "192.168.12.10", "192.168.12.0", "192.168.12.255", "192.168.13.1", "10.1.1.2" })
                Assert(!LanNetwork.IsPeer(Adapter, IPAddress.Parse(denied)));
            Assert(LanNetwork.Broadcast(Adapter).Equals(IPAddress.Parse("192.168.12.255")));
            Assert(!LanNetwork.IsUsableAdapter(Adapter with { PrefixLength = 31 }));
            Assert(!LanNetwork.IsUsableAdapter(Adapter with { PrefixLength = 0 }));
            Assert(!LanNetwork.IsUsableAdapter(Adapter with { InterfaceIndex = 0 }));
            Assert(!LanNetwork.IsUsableAdapter(Adapter with { Address = IPAddress.Parse("172.16.1.1"), PrefixLength = 8 }));
            return Task.CompletedTask;
        });
        await check("manual address uses canonical IPv4 only", () =>
        {
            Assert(LanNetwork.TryParsePeer(Adapter, "192.168.12.20", out _));
            foreach (string denied in new[] { "example.com", "3232238612", "192.168.012.20", "192.168.12.20:45831", " 192.168.12.20", "192.168.13.20" })
                Assert(!LanNetwork.TryParsePeer(Adapter, denied, out _));
            return Task.CompletedTask;
        });
        await check("discovery UTF-8 round trip", () =>
        {
            Guid id = Guid.NewGuid();
            var decoded = DiscoveryCodec.Decode(DiscoveryCodec.Encode(id, "工作电脑"));
            Assert(decoded.InstanceId == id && decoded.Name == "工作电脑");
            return Task.CompletedTask;
        });
        await check("discovery rejects malformed and incompatible packets", () =>
        {
            string valid = Encoding.UTF8.GetString(DiscoveryCodec.Encode(Guid.NewGuid(), "PC"));
            foreach (string invalid in new[] { "{}", "null", "{", valid.Replace("\"major\":2", "\"major\":3"),
                valid.Replace("\"minor\":0", "\"minor\":1"), valid.Replace("\"name\":\"PC\"", "\"name\":null"),
                valid.Replace("\"name\":\"PC\"", "\"name\":\"PC\",\"name\":\"other\""),
                valid.Replace("\"name\":\"PC\"", "\"name\":\"PC\",\"unknown\":1") })
            {
                try { DiscoveryCodec.Decode(Encoding.UTF8.GetBytes(invalid)); }
                catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException) { continue; }
                throw new Exception("Accepted invalid discovery JSON.");
            }
            foreach (byte[] invalid in new[] { Array.Empty<byte>(), new byte[513] })
            {
                try { DiscoveryCodec.Decode(invalid); }
                catch (InvalidDataException) { continue; }
                throw new Exception("Accepted invalid length.");
            }
            foreach (string invalid in new[] { "", "a\nb", new string('a', 65) })
            {
                try { DiscoveryCodec.Encode(Guid.NewGuid(), invalid); }
                catch (InvalidDataException) { continue; }
                throw new Exception("Accepted invalid name.");
            }
            return Task.CompletedTask;
        });
        await check("peer expiration, refresh and local echo", () =>
        {
            var peers = new PeerDirectory();
            Guid local = Guid.NewGuid();
            DateTimeOffset now = DateTimeOffset.UnixEpoch;
            var message = DiscoveryCodec.Decode(DiscoveryCodec.Encode(Guid.NewGuid(), "PC"));
            Assert(!peers.Observe(message with { InstanceId = local }, IPAddress.Parse("192.168.12.20"), local, now));
            Assert(peers.Observe(message, IPAddress.Parse("192.168.12.20"), local, now));
            Assert(peers.Snapshot(now.AddSeconds(6)).Count == 1);
            peers.Observe(message, IPAddress.Parse("192.168.12.20"), local, now.AddSeconds(6));
            Assert(peers.Snapshot(now.AddSeconds(12)).Count == 1);
            Assert(peers.Snapshot(now.AddSeconds(13)).Count == 0);
            return Task.CompletedTask;
        });
        await check("bounded peer registry", () =>
        {
            var peers = new PeerDirectory();
            Guid local = Guid.NewGuid();
            var message = DiscoveryCodec.Decode(DiscoveryCodec.Encode(Guid.NewGuid(), "PC"));
            for (int i = 1; i <= 129; i++)
                peers.Observe(message, IPAddress.Parse($"192.168.12.{i}"), local, DateTimeOffset.UnixEpoch);
            Assert(peers.Snapshot(DateTimeOffset.UnixEpoch).Count == 128);
            return Task.CompletedTask;
        });
        await check("discovery filters wrong interface, off-link, malformed and truncated packets", async () =>
        {
            var environment = new FakeEnvironment(Adapter);
            var factory = new FakeChannels();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var progress = new InlineProgress(snapshot =>
            {
                if (snapshot.Count == 0) return;
                Assert(snapshot.Count == 1 && snapshot[0].Address.Equals(IPAddress.Parse("192.168.12.20")));
                seen.TrySetResult();
                cancellation.Cancel();
            });
            byte[] data = DiscoveryCodec.Encode(Guid.NewGuid(), "Peer");
            factory.Channel.Enqueue(new(data, IPAddress.Parse("192.168.12.21"), 99));
            factory.Channel.Enqueue(new(data, IPAddress.Parse("8.8.8.8"), 7));
            factory.Channel.Enqueue(new(data, IPAddress.Parse("192.168.13.21"), 7));
            factory.Channel.Enqueue(new(data, IPAddress.Parse("192.168.12.22"), 7)); // route denied
            factory.Channel.Enqueue(new(Encoding.UTF8.GetBytes("{"), IPAddress.Parse("192.168.12.21"), 7));
            factory.Channel.Enqueue(new(data, IPAddress.Parse("192.168.12.21"), 7, true));
            factory.Channel.Enqueue(new(data, IPAddress.Parse("192.168.12.20"), 7));
            try { await new UdpDeviceDiscovery(environment, factory).RunAsync(Adapter, progress, cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Assert(seen.Task.IsCompletedSuccessfully && factory.Channel.Disposed && factory.Channel.SendCount == 1);
        });
        await check("adapter disappearance cancels discovery and releases channel", async () =>
        {
            var environment = new FakeEnvironment(Adapter) { RemoveAfterFirstRead = true };
            var factory = new FakeChannels();
            try
            {
                await new UdpDeviceDiscovery(environment, factory)
                    .RunAsync(Adapter, new InlineProgress(_ => { }), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (IOException)
            {
                Assert(factory.Channel.Disposed && factory.Channel.SendCount == 0);
                return;
            }
            throw new Exception("Changed profile was accepted.");
        });
        await check("discovery cancellation before socket creation", async () =>
        {
            var factory = new FakeChannels();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            try { await new UdpDeviceDiscovery(new FakeEnvironment(Adapter), factory).RunAsync(Adapter, new InlineProgress(_ => { }), cts.Token); }
            catch (OperationCanceledException)
            {
                Assert(!factory.Opened);
                return;
            }
            throw new Exception("Expected cancellation.");
        });
        await check("controller starts discovery automatically without adapter selection", async () =>
        {
            var environment = new FakeEnvironment(Adapter);
            var factory = new FakeChannels();
            var vm = new DashboardViewModel(environment, new UdpDeviceDiscovery(environment, factory));
            await vm.StartAsync();
            Assert(factory.Opened);
            await vm.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert(factory.Channel.Disposed);
        });
        await check("host responds only to valid on-link probes", () =>
        {
            var responder = new DiscoveryResponder(new FakeEnvironment(Adapter), Guid.NewGuid(), "Host");
            Guid scanId = Guid.NewGuid();
            byte[] probe = DiscoveryCodec.EncodeProbe(scanId);
            var reply = responder.Reply(probe, IPAddress.Parse("192.168.12.20"), 7, [Adapter]);
            Assert(reply is not null && DiscoveryCodec.Decode(reply).ScanId == scanId);
            Assert(responder.Reply(probe, IPAddress.Parse("192.168.12.20"), 8, [Adapter]) is null);
            Assert(responder.Reply(probe, IPAddress.Parse("192.168.12.22"), 7, [Adapter]) is null);
            Assert(responder.Reply(probe, IPAddress.Parse("8.8.8.8"), 7, [Adapter]) is null);
            Assert(responder.Reply(DiscoveryCodec.Encode(Guid.NewGuid(), "Host"), IPAddress.Parse("192.168.12.20"), 7, [Adapter]) is null);
            return Task.CompletedTask;
        });
        await check("native UDP socket binds and cancels without network transmission", async () =>
        {
            var adapters = await new WindowsNetworkEnvironment().GetLanAdaptersAsync(CancellationToken.None);
            if (adapters.Count == 0) { Console.WriteLine("SKIP native UDP: no LAN adapter."); return; }
            using var channel = new UdpDiscoveryChannelFactory().Open(adapters[0]);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try { await channel.ReceiveAsync(cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
            throw new Exception("UDP receive did not cancel.");
        });
        await check("Windows network catalog and cancellation smoke", async () =>
        {
            var environment = new WindowsNetworkEnvironment();
            var adapters = await environment.GetLanAdaptersAsync(CancellationToken.None);
            Assert(adapters.All(LanNetwork.IsUsableAdapter));
            Assert(!environment.HasDirectRoute(Adapter, IPAddress.Parse("8.8.8.8")));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            try { await environment.GetLanAdaptersAsync(cts.Token); }
            catch (OperationCanceledException) { return; }
            throw new Exception("Catalog ignored cancellation.");
        });
    }

    private sealed class InlineProgress(Action<IReadOnlyList<DiscoveredDevice>> report) : IProgress<IReadOnlyList<DiscoveredDevice>>
    {
        public void Report(IReadOnlyList<DiscoveredDevice> value) => report(value);
    }

    private sealed class FakeEnvironment(LanAdapter adapter) : INetworkEnvironment
    {
        private int reads;
        public bool RemoveAfterFirstRead { get; init; }
        public Task<IReadOnlyList<LanAdapter>> GetLanAdaptersAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<LanAdapter>>(RemoveAfterFirstRead && ++reads > 1 ? [] : [adapter]);
        }
        public bool HasDirectRoute(LanAdapter selected, IPAddress peer) => !peer.Equals(IPAddress.Parse("192.168.12.22"));
    }

    private sealed class FakeChannels : IDiscoveryChannelFactory
    {
        public FakeChannel Channel { get; } = new();
        public bool Opened { get; private set; }
        public IDiscoveryChannel Open(LanAdapter adapter) { Opened = true; return Channel; }
    }

    private sealed class FakeChannel : IDiscoveryChannel
    {
        private readonly List<DiscoveryDatagram> queued = [];
        private readonly Channel<DiscoveryDatagram> incoming = System.Threading.Channels.Channel.CreateUnbounded<DiscoveryDatagram>();
        public bool Disposed { get; private set; }
        public int SendCount { get; private set; }
        public void Enqueue(DiscoveryDatagram packet) => queued.Add(packet);
        public ValueTask SendAsync(byte[] data, CancellationToken cancellationToken)
        {
            SendCount++;
            var probe = DiscoveryCodec.Decode(data);
            foreach (var packet in queued)
            {
                byte[] body = packet.Data;
                try
                {
                    var offer = DiscoveryCodec.Decode(body);
                    body = DiscoveryCodec.Encode(offer.InstanceId, offer.Name, probe.ScanId);
                }
                catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException) { }
                incoming.Writer.TryWrite(packet with { Data = body });
            }
            queued.Clear();
            return ValueTask.CompletedTask;
        }
        public ValueTask<DiscoveryDatagram> ReceiveAsync(CancellationToken cancellationToken) => incoming.Reader.ReadAsync(cancellationToken);
        public void Dispose() { Disposed = true; incoming.Writer.TryComplete(); }
    }
}
