using System.Windows;
using PcRemote.Controller;
using PcRemote.Transport;
using PcRemote.Windows;

namespace PcRemote.App;

public partial class MainWindow : Window
{
    private readonly DashboardViewModel dashboard;

    public MainWindow()
    {
        InitializeComponent();
        var environment = new WindowsNetworkEnvironment();
        dashboard = new DashboardViewModel(environment,
            new UdpDeviceDiscovery(environment, new UdpDiscoveryChannelFactory()));
        DataContext = dashboard;
        Loaded += async (_, _) => await dashboard.StartAsync();
        Closed += async (_, _) => await dashboard.StopAsync();
    }
}
