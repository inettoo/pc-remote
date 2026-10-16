using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PcRemote.Protocol;

namespace PcRemote.Input;

public sealed class WindowsInput : IInputSink
{
    private readonly object gate = new();
    private readonly HashSet<int> keys = new();
    private bool left, right;
    public static bool IsDefaultDesktop()
    {
        IntPtr desktop = OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(256);
            return GetUserObjectInformationW(desktop, 2, name, name.Capacity * 2, out _) && name.ToString() == "Default";
        }
        finally { CloseDesktop(desktop); }
    }

    public Task ApplyAsync(RemoteInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Apply(input);
        return Task.CompletedTask;
    }

    public Task ReleaseAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReleaseAll();
        return Task.CompletedTask;
    }

    public void Apply(RemoteInput input)
    {
        lock (gate) ApplyCore(input);
    }

    private void ApplyCore(RemoteInput input)
    {
        DesktopMessages.Validate(input);
        if (input.Kind == RemoteInputKind.ReleaseAll) { ReleaseAll(); return; }
        if (!IsDefaultDesktop()) return;
        switch (input.Kind)
        {
            case RemoteInputKind.Move:
                var primary = Screen.PrimaryScreen?.Bounds ?? throw new InvalidOperationException("No primary screen.");
                var virtualBounds = SystemInformation.VirtualScreen;
                int px = primary.Left + (int)((long)input.X * (primary.Width - 1) / 65535);
                int py = primary.Top + (int)((long)input.Y * (primary.Height - 1) / 65535);
                SendMouse(0x8000 | 0x4000 | 0x0001,
                    (int)((long)(px - virtualBounds.Left) * 65535 / Math.Max(1, virtualBounds.Width - 1)),
                    (int)((long)(py - virtualBounds.Top) * 65535 / Math.Max(1, virtualBounds.Height - 1)));
                break;
            case RemoteInputKind.LeftDown: SendMouse(0x0002); left = true; break;
            case RemoteInputKind.LeftUp: SendMouse(0x0004); left = false; break;
            case RemoteInputKind.RightDown: SendMouse(0x0008); right = true; break;
            case RemoteInputKind.RightUp: SendMouse(0x0010); right = false; break;
            case RemoteInputKind.Wheel: SendMouse(0x0800, data: unchecked((uint)input.Delta)); break;
            case RemoteInputKind.KeyDown: SendKey(input.Key, false); keys.Add(input.Key); break;
            case RemoteInputKind.KeyUp: SendKey(input.Key, true); keys.Remove(input.Key); break;
        }
    }

    public void ReleaseAll()
    {
        lock (gate)
        {
            foreach (int key in keys.ToArray())
            {
                try { SendKey(key, true); keys.Remove(key); }
                catch (Win32Exception) { System.Diagnostics.Trace.TraceWarning("Key release blocked by current desktop."); }
            }
            if (left)
            {
                try { SendMouse(0x0004); left = false; }
                catch (Win32Exception) { System.Diagnostics.Trace.TraceWarning("Mouse release blocked by current desktop."); }
            }
            if (right)
            {
                try { SendMouse(0x0010); right = false; }
                catch (Win32Exception) { System.Diagnostics.Trace.TraceWarning("Mouse release blocked by current desktop."); }
            }
        }
    }

    private static void SendMouse(uint flags, int x = 0, int y = 0, uint data = 0)
        => Send(new NativeInput { Type = 0, Data = new InputUnion { Mouse = new MouseInput { X = x, Y = y, Data = data, Flags = flags } } });

    private static void SendKey(int key, bool up)
    {
        bool extended = key is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x6F or 0xA3 or 0xA5;
        Send(new NativeInput { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = (ushort)key, Flags = (up ? 2u : 0) | (extended ? 1u : 0) } } });
    }

    private static void Send(NativeInput input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<NativeInput>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected input; elevated/secure desktops are not supported.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public IntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder info, int length, out int needed);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
}
