using System.Diagnostics;

namespace LocalRemote;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string? Value(string key) { int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
        if (Value("--require-user") is string sid && sid != Startup.UserSid)
        {
            MessageBox.Show("Подтвердите запрос Windows под той же учётной записью, в которой работает LocalRemote. Для другого пользователя код доступа и рабочий стол отличаются.", "LocalRemote", MessageBoxButtons.OK, MessageBoxIcon.Error); return 2;
        }
        if (args.Contains("--configure-startup"))
        {
            try
            {
                if (!Enum.TryParse<StartupMode>(Value("--configure-startup"), out var mode) || !Enum.IsDefined(mode)) throw new IOException("Неизвестная настройка автозапуска.");
                Startup.Apply(mode, Value("--require-user") ?? throw new IOException("Не указана учётная запись.")); return 0;
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "LocalRemote · автозапуск", MessageBoxButtons.OK, MessageBoxIcon.Error); return 2; }
        }
        if (int.TryParse(Value("--wait-for-parent"), out int parentId) && parentId > 0 && parentId != Environment.ProcessId)
        {
            try
            {
                using var parent = Process.GetProcessById(parentId);
                if (parent.MainModule?.FileName?.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) != true || !parent.WaitForExit(20000))
                    throw new IOException("Предыдущая копия программы ещё работает. Закройте её через «Выход» возле часов и запустите LocalRemote заново.");
            }
            catch (ArgumentException) { }
            catch (Exception ex) { MessageBox.Show(ex.Message, "LocalRemote", MessageBoxButtons.OK, MessageBoxIcon.Error); return 2; }
        }
        Mutex mutex; bool first;
        // Keep the original mutex name to prevent a legacy host and LocalRemote running together.
        try { mutex = new Mutex(true, @"Local\NewAnyDesk-" + Environment.UserName, out first); }
        catch (UnauthorizedAccessException) { if (!args.Contains("--tray")) MessageBox.Show("LocalRemote уже работает с правами администратора. Откройте его через значок возле часов.", "LocalRemote"); return 0; }
        using var instance = mutex;
        if (!first)
        {
            if (!args.Contains("--tray")) MessageBox.Show("LocalRemote уже работает. Откройте его через значок в области уведомлений.", "LocalRemote");
            return 0;
        }
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "Ошибка LocalRemote", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new MainForm(args.Contains("--host"), args.Contains("--tray")));
        return 0;
    }
}
