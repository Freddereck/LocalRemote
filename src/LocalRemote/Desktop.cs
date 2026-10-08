using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace LocalRemote;

public static class DesktopAccess
{
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder buffer, int length, out int needed);
    public static bool IsAvailable()
    {
        IntPtr desktop = OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) return false;
        try { var name = new StringBuilder(256); return GetUserObjectInformation(desktop, 2, name, 512, out _) && name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase); }
        finally { CloseDesktop(desktop); }
    }
}

public sealed class ScreenCapture
{
    private VideoOptions options = new(Mode: "jpeg", Fps: 30);
    public void Configure(VideoOptions value) { if (!value.IsValid()) throw new InvalidDataException("Неверные настройки видео."); options = value; }
    private readonly ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().Single(c => c.FormatID == ImageFormat.Jpeg.Guid);
    public static MonitorInfo[] GetMonitors() => Screen.AllScreens.Select((s, i) => new MonitorInfo(i, s.DeviceName, s.Bounds.Width, s.Bounds.Height, s.Primary)).ToArray();
    public static Rectangle GetBounds(int index)
    {
        var screens = Screen.AllScreens;
        if (index < 0 || index >= screens.Length) throw new InvalidDataException("Монитор отключён. Переподключитесь.");
        return screens[index].Bounds;
    }

    public FrameData Capture(int index)
    {
        if (!DesktopAccess.IsAvailable()) throw new DesktopUnavailableException();
        Rectangle bounds = GetBounds(index);
        var size = options.Fit(bounds.Size);
        int width = size.Width, height = size.Height;
        double scale = width / (double)bounds.Width;
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            IntPtr source = GetDC(IntPtr.Zero);
            if (source == IntPtr.Zero) throw new DesktopUnavailableException();
            IntPtr target = graphics.GetHdc();
            try
            {
                SetStretchBltMode(target, 4);
                SetBrushOrgEx(target, 0, 0, IntPtr.Zero);
                if (!StretchBlt(target, 0, 0, width, height, source, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x40CC0020))
                    throw new DesktopUnavailableException();
            }
            finally { graphics.ReleaseHdc(target); ReleaseDC(IntPtr.Zero, source); }
            var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
            if (GetCursorInfo(ref cursor) && cursor.Flags == 1 && bounds.Contains(cursor.Position))
            {
                IntPtr copy = CopyIcon(cursor.Handle);
                if (copy != IntPtr.Zero)
                {
                    try
                    {
                        if (GetIconInfo(copy, out var info))
                        {
                            try
                            {
                                using var icon = Icon.FromHandle(copy);
                                graphics.DrawIcon(icon, new Rectangle((int)((cursor.Position.X - bounds.X - info.HotspotX) * scale), (int)((cursor.Position.Y - bounds.Y - info.HotspotY) * scale), Math.Max(1, (int)(icon.Width * scale)), Math.Max(1, (int)(icon.Height * scale))));
                            }
                            finally { if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask); if (info.Color != IntPtr.Zero) DeleteObject(info.Color); }
                        }
                    }
                    finally { DestroyIcon(copy); }
                }
            }
        }
        using var memory = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)options.JpegQuality);
        bitmap.Save(memory, jpeg, parameters);
        return new(bounds.Width, bounds.Height, memory.ToArray(), index);
    }

    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public int Size; public int Flags; public IntPtr Handle; public Point Position; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool IsIcon; public uint HotspotX; public uint HotspotY; public IntPtr Mask; public IntPtr Color; }
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sx, int sy, int sw, int sh, uint operation);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr CopyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}

public sealed class DesktopUnavailableException : Exception
{
    public DesktopUnavailableException() : base("Экран заблокирован или открыто защищённое окно Windows. Разблокируйте ПК локально.") { }
}

public sealed class InputController
{
    private readonly object gate = new();
    private readonly HashSet<(int Scan, bool Extended)> pressedKeys = [];
    private readonly HashSet<int> pressedButtons = [];
    public void Apply(RemoteInput input, int monitor)
    {
        if (!InputValidation.IsValid(input)) throw new InvalidDataException("Неверное событие ввода.");
        lock (gate)
        {
            if (!DesktopAccess.IsAvailable()) { ReleaseAll(); return; }
            if (input.Kind == "key")
            {
                Send(Key(input.Value, input.Up, input.Extended));
                if (input.Up) pressedKeys.Remove((input.Value, input.Extended)); else pressedKeys.Add((input.Value, input.Extended));
                return;
            }
            var bounds = ScreenCapture.GetBounds(monitor);
            var desktop = SystemInformation.VirtualScreen;
            int x = bounds.Left + (int)Math.Round(input.X / 65535d * (bounds.Width - 1));
            int y = bounds.Top + (int)Math.Round(input.Y / 65535d * (bounds.Height - 1));
            int absoluteX = (int)Math.Round((x - desktop.Left) * 65535d / Math.Max(1, desktop.Width - 1));
            int absoluteY = (int)Math.Round((y - desktop.Top) * 65535d / Math.Max(1, desktop.Height - 1));
            uint flags = 0x0001 | 0x8000 | 0x4000;
            uint data = 0;
            if (input.Kind == "button") flags |= ButtonFlag(input.Value, input.Up);
            if (input.Kind == "wheel") { flags |= 0x0800; data = unchecked((uint)input.Value); }
            Send(Mouse(flags, data, absoluteX, absoluteY));
            if (input.Kind == "button") { if (input.Up) pressedButtons.Remove(input.Value); else pressedButtons.Add(input.Value); }
        }
    }
    public void ReleaseAll()
    {
        lock (gate)
        {
            foreach (var key in pressedKeys) TrySend(Key(key.Scan, true, key.Extended));
            foreach (int button in pressedButtons) TrySend(Mouse(ButtonFlag(button, true)));
            pressedKeys.Clear(); pressedButtons.Clear();
        }
    }
    private static uint ButtonFlag(int button, bool up) => button switch { 0 => up ? 0x0004u : 0x0002u, 1 => up ? 0x0010u : 0x0008u, 2 => up ? 0x0040u : 0x0020u, _ => throw new InvalidDataException() };
    private static NativeInput Key(int scan, bool up, bool extended) => new() { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { Scan = (ushort)scan, Flags = 0x0008u | (up ? 0x0002u : 0) | (extended ? 1u : 0) } } };
    private static NativeInput Mouse(uint flags, uint data = 0, int x = 0, int y = 0) => new() { Type = 0, Union = new InputUnion { Mouse = new MouseInput { X = x, Y = y, Data = data, Flags = flags } } };
    private static void Send(NativeInput input) { if (SendInput(1, [input], Marshal.SizeOf<NativeInput>()) != 1) throw new InvalidOperationException("Windows отклонила ввод. На втором ПК нажмите «Перезапустить хост от администратора» или включите автозапуск с правами администратора."); }
    private static void TrySend(NativeInput input) { try { Send(input); } catch { } }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X; public int Y; public uint Data; public uint Flags; public uint Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey; public ushort Scan; public uint Flags; public uint Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
