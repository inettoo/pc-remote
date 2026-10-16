using System.IO;
using System.Windows;
using Microsoft.Win32;
using PcRemote.Controller;

namespace PcRemote.App;

public partial class FileWindow : Window
{
    private readonly FileBrowserViewModel browser;

    public FileWindow(RemoteDesktopClient client)
    {
        InitializeComponent();
        browser = new FileBrowserViewModel(client);
        DataContext = browser;
        Loaded += async (_, _) => await browser.NavigateAsync("");
        Closed += (_, _) => browser.Cancel();
    }

    private async void UpClick(object sender, RoutedEventArgs args) => await browser.NavigateAsync(Path.GetDirectoryName(browser.CurrentPath) ?? "");
    private async void RefreshClick(object sender, RoutedEventArgs args) => await browser.NavigateAsync(browser.CurrentPath);
    private void CancelClick(object sender, RoutedEventArgs args) => browser.Cancel();

    private async void UploadClick(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFileDialog { Title = "选择要上传的文件", Multiselect = false, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await browser.UploadAsync(dialog.FileName);
    }

    private async void OpenClick(object sender, RoutedEventArgs args)
    {
        if (browser.Busy || browser.Selected is not { } item) return;
        if (item.IsDirectory) { await browser.NavigateAsync(item.RelativePath); return; }
        var dialog = new SaveFileDialog { Title = "保存远端文件", FileName = Path.GetFileName(item.Name), OverwritePrompt = true };
        if (dialog.ShowDialog(this) == true) await browser.DownloadAsync(item, dialog.FileName, File.Exists(dialog.FileName));
    }
}
