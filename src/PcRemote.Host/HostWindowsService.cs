using System.Diagnostics;
using System.ServiceProcess;
using PcRemote.Transport;
using PcRemote.Windows;

namespace PcRemote.Host;

internal sealed class HostWindowsService : ServiceBase
{
    private CancellationTokenSource? lifetime;
    private Task? worker;

    public HostWindowsService()
    {
        ServiceName = HostInstaller.ServiceName;
        CanStop = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        lifetime = new CancellationTokenSource();
        worker = RunAsync(lifetime.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await new HostDiscoveryListener(new WindowsNetworkEnvironment(), Environment.MachineName).RunAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                EventLog.WriteEntry($"LAN discovery failed: {exception.Message}", EventLogEntryType.Error);
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            }
        }
    }

    protected override void OnStop()
    {
        lifetime?.Cancel();
        if (worker is not null && !worker.Wait(TimeSpan.FromSeconds(10)))
            throw new System.TimeoutException("The host worker did not stop within 10 seconds.");
        lifetime?.Dispose();
    }
}
