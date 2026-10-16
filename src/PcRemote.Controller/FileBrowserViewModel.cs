using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PcRemote.Protocol;

namespace PcRemote.Controller;

public sealed class FileBrowserViewModel(RemoteDesktopClient client) : INotifyPropertyChanged
{
    private CancellationTokenSource? operation;
    private bool busy;
    private string currentPath = "", status = "正在读取用户文件…";
    private double percent;
    private RemoteFileItem? selected;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<RemoteFileItem> Entries { get; } = [];
    public string CurrentPath { get => currentPath; private set { currentPath = value; Changed(); Changed(nameof(DisplayPath)); Changed(nameof(CanUp)); } }
    public string DisplayPath => string.IsNullOrEmpty(CurrentPath) ? "用户文件" : "用户文件 / " + CurrentPath;
    public string Status { get => status; private set { status = value; Changed(); } }
    public double Percent { get => percent; private set { percent = value; Changed(); } }
    public bool Busy { get => busy; private set { busy = value; Changed(); Changed(nameof(CanAct)); Changed(nameof(CanUp)); Changed(nameof(CanOpen)); } }
    public bool CanAct => !Busy && client.IsConnected;
    public bool CanUp => CanAct && CurrentPath.Length > 0;
    public bool CanOpen => CanAct && Selected is not null;
    public RemoteFileItem? Selected { get => selected; set { selected = value; Changed(); Changed(nameof(CanOpen)); } }

    public Task NavigateAsync(string path) => PerformAsync(async token =>
    {
        var directory = await client.ListFilesAsync(path, token);
        CurrentPath = directory.RelativePath;
        Entries.Clear();
        foreach (var item in directory.Items) Entries.Add(item);
        Selected = null;
        Status = directory.Truncated ? "显示部分项目（最多 128 项）；链接或不支持的路径已隐藏。" : $"{Entries.Count} 个项目";
    });

    public Task UploadAsync(string localPath) => PerformAsync(async token =>
    {
        string remotePath = Path.Combine(CurrentPath, Path.GetFileName(localPath));
        await client.UploadFileAsync(localPath, remotePath, Progress(), token);
        var directory = await client.ListFilesAsync(CurrentPath, token);
        Entries.Clear();
        foreach (var item in directory.Items) Entries.Add(item);
        Percent = 100;
        Status = "上传完成，SHA-256 校验通过。";
    });

    public Task DownloadAsync(RemoteFileItem item, string localPath, bool overwrite) => PerformAsync(async token =>
    {
        await client.DownloadFileAsync(item.RelativePath, localPath, overwrite, Progress(), token);
        Percent = 100;
        Status = "下载完成，SHA-256 校验通过。";
    });

    private Progress<TransferProgress> Progress()
    {
        var current = operation;
        return new(value =>
        {
            if (current is null || current != operation || current.IsCancellationRequested) return;
            Percent = value.Total == 0 ? 100 : 100d * value.Completed / value.Total;
            Status = $"传输中 · {value.Completed:N0} / {value.Total:N0} 字节";
        });
    }

    private async Task PerformAsync(Func<CancellationToken, Task> action)
    {
        if (Busy) return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        Busy = true;
        Percent = 0;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Status = "操作已取消或连接已结束。"; }
        catch (Exception exception) { Status = $"操作失败：{exception.Message}"; }
        finally { operation = null; Busy = false; }
    }

    public void Cancel() => operation?.Cancel();
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
