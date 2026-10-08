using System.Diagnostics;
using System.Net;

namespace LocalRemote;

public sealed class MainForm : Form
{
    private readonly bool autoHost;
    private readonly bool startInTray;
    private readonly Label hostStatus = new() { AutoSize = true, Text = "Доступ выключен." };
    private readonly TextBox hostCode = new() { ReadOnly = true, Multiline = true, Height = 84, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, AccessibleName = "Код подключения к этому ПК" };
    private readonly TextBox viewerCode = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, AccessibleName = "Код подключения со второго ПК" };
    private readonly ComboBox addresses = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Адрес этого компьютера" };
    private readonly Button hostToggle = new UiButton { Text = "Включить доступ", Tone = ButtonTone.Primary, MinimumSize = new(160, 40) };
    private readonly Button connect = new UiButton { Text = "Подключиться", Tone = ButtonTone.Primary, MinimumSize = new(180, 44) };
    private readonly CheckBox startup = new() { Text = "Запускать доступ после входа в Windows", AutoSize = true };
    private readonly CheckBox startupAdmin = new() { Text = "Запускать с правами администратора", AutoSize = true };
    private readonly Button saveStartup = new UiButton { Text = "Сохранить автозапуск", MinimumSize = new(160, 40) };
    private readonly Button restartAdmin = new UiButton { Text = "Перезапустить хост от администратора", MinimumSize = new(160, 40) };
    private readonly Label startupStatus = new() { AutoSize = true, MaximumSize = new(680, 0) };
    private readonly Label rightsStatus = new() { AutoSize = true, MaximumSize = new(680, 0) };
    private readonly ToolStripMenuItem trayHostToggle = new("Включить доступ");
    private readonly System.Windows.Forms.Timer networkRefresh = new() { Interval = 5000 };
    private readonly CheckBox remember = new() { Text = "Запомнить код на этом ПК", AutoSize = true };
    private readonly NotifyIcon tray = new() { Icon = UiTheme.AppIcon, Text = "LocalRemote · доступ выключен", Visible = true };
    private HostIdentity? identity;
    private HostServer? server;
    private bool exiting;
    private bool busy;
    private bool startupBusy;

    public MainForm(bool autoHost, bool startInTray)
    {
        this.autoHost = autoHost; this.startInTray = startInTray;
        Text = "LocalRemote · управление в локальной сети";
        Font = new("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new(760, 640); Size = new(1040, 860); StartPosition = FormStartPosition.CenterScreen;
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new(18, 8) };
        tabs.TabPages.Add(BuildViewerTab()); tabs.TabPages.Add(BuildHostTab());
        Controls.Add(tabs);
        Controls.Add(UiTheme.Header());
        Controls.Add(UiTheme.Footer(new Label { Text = "Локальная сеть · v0.4.0", AutoSize = true, ForeColor = UiTheme.Muted, Font = new("Segoe UI", 9) }));
        UiTheme.StyleTabs(tabs); UiTheme.Apply(this);
        if (autoHost) tabs.SelectedIndex = 1;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть LocalRemote", null, (_, _) => ShowWindow());
        trayHostToggle.Click += async (_, _) => { if (server == null) await StartHostAsync(); else await StopHostAsync(); };
        menu.Items.Add(trayHostToggle);
        menu.Items.Add("Отключить подключённый ПК", null, (_, _) => server?.DisconnectViewer());
        menu.Items.Add("Выход", null, (_, _) => ExitApp());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowWindow();
        RefreshAddresses(); addresses.SelectedIndexChanged += (_, _) => UpdateCode();
        networkRefresh.Tick += (_, _) => RefreshAddresses(); networkRefresh.Start();
        hostToggle.Click += async (_, _) => { if (server == null) await StartHostAsync(); else await StopHostAsync(); };
        connect.Click += async (_, _) => await ConnectAsync();
        viewerCode.Text = Settings.LoadInvitation(); remember.Checked = viewerCode.Text.Length > 0;
        var startupState = Startup.ReadStatus();
        startup.Checked = startupState.Mode != StartupMode.Off;
        startupAdmin.Checked = startupState.Mode == StartupMode.Administrator || (startupState.Mode == StartupMode.Off && Startup.CanElevate);
        RefreshStartupStatus(startupState);
        startup.CheckedChanged += (_, _) => { UpdateStartupControls(); startupStatus.Text = "Нажмите «Сохранить автозапуск», чтобы применить настройки."; };
        startupAdmin.CheckedChanged += (_, _) => startupStatus.Text = "Нажмите «Сохранить автозапуск», чтобы применить настройки.";
        saveStartup.Click += async (_, _) => await SaveStartupAsync();
        restartAdmin.Click += async (_, _) => await RestartAsAdministratorAsync();
        rightsStatus.Text = Startup.IsElevated ? "Сейчас: права администратора. Можно управлять обычными и повышенными окнами." : "Сейчас: обычные права. Для повышенных окон перезапустите хост от администратора.";
        Shown += async (_, _) => { if (startInTray) Hide(); if (autoHost) await StartHostAsync(); };
        FormClosing += (_, e) =>
        {
            if (!exiting && server != null && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true; Hide();
                tray.ShowBalloonTip(3000, "LocalRemote", "Доступ продолжает работать. Открыть или выключить его можно через значок в области уведомлений.", ToolTipIcon.Info);
            }
        };
        FormClosed += (_, _) => { tray.Visible = false; tray.Dispose(); identity?.Dispose(); };
    }

    private TabPage BuildHostTab()
    {
        var table = UiTheme.Stack();
        Add(table, UiTheme.Heading("Доступ к этому ПК"));
        Add(table, Description("Включите доступ на стриминговом компьютере и передайте код на основной ПК."));
        hostStatus.BackColor = UiTheme.Tint; hostStatus.ForeColor = UiTheme.Accent; hostStatus.Padding = new(10, 8, 10, 8);
        Add(table, hostStatus);
        Add(table, new Label { Text = "Адрес этого ПК · Wi-Fi или Ethernet", AutoSize = true });
        Add(table, UiTheme.Field(addresses));
        Add(table, new Label { Text = "Код подключения", AutoSize = true });
        hostCode.Font = new("Consolas", 10); hostCode.PlaceholderText = "Включите доступ, чтобы получить код подключения.";
        Add(table, UiTheme.Field(hostCode, 96));
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        buttons.Controls.Add(hostToggle);
        var copy = MakeButton("Скопировать код"); copy.Click += (_, _) => { if (hostCode.Text.Length > 0) Clipboard.SetText(hostCode.Text); };
        buttons.Controls.Add(copy);
        var disconnect = MakeButton("Отключить зрителя"); disconnect.Click += (_, _) => server?.DisconnectViewer(); buttons.Controls.Add(disconnect);
        Add(table, buttons);
        var firewall = MakeButton("Разрешить доступ в брандмауэре…");
        firewall.Click += (_, _) => ConfigureFirewall(); Add(table, firewall);
        Add(table, Description("Разрешите доступ в сети один раз. Передавайте код только на свой ПК; звук остаётся на стриминговом компьютере."), 0);
        var options = UiTheme.Stack();
        Add(options, UiTheme.Heading("Автозапуск и права"));
        Add(options, Description("Подключайтесь после входа в Windows без ручного запуска программы."));
        Add(options, startup);
        Add(options, startupAdmin);
        var startupButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        startupButtons.Controls.Add(saveStartup); startupButtons.Controls.Add(restartAdmin); Add(options, startupButtons);
        startupStatus.ForeColor = UiTheme.Muted; rightsStatus.ForeColor = UiTheme.Muted;
        Add(options, startupStatus); Add(options, rightsStatus);
        Add(options, Description("Права администратора нужны для повышенных окон CMD и системных утилит. Запрос Windows подтвердите на этом ПК. Защищённый экран UAC доступен только локально."));
        Add(options, Description("Закрытие окна оставляет доступ возле часов. Для остановки нажмите «Выключить доступ» или выберите «Выход» в меню значка."), 0);
        return UiTheme.Page("Этот ПК управляется", UiTheme.Columns(UiTheme.Card(table), UiTheme.Card(options)));
    }
    private TabPage BuildViewerTab()
    {
        var table = UiTheme.Stack();
        Add(table, UiTheme.Heading("Подключиться к компьютеру"));
        Add(table, Description("Вставьте код со стримингового ПК. Оба компьютера должны быть в одной домашней сети."));
        Add(table, new Label { Text = "Код подключения со второго ПК", AutoSize = true });
        Add(table, UiTheme.Field(viewerCode));
        var reveal = new CheckBox { Text = "Показать код", AutoSize = true };
        reveal.CheckedChanged += (_, _) => viewerCode.UseSystemPasswordChar = !reveal.Checked; Add(table, reveal);
        Add(table, remember);
        Add(table, connect);
        Add(table, Description("Код можно сохранить на этом ПК для следующего подключения."), 0);
        var guide = UiTheme.Stack();
        Add(guide, UiTheme.Heading("Всё под рукой"));
        Add(guide, new Label { Text = "01   Рабочий стол", AutoSize = true, Font = new("Segoe UI", 11, FontStyle.Bold), ForeColor = UiTheme.Accent });
        Add(guide, Description("Щёлкните по изображению для управления. F12 возвращает мышь и клавиатуру вашему ПК."));
        Add(guide, new Label { Text = "02   Чёткое изображение", AutoSize = true, Font = new("Segoe UI", 11, FontStyle.Bold), ForeColor = UiTheme.Accent });
        Add(guide, Description("Выберите 720p или 1080p прямо в окне сеанса. Частота кадров отображается внизу."));
        Add(guide, new Label { Text = "03   Файлы в обе стороны", AutoSize = true, Font = new("Segoe UI", 11, FontStyle.Bold), ForeColor = UiTheme.Accent });
        Add(guide, Description("Кнопка «Файлы…» открывает отправку и скачивание по локальной сети."));
        Add(guide, Description("Звук остаётся на втором ПК. Доступ работает после входа в Windows; защищённые запросы UAC подтверждаются локально."), 0);
        return UiTheme.Page("Подключиться", UiTheme.Columns(UiTheme.Card(table), UiTheme.Card(guide)));
    }
    private static void Add(TableLayoutPanel panel, Control control, int gap = 12) => UiTheme.Add(panel, control, gap);
    private static Label Description(string text) => UiTheme.Note(text);
    private static Button MakeButton(string text) => new UiButton { Text = text, MinimumSize = new(100, 40) };
    private void UpdateStartupControls()
    {
        startup.Enabled = saveStartup.Enabled = !startupBusy;
        startupAdmin.Enabled = !startupBusy && startup.Checked && Startup.CanElevate;
        restartAdmin.Enabled = !startupBusy && !Startup.IsElevated && Startup.CanElevate;
    }
    private void RefreshStartupStatus(StartupStatus state)
    {
        startupStatus.Text = state.Error.Length > 0 ? state.Error : state.Mode switch
        {
            StartupMode.Administrator => "Автозапуск включён: после входа, с правами администратора, в области уведомлений.",
            StartupMode.User => "Автозапуск включён: после входа, с обычными правами, в области уведомлений.",
            _ => "Автозапуск выключен. Текущий доступ выключается кнопкой «Выключить доступ»."
        };
        UpdateStartupControls();
    }
    private async Task SaveStartupAsync()
    {
        if (startupBusy || busy) return;
        StartupMode mode = !startup.Checked ? StartupMode.Off : startupAdmin.Checked ? StartupMode.Administrator : StartupMode.User;
        startupBusy = true; UpdateStartupControls(); hostToggle.Enabled = false;
        bool saved = false;
        try
        {
            await Startup.ConfigureAsync(mode);
            saved = true;
            RefreshStartupStatus(Startup.ReadStatus());
            if (mode == StartupMode.Administrator && !Startup.IsElevated)
            {
                Startup.StartElevatedReplacement(); await ExitForReplacementAsync();
            }
            else if (mode != StartupMode.Off && server == null) await StartHostAsync();
        }
        catch (OperationCanceledException) { RefreshStartupStatus(Startup.ReadStatus()); startupStatus.Text += " Запрос Windows отменён; новые настройки не применены."; }
        catch (Exception ex) { RefreshStartupStatus(Startup.ReadStatus()); ShowError((saved ? "Автозапуск сохранён, но хост не удалось перезапустить. Нажмите «Перезапустить хост от администратора»." : "Не удалось настроить автозапуск.") + "\n\n" + ex.Message); }
        finally { startupBusy = false; if (!IsDisposed) { UpdateStartupControls(); hostToggle.Enabled = !busy; } }
    }
    private async Task RestartAsAdministratorAsync()
    {
        if (busy || startupBusy || Startup.IsElevated) return;
        restartAdmin.Enabled = false;
        try { Startup.StartElevatedReplacement(); await ExitForReplacementAsync(); }
        catch (OperationCanceledException) { hostStatus.Text = "Перезапуск отменён. Текущий доступ продолжает работать."; }
        catch (Exception ex) { ShowError("Не удалось перезапустить хост.\n\n" + ex.Message); }
        finally { if (!IsDisposed) UpdateStartupControls(); }
    }
    private async Task ExitForReplacementAsync() { await StopHostAsync(); exiting = true; Close(); }
    private void RefreshAddresses()
    {
        try
        {
            var previous = (addresses.SelectedItem as LocalAddress)?.Address;
            var current = LanAddresses.GetAdapters();
            if (current.Length == 0) current = [new(IPAddress.Loopback, "Только этот ПК — ожидаю Wi-Fi или Ethernet")];
            if (addresses.Items.Cast<LocalAddress>().SequenceEqual(current)) return;
            addresses.BeginUpdate(); addresses.Items.Clear(); addresses.Items.AddRange(current.Cast<object>().ToArray());
            int index = Array.FindIndex(current, address => address.Address.Equals(previous)); addresses.SelectedIndex = Math.Max(0, index); addresses.EndUpdate(); UpdateCode();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
    }
    private async Task StartHostAsync()
    {
        if (busy) return; busy = true; hostToggle.Enabled = false;
        try
        {
            identity ??= HostIdentity.LoadOrCreate();
            var capture = new ScreenCapture(); var input = new InputController();
            server = new(identity, ScreenCapture.GetMonitors(), capture.Capture, input.Apply, input.ReleaseAll, configureCapture: capture.Configure, videoFactory: FfmpegTools.FindExecutable() != null ? H264Encoder.StartDesktopAsync : null);
            var startedServer = server;
            server.StatusChanged += status => OnUi(() => { if (ReferenceEquals(server, startedServer)) { hostStatus.Text = status; tray.Text = "LocalRemote · доступ включён"; } });
            server.Start(); hostToggle.Text = trayHostToggle.Text = "Выключить доступ"; UpdateCode();
        }
        catch (Exception ex)
        {
            if (server != null) { await server.DisposeAsync(); server = null; }
            hostStatus.Text = "Не удалось включить доступ: " + ex.Message;
            if (startInTray) { ShowWindow(); tray.ShowBalloonTip(5000, "LocalRemote: ошибка запуска", ex.Message, ToolTipIcon.Error); }
            else ShowError(ex.Message);
        }
        finally { busy = false; hostToggle.Enabled = true; }
    }
    private async Task StopHostAsync()
    {
        if (busy || server == null) return; busy = true; hostToggle.Enabled = false;
        try { var old = server; server = null; await old.DisposeAsync(); hostStatus.Text = "Доступ выключен."; hostCode.Clear(); hostToggle.Text = trayHostToggle.Text = "Включить доступ"; tray.Text = "LocalRemote · доступ выключен"; }
        finally { busy = false; hostToggle.Enabled = true; }
    }
    private void UpdateCode()
    {
        if (server != null && identity != null && addresses.SelectedItem is LocalAddress address) hostCode.Text = new Invitation(address.Address.ToString(), server.Port, identity.Fingerprint, identity.Secret).ToString();
    }
    private async Task ConnectAsync()
    {
        connect.Enabled = false; var client = new ViewerClient();
        try
        {
            var invitation = Invitation.Parse(viewerCode.Text);
            var welcome = await client.ConnectAsync(invitation);
            Settings.SaveInvitation(remember.Checked ? invitation.ToString() : null);
            var viewer = new ViewerForm(client, welcome, invitation); viewer.Show(this);
        }
        catch (Exception ex) { await client.DisposeAsync(); ShowError("Не удалось подключиться. Проверьте код, включённый хост и брандмауэр.\n\n" + ex.Message); viewerCode.Focus(); }
        finally { connect.Enabled = true; }
    }
    private void ConfigureFirewall()
    {
        string script = Path.Combine(AppContext.BaseDirectory, "Configure-Firewall.ps1");
        if (!File.Exists(script)) { ShowError("Рядом с EXE отсутствует Configure-Firewall.ps1. Скопируйте всю папку приложения."); return; }
        try { Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -ProgramPath \"{Environment.ProcessPath}\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }); }
        catch (Exception ex) { ShowError("Не удалось настроить брандмауэр: " + ex.Message); }
    }
    private void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void OnUi(Action action) { if (!IsDisposed && IsHandleCreated) { try { BeginInvoke(action); } catch (InvalidOperationException) { } } }
    private void ShowError(string message) => MessageBox.Show(this, message, "LocalRemote", MessageBoxButtons.OK, MessageBoxIcon.Error);
    private async void ExitApp() { await StopHostAsync(); exiting = true; Close(); }
    protected override void Dispose(bool disposing) { if (disposing) { networkRefresh.Dispose(); tray.Dispose(); } base.Dispose(disposing); }
}
