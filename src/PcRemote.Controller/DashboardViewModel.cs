namespace PcRemote.Controller;

public sealed class DashboardViewModel
{
    public string Version => "v0.0.1 · Foundation";
    public string Status => "基础版本 · 网络功能尚未启用";
    public string EmptyTitle => "从同一局域网，连接你的电脑";
    public string EmptyDescription => "设备发现将在 v0.0.2 提供。当前版本不会扫描网络或建立远程连接。";
}
