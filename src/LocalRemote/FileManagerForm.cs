using System.Diagnostics;

namespace LocalRemote;

public sealed class FileManagerForm : Form
{
    private readonly Func<FileTransferClient> remote;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? transfer;
    private readonly ListView localList = NewList("Файлы на этом ПК");
    private readonly ListView remoteList = NewList("Файлы на втором ПК");
    private readonly TextBox localPath = new() { Dock = DockStyle.Fill, AccessibleName = "Папка на этом ПК" };
    private readonly TextBox remotePath = new() { Dock = DockStyle.Fill, AccessibleName = "Папка на втором ПК" };
    private readonly Button upload = new UiButton { Text = "Отправить на второй ПК →", Tone = ButtonTone.Primary, MinimumSize = new(190, 40) };
    private readonly Button download = new UiButton { Text = "← Скачать на этот ПК", MinimumSize = new(190, 40) };
    private readonly Button cancel = new UiButton { Text = "Отменить передачу", Tone = ButtonTone.Danger, Enabled = false, MinimumSize = new(150, 40) };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Maximum = 1000, Height = 20 };
    private readonly Label status = new() { AutoSize = true, Text = "Выберите файл и папку назначения." };
    private readonly ComboBox speedLimit = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Ограничение скорости передачи файлов" };
    private readonly Label metrics = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true, ForeColor = UiTheme.Muted, Text = "Выберите лимит перед передачей.", AccessibleName = "Скорость передачи и оставшееся время" };
    private readonly int[] rateLimits = [0, 32, 16, 4];
    private int remotePage;
    private bool busy;
    private bool closing;
    public FileManagerForm(Func<FileTransferClient> remote)
    {
        this.remote = remote;
        Text = "LocalRemote · файлы между компьютерами"; Font = new("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        Size = new(1100, 720); MinimumSize = new(850, 600); StartPosition = FormStartPosition.CenterParent;
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new(1060,500), SplitterDistance = 520, Panel1MinSize = 250, Panel2MinSize = 250, SplitterWidth = 12, BackColor = UiTheme.Canvas, Padding = new(12, 12, 12, 0) };
        split.Panel1.Controls.Add(BuildPane("Этот ПК", localPath, localList, false));
        split.Panel2.Controls.Add(BuildPane("Второй ПК", remotePath, remoteList, true));
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 154, Padding = new(18, 12, 18, 12), ColumnCount = 1, RowCount = 3, BackColor = UiTheme.Paper };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        actions.Controls.Add(upload); actions.Controls.Add(download); actions.Controls.Add(cancel);
        speedLimit.Items.AddRange(["Без лимита", "32 МиБ/с", "16 МиБ/с", "4 МиБ/с · для стрима"]); speedLimit.SelectedIndex = 0;
        var rateRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new(0, 0, 0, 8) };
        rateRow.ColumnStyles.Add(new(SizeType.AutoSize)); rateRow.ColumnStyles.Add(new(SizeType.Absolute, 210)); rateRow.ColumnStyles.Add(new(SizeType.Percent, 100));
        rateRow.Controls.Add(new Label { Text = "Скорость:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(0, 0, 12, 0) }, 0, 0);
        rateRow.Controls.Add(speedLimit, 1, 0); rateRow.Controls.Add(metrics, 2, 0);
        footer.RowStyles.Add(new(SizeType.Absolute, 54)); footer.RowStyles.Add(new(SizeType.Absolute, 44)); footer.RowStyles.Add(new(SizeType.Percent, 100));
        footer.Controls.Add(actions, 0, 0); footer.Controls.Add(rateRow, 0, 1); footer.Controls.Add(progress, 0, 2);
        Controls.Add(split); Controls.Add(footer);
        Controls.Add(UiTheme.Footer(status)); status.ForeColor = UiTheme.Muted;
        UiTheme.Apply(this);
        localPath.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        upload.Click += async (_, _) => await TransferAsync(true);
        download.Click += async (_, _) => await TransferAsync(false);
        cancel.Click += (_, _) => transfer?.Cancel();
        localList.DoubleClick += async (_, _) => await NavigateSelectionAsync(false);
        remoteList.DoubleClick += async (_, _) => await NavigateSelectionAsync(true);
        localList.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; await NavigateSelectionAsync(false); } };
        remoteList.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; await NavigateSelectionAsync(true); } };
        localPath.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await RefreshLocalAsync(); } };
        remotePath.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; remotePage = 0; await RefreshRemoteAsync(); } };
        Shown += async (_, _) => { await RefreshLocalAsync(); await RefreshRemoteAsync(); };
        FormClosing += (_, _) => { closing = true; lifetime.Cancel(); transfer?.Cancel(); };
    }
    private Control BuildPane(string title, TextBox path, ListView list, bool isRemote)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(8), ColumnCount = 1, RowCount = 4, BackColor = UiTheme.Paper };
        table.RowStyles.Add(new(SizeType.AutoSize)); table.RowStyles.Add(new(SizeType.AutoSize)); table.RowStyles.Add(new(SizeType.AutoSize)); table.RowStyles.Add(new(SizeType.Percent, 100));
        table.Controls.Add(new Label { Text = title, AutoSize = true, Font = new("Segoe UI", 15, FontStyle.Bold), Margin = new(0, 0, 0, 12) }, 0, 0);
        table.Controls.Add(UiTheme.Field(path, 44), 0, 1);
        var tools = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var roots = new UiButton { Text = "Диски" }; roots.Click += async (_, _) => { if (isRemote) { remotePath.Clear(); remotePage = 0; await RefreshRemoteAsync(); } else { localPath.Clear(); await RefreshLocalAsync(); } };
        var parent = new UiButton { Text = "Выше" }; parent.Click += async (_, _) =>
        {
            try { path.Text = path.Text.Length == 0 ? "" : Directory.GetParent(path.Text)?.FullName ?? ""; if (isRemote) { remotePage = 0; await RefreshRemoteAsync(); } else await RefreshLocalAsync(); }
            catch (Exception ex) { ShowError(ex.Message); }
        };
        var refresh = new UiButton { Text = "Обновить" }; refresh.Click += async (_, _) => { if (isRemote) await RefreshRemoteAsync(); else await RefreshLocalAsync(); };
        tools.Controls.Add(roots); tools.Controls.Add(parent); tools.Controls.Add(refresh);
        if (isRemote)
        {
            var previous = new UiButton { Text = "‹", AccessibleName = "Предыдущая страница", MinimumSize = new(40, 40) }; previous.Click += async (_, _) => { if (remotePage > 0) { remotePage--; await RefreshRemoteAsync(); } };
            var next = new UiButton { Text = "›", AccessibleName = "Следующая страница", MinimumSize = new(40, 40) }; next.Click += async (_, _) => { remotePage++; await RefreshRemoteAsync(); };
            tools.Controls.Add(previous); tools.Controls.Add(next);
        }
        foreach (Button button in tools.Controls.OfType<Button>())
        {
            button.Padding = new(6, 6, 6, 6); button.Margin = new(0, 0, 4, 8);
        }
        tools.Margin = new(0, 10, 0, 6); table.Controls.Add(tools, 0, 2); table.Controls.Add(list, 0, 3);
        var card = new UiCard { Dock = DockStyle.Fill, Padding = new(10), Margin = new(0) }; card.Controls.Add(table); return card;
    }
    private static ListView NewList(string name)
    {
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false, AccessibleName = name };
        list.Columns.Add("Имя", 260); list.Columns.Add("Размер", 90); list.Columns.Add("Изменён", 130); return list;
    }
    private void Fill(ListView list, FileEntry[] entries)
    {
        if (closing) return; list.BeginUpdate(); list.Items.Clear();
        foreach (var entry in entries)
        {
            var row = new ListViewItem((entry.Directory ? "[Папка] " : "") + entry.Name) { Tag = entry, BackColor = list.Items.Count % 2 == 0 ? UiTheme.Paper : UiTheme.Canvas, ForeColor = UiTheme.Ink };
            row.SubItems.Add(entry.Directory ? "" : FormatBytes(entry.Length)); row.SubItems.Add(entry.Modified == default ? "" : entry.Modified.ToString("dd.MM.yyyy HH:mm")); list.Items.Add(row);
        }
        list.EndUpdate();
    }
    private async Task RefreshLocalAsync()
    {
        try
        {
            string folder = localPath.Text;
            var entries = await Task.Run(() => folder.Length == 0 ? DriveInfo.GetDrives().Select(d => new FileEntry(d.Name, d.Name, true)).ToArray() : Directory.EnumerateFileSystemEntries(folder).Select(p =>
            {
                bool directory = Directory.Exists(p); var info = directory ? (FileSystemInfo)new DirectoryInfo(p) : new FileInfo(p);
                return new FileEntry(info.Name, info.FullName, directory, directory ? 0 : ((FileInfo)info).Length, info.LastWriteTime);
            }).OrderBy(e => e.Directory ? 0 : 1).ThenBy(e => e.Name).ToArray(), lifetime.Token);
            Fill(localList, entries);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError("Не удалось открыть папку на этом ПК: " + ex.Message); }
    }
    private async Task RefreshRemoteAsync()
    {
        try
        {
            var reply = await remote().ListAsync(remotePath.Text, remotePage, lifetime.Token);
            if (closing) return; remotePath.Text = reply.Path; Fill(remoteList, reply.Entries ?? []);
            if (!busy) status.Text = remotePath.Text.Length == 0 ? "Откройте диск второго ПК." : $"Второй ПК · страница {remotePage + 1}" + (reply.HasMore ? " · есть следующая страница" : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError("Не удалось открыть папку второго ПК: " + ex.Message); }
    }
    private async Task NavigateSelectionAsync(bool isRemote)
    {
        var list = isRemote ? remoteList : localList;
        if (list.SelectedItems.Count != 1 || list.SelectedItems[0].Tag is not FileEntry { Directory: true } entry) return;
        if (isRemote) { remotePath.Text = entry.Path; remotePage = 0; await RefreshRemoteAsync(); }
        else { localPath.Text = entry.Path; await RefreshLocalAsync(); }
    }
    private async Task TransferAsync(bool sending)
    {
        if (busy) return;
        var selected = (sending ? localList : remoteList).SelectedItems.Cast<ListViewItem>().Select(i => (FileEntry)i.Tag!).ToArray();
        if (selected.Length == 0 || selected.Any(e => e.Directory)) { ShowError("Выберите один или несколько файлов. Передача папок пока не поддерживается."); return; }
        string destination = sending ? remotePath.Text : localPath.Text;
        if (destination.Length == 0) { ShowError("Откройте папку назначения."); return; }
        int rateLimitMiB = rateLimits[speedLimit.SelectedIndex];
        busy = true; upload.Enabled = download.Enabled = speedLimit.Enabled = false; cancel.Enabled = true;
        transfer = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            foreach (var entry in selected)
            {
                bool exists = sending ? remoteList.Items.Cast<ListViewItem>().Any(i => i.Tag is FileEntry remoteEntry && remoteEntry.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)) : File.Exists(Path.Combine(destination, entry.Name));
                bool overwrite = false;
                if (exists)
                {
                    var answer = MessageBox.Show(this, $"В папке назначения уже есть «{entry.Name}». Заменить файл?", "Передача файла", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (answer == DialogResult.Cancel) throw new OperationCanceledException();
                    if (answer == DialogResult.No) continue; overwrite = true;
                }
                var watch = Stopwatch.StartNew(); long lastBytes = 0; double lastSample = 0, bytesPerSecond = 0;
                bool fileActive = true; metrics.Text = "Измерение скорости…";
                var updates = new Progress<TransferProgress>(p =>
                {
                    if (closing || !fileActive) return; progress.Value = p.Total == 0 ? 1000 : (int)Math.Clamp(p.Completed * 1000d / p.Total, 0, 1000);
                    status.Text = (sending ? "Отправка: " : "Скачивание: ") + entry.Name + $" · {FormatBytes(p.Completed)} / {FormatBytes(p.Total)}";
                    double elapsed = watch.Elapsed.TotalSeconds;
                    if (elapsed - lastSample >= .5)
                    {
                        bytesPerSecond = (p.Completed - lastBytes) / (elapsed - lastSample); lastBytes = p.Completed; lastSample = elapsed;
                    }
                    if (bytesPerSecond > 0)
                    {
                        double remaining = Math.Max(0, p.Total - p.Completed) / bytesPerSecond;
                        metrics.Text = $"{bytesPerSecond / (1024 * 1024):0.0} МиБ/с · осталось {FormatTime(remaining)}";
                    }
                });
                progress.Value = 0;
                try
                {
                    if (sending) await remote().UploadAsync(entry.Path, destination, overwrite, updates, transfer.Token, rateLimitMiB);
                    else await remote().DownloadAsync(entry.Path, Path.Combine(destination, entry.Name), overwrite, updates, transfer.Token, rateLimitMiB);
                    watch.Stop();
                    if (!closing) metrics.Text = $"Средняя скорость: {entry.Length / Math.Max(.001, watch.Elapsed.TotalSeconds) / (1024 * 1024):0.0} МиБ/с";
                }
                finally { fileActive = false; }
            }
            if (!closing) { progress.Value = 1000; status.Text = "Передача завершена. Контрольные суммы файлов проверены."; }
            await RefreshLocalAsync(); await RefreshRemoteAsync();
        }
        catch (OperationCanceledException) { if (!closing) status.Text = "Передача отменена."; }
        catch (Exception ex) { ShowError("Не удалось передать файл: " + ex.Message); }
        finally { transfer.Dispose(); transfer = null; busy = false; if (!closing) { upload.Enabled = download.Enabled = speedLimit.Enabled = true; cancel.Enabled = false; } }
    }
    private static string FormatTime(double seconds)
    {
        seconds = Math.Min(seconds, 365 * 24 * 3600d);
        var time = TimeSpan.FromSeconds(Math.Ceiling(seconds));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours} ч {time.Minutes:00} мин" : time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes} мин {time.Seconds:00} с" : $"{time.Seconds} с";
    }
    private void ShowError(string text) { if (!closing) MessageBox.Show(this, text, "LocalRemote · файлы", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.0} ГБ" : bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.0} МБ" : bytes >= 1024 ? $"{bytes / 1024d:0.0} КБ" : $"{bytes} Б";
}
