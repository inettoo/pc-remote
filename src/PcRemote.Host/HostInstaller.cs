using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;

namespace PcRemote.Host;

internal static class HostInstaller
{
    public const string ServiceName = "PcRemoteHost";
    private const string FirewallRuleName = "PC Remote LAN Discovery";
    private const string DesktopFirewallRule = "PC Remote LAN Desktop";
    private static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PcRemote");
    private static string InstalledPath => Path.Combine(InstallDirectory, "PcRemote.Host.exe");

    public static void Install()
    {
#if !SINGLE_FILE_HOST
        throw new InvalidOperationException("请使用发布目录中的单文件 PcRemote.Host.exe，不要直接安装开发构建。");
#else
        string source = Environment.ProcessPath ?? throw new IOException("无法定位 EXE。");
        bool existed = ServiceExists();
        if (existed) VerifyExistingService();
        Directory.CreateDirectory(InstallDirectory);
        EnsureRegularPath(InstallDirectory);
        if (File.Exists(InstalledPath)) EnsureRegularPath(InstalledPath);
        if (existed) StopService();
        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(InstalledPath), StringComparison.OrdinalIgnoreCase))
        {
            string staging = Path.Combine(InstallDirectory, "PcRemote.Host.new.exe");
            if (File.Exists(staging)) EnsureRegularPath(staging);
            File.Copy(source, staging, overwrite: true);
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(staging, InstalledPath, overwrite: true); break; }
                catch (IOException exception) when (attempt < 20 && (exception.HResult & 0xffff) is 32 or 33)
                {
                    // SCM can report Stopped just before the old process releases its image.
                    Thread.Sleep(100);
                }
            }
        }
        if (!existed)
            RunSystemTool("sc.exe", "create", ServiceName, "binPath=", ServiceCommand(), "start=", "delayed-auto",
                "DisplayName=", "PC Remote LAN Host");
        // Only this executable and UDP port; peers must also pass the application's on-link checks.
        RunSystemTool("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={FirewallRuleName}");
        RunSystemTool("netsh.exe", "advfirewall", "firewall", "add", "rule", $"name={FirewallRuleName}", "dir=in", "action=allow",
            $"program={InstalledPath}", "protocol=UDP", "localport=45831", "remoteip=LocalSubnet", "profile=any", "enable=yes");
        RunSystemTool("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={DesktopFirewallRule}");
        RunSystemTool("netsh.exe", "advfirewall", "firewall", "add", "rule", $"name={DesktopFirewallRule}", "dir=in", "action=allow",
            $"program={InstalledPath}", "protocol=TCP", "localport=45832", "remoteip=LocalSubnet", "profile=any", "enable=yes");
        if (!EventLog.SourceExists(ServiceName)) EventLog.CreateEventSource(ServiceName, "Application");
        using var service = new ServiceController(ServiceName);
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
#endif
    }

    public static void Uninstall()
    {
        if (ServiceExists())
        {
            VerifyExistingService();
            StopService();
            RunSystemTool("sc.exe", "delete", ServiceName);
        }
        RunSystemTool("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={FirewallRuleName}");
        RunSystemTool("netsh.exe", "advfirewall", "firewall", "delete", "rule", $"name={DesktopFirewallRule}");
        // Keep binaries and event history; never recursively delete a computed installation path.
    }

    private static bool ServiceExists()
    {
        using var service = new ServiceController(ServiceName);
        try { _ = service.Status; return true; }
        catch (InvalidOperationException exception) when (exception.InnerException is Win32Exception { NativeErrorCode: 1060 }) { return false; }
    }

    private static void VerifyExistingService()
    {
        using var key = Registry.LocalMachine.OpenSubKey(Path.Combine("SYSTEM", "CurrentControlSet", "Services", ServiceName));
        if (key?.GetValue("ImagePath") is not string path || !path.Equals(ServiceCommand(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("已有同名服务的路径不匹配，未覆盖。");
    }

    private static string ServiceCommand() => $"{(char)34}{InstalledPath}{(char)34} --service";

    private static void StopService()
    {
        using var service = new ServiceController(ServiceName);
        if (service.Status == ServiceControllerStatus.Stopped) return;
        if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
    }

    private static void EnsureRegularPath(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("安装路径包含重解析点，未写入。");
    }

    private static void RunSystemTool(string tool, params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, tool))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException($"无法运行 {tool}。");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(20000))
        {
            process.Kill();
            throw new System.TimeoutException($"{tool} 超时。");
        }
        Task.WaitAll(output, error);
        // netsh delete reports exit 1 when a matching rule does not exist.
        if (process.ExitCode != 0 && !(tool == "netsh.exe" && args.Contains("delete") && process.ExitCode == 1))
            throw new IOException($"{tool} 失败（{process.ExitCode}）：{error.Result}{output.Result}");
    }
}
