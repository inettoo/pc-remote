using System.Windows;
using PcRemote.Controller;

namespace PcRemote.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new DashboardViewModel();
    }
}
