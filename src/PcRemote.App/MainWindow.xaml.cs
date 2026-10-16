using System.Windows;
using PcRemote.Controller;
using PcRemote.Transport;
using PcRemote.Windows;

namespace PcRemote.App;

public partial class MainWindow : Window
{
    private readonly DashboardViewModel dashboard;
    private readonly WindowsNetworkEnvironment environment;
    private RemoteWindow? remote;

    public MainWindow()
    {
        InitializeComponent();
        environment = new WindowsNetworkEnvironment();
        dashboard = new DashboardViewModel(environment,
            new UdpDeviceDiscovery(environment, new UdpDiscoveryChannelFactory()));
        DataContext = dashboard;
        Loaded += async (_, _) => await dashboard.StartAsync();
        Closed += async (_, _) => { remote?.Close(); await dashboard.StopAsync(); };
    }
    private void ConnectClick(object sender, RoutedEventArgs args)
    {
        if (remote is not null) { remote.Activate(); return; }
        if (sender is not System.Windows.Controls.Button { DataContext: PcRemote.Contracts.DiscoveredDevice device }) return;
        remote = new RemoteWindow(new RemoteDesktopClient(environment), device);
        remote.Closed += (_, _) => remote = null;
        remote.Show();
    }
}
