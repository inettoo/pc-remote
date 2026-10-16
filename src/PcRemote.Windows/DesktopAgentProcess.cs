using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PcRemote.Windows;

public sealed class DesktopUserContext : IDisposable
{
    private readonly SafeAccessTokenHandle token;
    public SecurityIdentifier UserSid { get; }
    public uint SessionId { get; }
    public bool IsActive => WTSGetActiveConsoleSessionId() == SessionId;

    private DesktopUserContext(SafeAccessTokenHandle token, uint sessionId)
    {
        this.token = token;
        SessionId = sessionId;
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        UserSid = identity.User ?? throw new IOException("No logged-in desktop user.");
    }

    public static DesktopUserContext OpenActiveConsole()
    {
        uint session = WTSGetActiveConsoleSessionId();
        if (session is 0 or uint.MaxValue || !WTSQueryUserToken(session, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "没有可用的已登录用户桌面。");
        try
        {
            if (!GetTokenElevationType(token, 18, out int elevationType, sizeof(int), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (elevationType == 2)
            {
                if (!GetLinkedToken(token, 19, out IntPtr linked, IntPtr.Size, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                token.Dispose();
                token = new SafeAccessTokenHandle(linked);
            }
            return new DesktopUserContext(token, session);
        }
        catch { token.Dispose(); throw; }
    }

    public DesktopAgentProcess Launch(string executable, string pipeName, int serviceProcessId)
    {
        var start = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = "winsta0" + (char)92 + "default" };
        var command = new StringBuilder($"{(char)34}{executable}{(char)34} --agent {pipeName} {serviceProcessId}");
        if (!CreateEnvironmentBlock(out IntPtr environment, token, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!CreateProcessAsUserW(token, executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x00000400 | 0x08000000,
                environment, Path.GetDirectoryName(executable)!, ref start, out var process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启动用户桌面代理。");
            CloseHandle(process.Thread);
            return new DesktopAgentProcess(process.Process, process.ProcessId);
        }
        finally { DestroyEnvironmentBlock(environment); }
    }

    public void Dispose() => token.Dispose();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public short Show, ReservedBytes;
        public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    private static extern bool GetTokenElevationType(SafeAccessTokenHandle token, int informationClass, out int value, int length, out int needed);
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    private static extern bool GetLinkedToken(SafeAccessTokenHandle token, int informationClass, out IntPtr value, int length, out int needed);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr environment);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool CreateProcessAsUserW(SafeAccessTokenHandle token, string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

public sealed class DesktopAgentProcess(IntPtr handle, uint processId) : IDisposable
{
    public uint ProcessId { get; } = processId;
    public async Task WaitForExitAsync()
    {
        await Task.Run(() => WaitForSingleObject(handle, 3000));
    }
    public void Dispose()
    {
        if (WaitForSingleObject(handle, 0) == 258) TerminateProcess(handle, 1);
        CloseHandle(handle);
    }
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr handle, uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
