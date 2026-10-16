using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;

namespace PcRemote.Host;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool quiet = args.Contains("--quiet") || args.Contains("--agent");
        args = args.Where(arg => arg != "--quiet").ToArray();
        try
        {
            if (args.Length == 3 && args[0] == "--agent" && uint.TryParse(args[2], out uint serviceId))
            {
                DesktopAgent.RunAsync(args[1], serviceId).GetAwaiter().GetResult();
                return 0;
            }
            if (args.SequenceEqual(["--service"]))
            {
                ServiceBase.Run(new HostWindowsService());
                return 0;
            }
            if (args.SequenceEqual(["--diagnose"]))
            {
                Console.WriteLine("PC Remote Host: installer/service entry points available; no changes made.");
                return 0;
            }
            bool uninstall = args.SequenceEqual(["--uninstall"]);
            if (args.Length > 0 && !uninstall && !args.SequenceEqual(["--install"]))
                throw new ArgumentException("支持参数：--install、--uninstall、--service、--diagnose");
            if (!IsAdministrator())
            {
                if (quiet) throw new InvalidOperationException("静默安装需要已有管理员权限。");
                string path = Environment.ProcessPath ?? throw new IOException("无法定位被控端 EXE。");
                Process.Start(new ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = uninstall ? "--uninstall" : "--install",
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                return 0;
            }
            if (uninstall) HostInstaller.Uninstall();
            else HostInstaller.Install();
            if (!quiet) MessageBox(IntPtr.Zero,
                uninstall ? "被控服务已停止并移除。" : "被控服务已安装并启动，以后随 Windows 启动。\n控制端将自动发现这台电脑。支持已登录的活动控制台桌面；锁屏和 UAC 安全桌面不支持。",
                "PC Remote 被控端", 0x40);
            return 0;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            if (!quiet) MessageBox(IntPtr.Zero, "Windows 管理员提升已取消，未安装服务。", "PC Remote 被控端", 0x40);
            return 1;
        }
        catch (Exception exception)
        {
            if (quiet) Console.Error.WriteLine(exception);
            else MessageBox(IntPtr.Zero, $"操作未完成：{exception.Message}", "PC Remote 被控端", 0x10);
            return 1;
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
    private static int MessageBox(IntPtr owner, string text, string caption, uint type) => MessageBoxW(owner, text, caption, type);
}
