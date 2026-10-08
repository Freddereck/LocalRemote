using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LocalRemote;

internal static class UiTheme
{
    public const string Credit = "By mderick.dev";
    public static Color Canvas => SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(243, 246, 248);
    public static Color Paper => SystemInformation.HighContrast ? SystemColors.Window : Color.White;
    public static Color Ink => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(30, 44, 53);
    public static Color Muted => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(94, 108, 118);
    public static Color Line => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(216, 225, 230);
    public static Color Accent => SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(26, 98, 101);
    public static Color Tint => SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(231, 242, 240);
    public static Icon AppIcon { get; } = CreateIcon();

    public static void Apply(Form form)
    {
        form.BackColor = Canvas; form.ForeColor = Ink; form.Font = new("Segoe UI", 10); form.Icon = AppIcon;
        void Visit(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                if (control is TextBox text) { text.BackColor = Paper; text.ForeColor = Ink; }
                else if (control is ComboBox combo) { combo.BackColor = Paper; combo.ForeColor = Ink; combo.FlatStyle = FlatStyle.Flat; }
                else if (control is ListView list) { list.BackColor = Paper; list.ForeColor = Ink; list.BorderStyle = BorderStyle.None; }
                else if (control is CheckBox check) check.ForeColor = Ink;
                Visit(control);
            }
        }
        Visit(form);
        form.Shown += (_, _) =>
        {
            Rectangle area = Screen.FromControl(form).WorkingArea;
            var available = new Size(Math.Max(320, area.Width - 24), Math.Max(300, area.Height - 24));
            form.MinimumSize = new(Math.Min(form.MinimumSize.Width, available.Width), Math.Min(form.MinimumSize.Height, available.Height));
            form.Size = new(Math.Min(form.Width, available.Width), Math.Min(form.Height, available.Height));
            form.Location = new(Math.Clamp(form.Left, area.Left, Math.Max(area.Left, area.Right - form.Width)), Math.Clamp(form.Top, area.Top, Math.Max(area.Top, area.Bottom - form.Height)));
        };
    }
    public static Control Header(string title = "LocalRemote", string subtitle = "Управление компьютерами в вашей сети")
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 92, Padding = new(24, 18, 24, 14), ColumnCount = 2, RowCount = 2, BackColor = Paper };
        header.ColumnStyles.Add(new(SizeType.Absolute, 68)); header.ColumnStyles.Add(new(SizeType.Percent, 100));
        var mark = new BrandMark { Size = new(48, 48), Margin = new(0, 0, 16, 0) }; header.Controls.Add(mark, 0, 0); header.SetRowSpan(mark, 2);
        header.Controls.Add(new Label { Text = title, AutoSize = true, Font = new("Segoe UI", 22, FontStyle.Bold), ForeColor = Ink, Margin = new(0) }, 1, 0);
        header.Controls.Add(new Label { Text = subtitle, AutoSize = true, ForeColor = Muted, Margin = new(1, 2, 0, 0) }, 1, 1);
        return header;
    }
    public static Control Footer(Control? left = null)
    {
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 42, Padding = new(18, 8, 18, 6), ColumnCount = 2, RowCount = 1, BackColor = Paper };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.AutoSize));
        footer.RowStyles.Add(new(SizeType.Percent, 100));
        if (left != null)
        {
            left.Dock = DockStyle.Fill; left.Margin = new(0, 0, 12, 0); left.Padding = new(0);
            if (left is Label label) { label.AutoSize = false; label.AutoEllipsis = true; label.TextAlign = ContentAlignment.MiddleLeft; }
            footer.Controls.Add(left, 0, 0);
        }
        footer.Controls.Add(new Label { Text = Credit, AutoSize = true, ForeColor = Muted, Font = new("Segoe UI", 9), Anchor = AnchorStyles.Right, Margin = new(12, 0, 0, 0), AccessibleName = Credit }, 1, 0);
        return footer;
    }
    public static Control Field(Control control, int height = 46)
    {
        var field = new UiCard { Dock = DockStyle.Fill, Height = height, MinimumSize = new(0, height), Padding = new(12, 10, 12, 8), Radius = 7, FillColor = Paper, Margin = new(0) };
        if (control is TextBox text) text.BorderStyle = BorderStyle.None;
        control.Dock = DockStyle.Fill; field.Controls.Add(control); return field;
    }
    public static TableLayoutPanel Stack(int padding = 0)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new(padding), ColumnCount = 1, GrowStyle = TableLayoutPanelGrowStyle.AddRows, BackColor = Paper, Margin = new(0) };
        table.ColumnStyles.Add(new(SizeType.Percent, 100));
        table.SizeChanged += (_, _) =>
        {
            int available = Math.Max(100, table.ClientSize.Width - table.Padding.Horizontal);
            foreach (var label in table.Controls.OfType<Label>()) label.MaximumSize = new(available, 0);
            foreach (var check in table.Controls.OfType<CheckBox>()) check.MaximumSize = new(available, 0);
        };
        return table;
    }
    public static void Add(TableLayoutPanel table, Control control, int gap = 14)
    {
        control.Margin = new(0, 0, 0, gap); table.RowStyles.Add(new(SizeType.AutoSize)); table.Controls.Add(control, 0, table.RowCount++);
    }
    public static Control Card(TableLayoutPanel content)
    {
        var card = new UiCard { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Padding = new(22), Margin = new(0), Radius = 14 };
        card.Controls.Add(content); return card;
    }
    public static Label Heading(string text) => new() { Text = text, AutoSize = true, Font = new("Segoe UI", 16, FontStyle.Bold), ForeColor = Ink };
    public static Label Note(string text) => new() { Text = text, AutoSize = true, ForeColor = Muted, MaximumSize = new(620, 0) };
    public static Control Columns(Control left, Control right)
    {
        var columns = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, BackColor = Canvas, Margin = new(0), Size = new(920, 500) };
        columns.ColumnStyles.Add(new(SizeType.Percent, 52)); columns.ColumnStyles.Add(new(SizeType.Percent, 48));
        left.Margin = new(0, 0, 10, 14); right.Margin = new(10, 0, 0, 14); columns.Controls.Add(left, 0, 0); columns.Controls.Add(right, 1, 0);
        bool changing = false;
        columns.SizeChanged += (_, _) =>
        {
            if (changing) return;
            int count = columns.Width < (int)(800 * columns.DeviceDpi / 96d) ? 1 : 2;
            if (columns.ColumnCount == count) return;
            changing = true; columns.SuspendLayout(); columns.Controls.Remove(left); columns.Controls.Remove(right);
            columns.ColumnStyles.Clear(); columns.RowStyles.Clear(); columns.ColumnCount = count; columns.RowCount = count == 1 ? 2 : 1;
            columns.ColumnStyles.Add(new(SizeType.Percent, count == 1 ? 100 : 52)); if (count == 2) columns.ColumnStyles.Add(new(SizeType.Percent, 48));
            left.Margin = new(0, 0, count == 2 ? 10 : 0, 14); right.Margin = new(count == 2 ? 10 : 0, 0, 0, 14);
            columns.Controls.Add(left, 0, 0); columns.Controls.Add(right, count == 1 ? 0 : 1, count == 1 ? 1 : 0); columns.ResumeLayout(true); changing = false;
        };
        return columns;
    }
    public static TabPage Page(string name, Control content)
    {
        var page = new TabPage(name) { BackColor = Canvas };
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new(22, 24, 22, 12), BackColor = Canvas };
        scroll.Controls.Add(content); page.Controls.Add(scroll); return page;
    }
    public static void StyleTabs(TabControl tabs)
    {
        if (SystemInformation.HighContrast) return;
        tabs.DrawMode = TabDrawMode.OwnerDrawFixed; tabs.SizeMode = TabSizeMode.Fixed; tabs.ItemSize = new(220, 48);
        tabs.DrawItem += (_, e) =>
        {
            bool selected = e.Index == tabs.SelectedIndex;
            using var brush = new SolidBrush(selected ? Paper : Canvas); e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, tabs.Font, e.Bounds, selected ? Accent : Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            if (selected) { using var line = new SolidBrush(Accent); e.Graphics.FillRectangle(line, e.Bounds.Left + 20, e.Bounds.Bottom - 3, e.Bounds.Width - 40, 3); }
            if (tabs.Focused && selected) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Bounds, -7, -7), Accent, Paper);
        };
    }
    public static GraphicsPath Round(RectangleF rectangle, float radius)
    {
        var path = new GraphicsPath(); float diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90); path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90); path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90); path.CloseFigure(); return path;
    }
    public static void DrawMark(Graphics graphics, RectangleF bounds)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var background = new SolidBrush(Accent); using var shape = Round(bounds, bounds.Width * .24f); graphics.FillPath(background, shape);
        float s = bounds.Width / 48; float x = bounds.X, y = bounds.Y;
        using var pen = new Pen(Color.White, 2.2f * s) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawRectangle(pen, x + 10 * s, y + 11 * s, 21 * s, 16 * s);
        graphics.DrawLine(pen, x + 20 * s, y + 28 * s, x + 20 * s, y + 33 * s); graphics.DrawLine(pen, x + 14 * s, y + 34 * s, x + 26 * s, y + 34 * s);
        using var fill = new SolidBrush(Accent); graphics.FillRectangle(fill, x + 27 * s, y + 22 * s, 12 * s, 17 * s); graphics.DrawRectangle(pen, x + 28 * s, y + 23 * s, 10 * s, 14 * s);
    }
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(64, 64); using (var graphics = Graphics.FromImage(bitmap)) DrawMark(graphics, new(1, 1, 62, 62));
        IntPtr handle = bitmap.GetHicon(); try { return (Icon)Icon.FromHandle(handle).Clone(); } finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}

