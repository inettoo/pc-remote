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
                using var iteration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var environment = new WindowsNetworkEnvironment();
                Task discovery = new HostDiscoveryListener(environment, Environment.MachineName).RunAsync(iteration.Token);
                Task desktop = new DesktopSessionServer(environment,
                    exception => EventLog.WriteEntry($"Desktop session ended: {exception.Message}", EventLogEntryType.Warning)).RunAsync(iteration.Token);
                try { await await Task.WhenAny(discovery, desktop); }
                finally
                {
                    iteration.Cancel();
                    try { await Task.WhenAll(discovery, desktop); }
                    catch (OperationCanceledException) when (iteration.IsCancellationRequested) { }
                }
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
