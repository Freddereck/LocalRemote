using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LocalRemote;

public sealed class ViewerForm : Form
{
    private ViewerClient client;
    private readonly Invitation invitation;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ScreenSurface surface = new() { Dock = DockStyle.Fill, TabStop = true, AccessibleName = "Удалённый рабочий стол. Щёлкните или перейдите Tab для управления. F12 возвращает локальное управление." };
    private readonly CheckBox control = new() { Text = "Управлять", Checked = true, AutoSize = true };
    private readonly ComboBox monitors = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Удалённый монитор" };
    private readonly ComboBox quality = new() { Width = 210, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Качество и частота кадров" };
    private readonly VideoOptions[] profiles = [new(), new(1920,1080,60,20000000), new(1920,1080,30,16000000), new(1920,1080,30,16000000,"jpeg",98)];
    private readonly Stopwatch statistics = Stopwatch.StartNew();
    private int receivedFrames;
    private string videoLabel = "Подключение";
    private string rateLabel = "";
    private string inputNotice = "";
    private long inputNoticeUntil;
    private FileManagerForm? fileManager;
    private readonly Label status = new() { Text = "Щёлкните по экрану для управления. F12 — отпустить управление.", AutoSize = true, Padding = new(12, 7, 12, 7) };
    private KeyboardCapture? keyboard;
    private long lastMove;
    private MouseButtons heldButtons;
    private bool closing;
    private bool reconnecting;
    private bool settingMonitor;
    private bool fullscreen;
    private Task? reconnectTask;
    private Rectangle savedBounds;
    private FormWindowState savedState;
    public ViewerForm(ViewerClient initialClient, WelcomeInfo welcome, Invitation invitation)
    {
        client = initialClient; this.invitation = invitation;
        Text = "LocalRemote · " + welcome.Computer; Font = new("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        Size = new(1280, 820); MinimumSize = new(700, 480); StartPosition = FormStartPosition.CenterParent;
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new(12, 12, 12, 4), WrapContents = true, BackColor = UiTheme.Paper };
        toolbar.Controls.Add(new BrandMark { Size = new(36, 36), Margin = new(0, 0, 12, 8) });
        control.Margin = new(0, 9, 14, 8); monitors.Margin = quality.Margin = new(0, 6, 12, 8);
        toolbar.Controls.Add(control); toolbar.Controls.Add(monitors);
        quality.Items.AddRange(["720p · 60 FPS", "1080p · 60 FPS", "1080p · 30 FPS", "Чёткий текст · 1080p"]); quality.SelectedIndex = 0;
        toolbar.Controls.Add(quality);
        quality.SelectedIndexChanged += (_, _) => { if (client.IsConnected) { ReleaseInput(); surface.ClearFrame(); ApplyProfile(); } };
        var files = new UiButton { Text = "Файлы…", Tone = ButtonTone.Primary, MinimumSize = new(85,40) };
        files.Click += (_, _) => { if (fileManager == null || fileManager.IsDisposed) { fileManager = new(() => client.Files); fileManager.Show(this); } else fileManager.Activate(); };
        toolbar.Controls.Add(files);
        var taskManager = new UiButton { Text = "Диспетчер задач", MinimumSize = new(120, 40) };
        taskManager.Click += (_, _) =>
        {
            if (!client.IsConnected) return;
            foreach (int scan in new[] { 0x1d, 0x2a, 0x01 }) client.SendInput(new("key", Value: scan));
            foreach (int scan in new[] { 0x01, 0x2a, 0x1d }) client.SendInput(new("key", Value: scan, Up: true));
        };
        toolbar.Controls.Add(taskManager);
        var full = new UiButton { Text = "Полный экран", MinimumSize = new(100, 40) }; full.Click += (_, _) => ToggleFullscreen(); toolbar.Controls.Add(full);
        var disconnect = new UiButton { Text = "Отключиться", Tone = ButtonTone.Danger, MinimumSize = new(100, 40) }; disconnect.Click += (_, _) => Close(); toolbar.Controls.Add(disconnect);
        var footer = UiTheme.Footer(status); status.ForeColor = UiTheme.Muted;
        Controls.Add(surface); Controls.Add(footer); Controls.Add(toolbar);
        UiTheme.Apply(this);
        PopulateMonitors(welcome);
        monitors.SelectedIndexChanged += (_, _) => { if (!settingMonitor && client.IsConnected && monitors.SelectedIndex >= 0) { ReleaseInput(); surface.ClearFrame(); client.SelectMonitor(monitors.SelectedIndex); } };
        control.CheckedChanged += (_, _) => { if (!control.Checked) ReleaseInput(); status.Text = control.Checked ? "Щёлкните по экрану. F12 — локальное управление." : "Управление отпущено. Включите «Управлять» для продолжения."; };
        surface.MouseDown += (_, e) =>
        {
            surface.Focus();
            if (!CanControl() || !surface.TryNormalize(e.Location, false, out int x, out int y)) return;
            int button = ButtonIndex(e.Button); if (button < 0) return;
            heldButtons |= e.Button;
            surface.Capture = true; client.SendInput(new("button", x, y, button));
        };
        surface.MouseUp += (_, e) =>
        {
            if (CanControl() && surface.TryNormalize(e.Location, true, out int x, out int y)) { int button = ButtonIndex(e.Button); if (button >= 0) client.SendInput(new("button", x, y, button, true)); }
            heldButtons &= ~e.Button;
            if (heldButtons == MouseButtons.None) surface.Capture = false;
        };
        surface.MouseMove += (_, e) =>
        {
            long now = Environment.TickCount64;
            if (now - lastMove < 25 || !CanControl() || !surface.TryNormalize(e.Location, surface.Capture, out int x, out int y)) return;
            lastMove = now; client.SendInput(new("move", x, y));
        };
        surface.MouseWheel += (_, e) => { if (CanControl() && surface.TryNormalize(e.Location, false, out int x, out int y)) client.SendInput(new("wheel", x, y, Math.Clamp(e.Delta, -1200, 1200))); };
        surface.LostFocus += (_, _) => ReleaseInput();
        Deactivate += (_, _) => ReleaseInput();
        surface.MouseCaptureChanged += (_, _) => { if (!surface.Capture && heldButtons != MouseButtons.None) ReleaseInput(); };
        Shown += (_, _) =>
        {
            try { keyboard = new(CanControl, input => this.client.SendInput(input), () => { control.Checked = false; control.Focus(); }); }
            catch (Exception ex) { control.Checked = false; MessageBox.Show(this, "Не удалось включить клавиатуру: " + ex.Message, "LocalRemote"); }
            AttachClient(client); client.StartReceiving(); ApplyProfile();
        };
        FormClosing += (_, _) => { closing = true; lifetime.Cancel(); keyboard?.Dispose(); ReleaseInput(); };
        FormClosed += async (_, _) => { if (reconnectTask != null) await reconnectTask; await this.client.DisposeAsync(); lifetime.Dispose(); };
    }
    private bool CanControl() => !closing && !reconnecting && client.IsConnected && control.Checked && surface.Focused && Form.ActiveForm == this && surface.HasFrame;
    private void ReleaseInput() { heldButtons = MouseButtons.None; if (client.IsConnected) client.ReleaseInput(); }
    private static int ButtonIndex(MouseButtons button) => button switch { MouseButtons.Left => 0, MouseButtons.Right => 1, MouseButtons.Middle => 2, _ => -1 };
    private void PopulateMonitors(WelcomeInfo welcome)
    {
        settingMonitor = true; monitors.Items.Clear();
        foreach (var m in welcome.Monitors) monitors.Items.Add($"Экран {m.Index + 1} · {m.Width}×{m.Height}");
        monitors.SelectedIndex = Math.Clamp(welcome.SelectedMonitor, 0, welcome.Monitors.Length - 1); settingMonitor = false;
    }
    private void AttachClient(ViewerClient target)
    {
        target.FrameReceived += frame => DisplayFrameAsync(target, frame);
        target.VideoStarted += info => OnUi(() => { if (ReferenceEquals(client, target)) { videoLabel = $"{info.Width}×{info.Height} · {info.Encoder}"; statistics.Restart(); receivedFrames = 0; rateLabel = ""; } });
        target.StatusChanged += text => OnUi(() =>
        {
            if (!ReferenceEquals(client, target)) return;
            if (text.StartsWith("Windows отклонила ввод.", StringComparison.Ordinal)) { inputNotice = text; inputNoticeUntil = Environment.TickCount64 + 15000; }
            else surface.ClearFrame();
            status.Text = text; ReleaseInput();
        });
        target.Disconnected += () => OnUi(() => { if (ReferenceEquals(client, target) && !closing) reconnectTask = ReconnectAsync(); });
    }
    private Task DisplayFrameAsync(ViewerClient target, FrameData frame)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Bitmap bitmap;
        try
        {
            if (frame.Encoding == FrameEncoding.Bgr24)
            {
                if (frame.PixelWidth is < 2 or > 1920 || frame.PixelHeight is < 2 or > 1080 || frame.Jpeg.Length != frame.PixelWidth * frame.PixelHeight * 3) throw new InvalidDataException("Неверный кадр видео.");
                bitmap = new Bitmap(frame.PixelWidth, frame.PixelHeight, PixelFormat.Format24bppRgb);
                var bits = bitmap.LockBits(new(0,0,bitmap.Width,bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try { for (int row = 0; row < bitmap.Height; row++) Marshal.Copy(frame.Jpeg, row * bitmap.Width * 3, IntPtr.Add(bits.Scan0, row * bits.Stride), bitmap.Width * 3); }
                finally { bitmap.UnlockBits(bits); }
            }
            else
            {
                using var data = new MemoryStream(frame.Jpeg); using var image = Image.FromStream(data);
                if (image.Width is < 1 or > 4096 || image.Height is < 1 or > 4096) throw new InvalidDataException("Слишком большой кадр.");
                bitmap = new Bitmap(image);
            }
        }
        catch (Exception ex) { completion.TrySetException(ex); return completion.Task; }
        if (!OnUi(() =>
        {
            if (closing || !ReferenceEquals(client, target) || frame.MonitorIndex != monitors.SelectedIndex) bitmap.Dispose();
            else
            {
                if (frame.Encoding == FrameEncoding.Jpeg) videoLabel = $"{bitmap.Width}×{bitmap.Height} · JPEG";
                surface.SetFrame(bitmap); receivedFrames++;
                if (statistics.Elapsed.TotalSeconds >= 1) { rateLabel = $"{receivedFrames / statistics.Elapsed.TotalSeconds:0} FPS"; receivedFrames = 0; statistics.Restart(); }
                status.Text = Environment.TickCount64 < inputNoticeUntil ? inputNotice : $"{videoLabel} · {rateLabel} · " + (control.Checked ? "F12 — локальное управление" : "Управление отпущено");
            }
            completion.TrySetResult();
        })) { bitmap.Dispose(); completion.TrySetCanceled(); }
        return completion.Task.WaitAsync(lifetime.Token);
    }
    private async Task ReconnectAsync()
    {
        if (reconnecting || closing) return; reconnecting = true; surface.ClearFrame();
        try
        {
            await client.DisposeAsync();
            while (!lifetime.IsCancellationRequested)
            {
                status.Text = "Связь потеряна. Повторное подключение через 2 секунды…";
                await Task.Delay(2000, lifetime.Token);
                var next = new ViewerClient();
                try
                {
                    var welcome = await next.ConnectAsync(invitation, lifetime.Token);
                    if (closing) { await next.DisposeAsync(); return; }
                    client = next; PopulateMonitors(welcome); AttachClient(next); reconnecting = false; next.StartReceiving(); ApplyProfile(); return;
                }
                catch (Exception ex) { await next.DisposeAsync(); status.Text = "Повторное подключение: " + ex.Message; }
            }
        }
        catch (OperationCanceledException) { }
        finally { reconnecting = false; }
    }
    private void ApplyProfile()
    {
        int index = Math.Clamp(quality.SelectedIndex, 0, profiles.Length - 1);
        var options = profiles[index];
        if (FfmpegTools.FindExecutable() == null) options = options with { Mode = "jpeg", Fps = 30 };
        client.SetVideoOptions(options);
    }
    private bool OnUi(Action action)
    {
        if (closing || IsDisposed || !IsHandleCreated) return false;
        try { BeginInvoke(action); return true; } catch (InvalidOperationException) { return false; }
    }
    private void ToggleFullscreen()
    {
        if (!fullscreen) { savedBounds = Bounds; savedState = WindowState; WindowState = FormWindowState.Normal; FormBorderStyle = FormBorderStyle.None; Bounds = Screen.FromControl(this).Bounds; }
        else { FormBorderStyle = FormBorderStyle.Sizable; Bounds = savedBounds; WindowState = savedState; }
        fullscreen = !fullscreen;
    }
}

internal sealed class ScreenSurface : Control
{
    private Bitmap? frame;
    public bool HasFrame => frame != null;
    public ScreenSurface() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true); BackColor = Color.FromArgb(23, 25, 29); }
    public void SetFrame(Bitmap bitmap) { var old = frame; frame = bitmap; old?.Dispose(); Invalidate(); }
    public void ClearFrame() { frame?.Dispose(); frame = null; Invalidate(); }
    private Rectangle ImageBounds()
    {
        if (frame == null || ClientSize.Width <= 0 || ClientSize.Height <= 0) return Rectangle.Empty;
        double scale = Math.Min(ClientSize.Width / (double)frame.Width, ClientSize.Height / (double)frame.Height);
        int width = Math.Max(1, (int)(frame.Width * scale)), height = Math.Max(1, (int)(frame.Height * scale));
        return new((ClientSize.Width - width) / 2, (ClientSize.Height - height) / 2, width, height);
    }
    public bool TryNormalize(Point point, bool clamp, out int x, out int y)
    {
        var area = ImageBounds(); x = y = 0;
        if (area.IsEmpty || (!clamp && !area.Contains(point))) return false;
        x = (int)Math.Round(Math.Clamp((point.X - area.X) / (double)Math.Max(1, area.Width - 1), 0, 1) * 65535);
        y = (int)Math.Round(Math.Clamp((point.Y - area.Y) / (double)Math.Max(1, area.Height - 1), 0, 1) * 65535); return true;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (frame != null)
        {
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var bounds = ImageBounds();
            e.Graphics.DrawImage(frame, bounds, 0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel);
        }
        else TextRenderer.DrawText(e.Graphics, "Ожидаю изображение…", Font, ClientRectangle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3), Color.White, BackColor);
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void Dispose(bool disposing) { if (disposing) frame?.Dispose(); base.Dispose(disposing); }
}