internal sealed class UiCard : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 14;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor { get; set; } = UiTheme.Paper;
    public UiCard() { BackColor = UiTheme.Canvas; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); if (Width < 2 || Height < 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using var path = UiTheme.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), Radius * DeviceDpi / 96f);
        using var fill = new SolidBrush(FillColor); using var line = new Pen(UiTheme.Line); e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(line, path);
    }
}
internal sealed class BrandMark : Control
{
    public BrandMark() { TabStop = false; AccessibleName = "LocalRemote"; SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); float size = Math.Min(Width, Height) - 2; if (size > 0) UiTheme.DrawMark(e.Graphics, new(1, 1, size, size)); }
}
internal enum ButtonTone { Normal, Primary, Quiet, Danger }
internal sealed class UiButton : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ButtonTone Tone { get; set; }
    private bool hover, pressed;
    public UiButton()
    {
        AutoSize = true; MinimumSize = new(0, 40); Margin = new(0, 0, 8, 8); Padding = new(12, 6, 12, 6);
        if (SystemInformation.HighContrast) FlatStyle = FlatStyle.System;
        else { FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
    }
    public override Size GetPreferredSize(Size proposedSize)
    {
        Size text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine);
        return new(Math.Max(MinimumSize.Width, text.Width + Padding.Horizontal + 6), Math.Max(MinimumSize.Height, text.Height + Padding.Vertical));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast) { base.OnPaint(e); return; }
        if (Width < 2 || Height < 2) return;
        Color fill = Tone == ButtonTone.Primary ? UiTheme.Accent : UiTheme.Paper;
        Color ink = Tone == ButtonTone.Primary ? Color.White : Tone == ButtonTone.Danger ? Color.FromArgb(146, 46, 65) : UiTheme.Ink;
        Color border = Tone == ButtonTone.Primary ? UiTheme.Accent : UiTheme.Line;
        if (!Enabled) { fill = Color.FromArgb(239, 243, 245); ink = Color.FromArgb(122, 133, 142); border = UiTheme.Line; }
        else if (pressed) fill = Tone == ButtonTone.Primary ? Color.FromArgb(19, 73, 76) : Color.FromArgb(224, 235, 237);
        else if (hover) fill = Tone == ButtonTone.Primary ? Color.FromArgb(21, 83, 86) : Tone == ButtonTone.Danger ? Color.FromArgb(255, 238, 242) : UiTheme.Tint;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiTheme.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 7 * DeviceDpi / 96f); using var brush = new SolidBrush(fill); using var pen = new Pen(border);
        e.Graphics.FillPath(brush, path); if (Tone != ButtonTone.Quiet) e.Graphics.DrawPath(pen, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues) { using var focus = UiTheme.Round(new RectangleF(3, 3, Width - 6, Height - 6), 5 * DeviceDpi / 96f); using var focusPen = new Pen(Tone == ButtonTone.Primary ? Color.White : UiTheme.Accent, 2); e.Graphics.DrawPath(focusPen, focus); }
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) pressed = true; Invalidate(); base.OnKeyDown(e); }
    protected override void OnKeyUp(KeyEventArgs e) { pressed = false; Invalidate(); base.OnKeyUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
}
