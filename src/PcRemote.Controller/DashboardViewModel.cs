using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PcRemote.Contracts;

namespace PcRemote.Controller;

public sealed class DashboardViewModel(INetworkEnvironment environment, IDeviceDiscovery discovery) : INotifyPropertyChanged
{
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? cycle;
    private Task? monitor;
    private int generation;
    private string status = "正在自动寻找局域网中的被控电脑…";
    private string error = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<DiscoveredDevice> Devices { get; } = [];
    public string Version => "v0.0.4 · LAN Remote";
    public string Status { get => status; private set { status = value; Changed(); } }
    public string Error { get => error; private set { error = value; Changed(); } }

    public Task StartAsync()
    {
        monitor ??= MonitorAsync();
        return Task.CompletedTask;
    }

    private async Task MonitorAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            using var current = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            cycle = current;
            int currentGeneration = ++generation;
            try
            {
                Error = "";
                var adapters = await environment.GetLanAdaptersAsync(current.Token);
                if (adapters.Count == 0)
                {
                    Status = "等待局域网连接…";
                    await Task.Delay(TimeSpan.FromSeconds(3), current.Token);
                    continue;
                }
                var snapshots = new Dictionary<LanAdapter, IReadOnlyList<DiscoveredDevice>>();
                var workers = adapters.Select(adapter =>
                {
                    var progress = new Progress<IReadOnlyList<DiscoveredDevice>>(snapshot =>
                    {
                        if (generation != currentGeneration || current.IsCancellationRequested) return;
                        snapshots[adapter] = snapshot;
                        var devices = snapshots.Values.SelectMany(value => value).DistinctBy(value => value.Address).OrderBy(value => value.Name).ToArray();
                        Devices.Clear();
                        foreach (var device in devices) Devices.Add(device);
                        Status = devices.Length == 0 ? "正在寻找被控电脑，请在另一台电脑运行被控端 EXE。" : $"已发现 {devices.Length} 台被控电脑";
                    });
                    return Task.Run(() => discovery.RunAsync(adapter, progress, current.Token), current.Token);
                }).ToList();
                // Restart only when adapter membership changes, preserving stable device rows.
                workers.Add(WatchAdaptersAsync(adapters, current.Token));
                try { await await Task.WhenAny(workers); }
                finally
                {
                    current.Cancel();
                    try { await Task.WhenAll(workers); }
                    catch (OperationCanceledException) when (current.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (current.IsCancellationRequested) { }
            catch (Exception exception)
            {
                Error = $"自动发现暂时失败：{exception.Message}";
                Status = "正在重试局域网发现…";
            }
            finally
            {
                generation++;
                cycle = null;
                Devices.Clear();
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }

    private async Task WatchAdaptersAsync(IReadOnlyList<LanAdapter> adapters, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var current = await environment.GetLanAdaptersAsync(cancellationToken);
            if (!adapters.ToHashSet().SetEquals(current)) return;
        }
    }

    public async Task StopAsync()
    {
        lifetime.Cancel();
        cycle?.Cancel();
        if (monitor is not null) await monitor;
    }

    private void Changed([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
