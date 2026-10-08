using System.Runtime.InteropServices;

namespace LocalRemote;

internal sealed class KeyboardCapture : IDisposable
{
    private readonly HookCallback callback;
    private readonly Func<bool> active;
    private readonly Action<RemoteInput> input;
    private readonly Action escape;
    private IntPtr hook;
    public KeyboardCapture(Func<bool> active, Action<RemoteInput> input, Action escape)
    {
        this.active = active; this.input = input; this.escape = escape; callback = OnKey;
        hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    private IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && active())
        {
            var key = Marshal.PtrToStructure<HookKey>(data);
            bool up = message.ToInt64() is 0x0101 or 0x0105;
            if ((key.Flags & 0x10) == 0)
            {
                if (key.VirtualKey == 0x7b) { if (!up) escape(); return new(1); }
                if (key.Scan is > 0 and <= 255) { input(new("key", Value: (int)key.Scan, Up: up, Extended: (key.Flags & 1) != 0)); return new(1); }
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }
    public void Dispose() { if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; } }
    [StructLayout(LayoutKind.Sequential)] private struct HookKey { public uint VirtualKey; public uint Scan; public uint Flags; public uint Time; public UIntPtr Extra; }
    private delegate IntPtr HookCallback(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookCallback callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
