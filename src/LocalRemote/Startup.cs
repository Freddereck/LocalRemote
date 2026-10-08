using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using Microsoft.Win32;

namespace LocalRemote;

public enum StartupMode { Off, User, Administrator }
public sealed record StartupStatus(StartupMode Mode, bool HasTask = false, string Error = "", bool Legacy = false);

public static class Startup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string TaskArguments = "--host --tray --wait-for-parent $(Arg0)";
    public static string UserSid { get { using var identity = WindowsIdentity.GetCurrent(); return identity.User!.Value; } }
    public static bool IsElevated { get { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); } }
    public static bool CanElevate { get { using var identity = WindowsIdentity.GetCurrent(); return identity.Groups?.Any(g => g.Value == "S-1-5-32-544") == true; } }
    public static string TaskName(string sid) { _ = new SecurityIdentifier(sid); return "LocalRemote-Host-" + sid; }
    private static string LegacyTaskName(string sid) { _ = new SecurityIdentifier(sid); return "NewAnyDesk-Host-" + sid; }
    public static string ExecutablePath()
    {
        string path = Environment.ProcessPath ?? throw new IOException("Не удалось определить путь программы.");
        if (!Path.GetFileName(path).Equals("LocalRemote.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("Настраивайте автозапуск из собранного LocalRemote.exe.");
        return Path.GetFullPath(path);
    }
    public static string BuildTaskXml(string path, string sid)
    {
        _ = TaskName(sid);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\")) throw new IOException("Сохраните программу на локальном диске.");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, object value) => new(ns + name, value);
        return new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", E("Description", "LocalRemote: доступ после входа в Windows, в рабочем столе этой учётной записи.")),
            E("Triggers", new XElement(ns + "LogonTrigger", E("Enabled", true), E("UserId", sid))),
            E("Principals", new XElement(ns + "Principal", new XAttribute("id", "Host"), E("UserId", sid), E("LogonType", "InteractiveToken"), E("RunLevel", "HighestAvailable"))),
            E("Settings", new object[] { E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", false), E("StopIfGoingOnBatteries", false),
                E("AllowHardTerminate", false), E("StartWhenAvailable", true), E("RunOnlyIfNetworkAvailable", false), E("AllowStartOnDemand", true),
                E("Enabled", true), E("Hidden", false), E("RunOnlyIfIdle", false), E("WakeToRun", false), E("ExecutionTimeLimit", "PT0S"), E("Priority", 7),
                new XElement(ns + "RestartOnFailure", E("Interval", "PT1M"), E("Count", 3)) }),
            new XElement(ns + "Actions", new XAttribute("Context", "Host"), new XElement(ns + "Exec", E("Command", path), E("Arguments", TaskArguments), E("WorkingDirectory", Path.GetDirectoryName(path)!))))).ToString();
    }
    private static object ConnectService()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service") ?? throw new IOException("Планировщик задач Windows недоступен."))!;
        try { service.Connect(); return service; } catch { Release(service); throw; }
    }
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    public static StartupStatus ReadStatus()
    {
        bool userStartup, legacyUser;
        using (var key = Registry.CurrentUser.OpenSubKey(Key)) { userStartup = key?.GetValue("LocalRemote") is string; legacyUser = key?.GetValue("NewAnyDesk") is string; }
        bool legacyTask = false;
        object? service = null, folder = null, task = null;
        try
        {
            service = ConnectService(); folder = ((dynamic)service).GetFolder(@"\");
            try { task = ((dynamic)folder).GetTask(TaskName(UserSid)); }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002))
            {
                try { task = ((dynamic)folder).GetTask(LegacyTaskName(UserSid)); legacyTask = true; }
                catch (Exception missing) when (missing.HResult == unchecked((int)0x80070002))
                {
                    return new(userStartup || legacyUser ? StartupMode.User : StartupMode.Off,
                        Error: legacyUser ? "Программа переименована. Сохраните автозапуск заново." : "", Legacy: legacyUser);
                }
            }
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var xml = XDocument.Parse((string)((dynamic)task).Xml);
            bool enabled = (bool)((dynamic)task).Enabled;
            string path = xml.Descendants(ns + "Command").SingleOrDefault()?.Value ?? "";
            string error = !path.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) ? "Папка программы изменилась. Сохраните автозапуск заново." : "";
            if (xml.Descendants(ns + "RunLevel").SingleOrDefault()?.Value != "HighestAvailable" || xml.Descendants(ns + "LogonType").SingleOrDefault()?.Value != "InteractiveToken" || xml.Descendants(ns + "Arguments").SingleOrDefault()?.Value != TaskArguments)
                error = "Задание автозапуска изменено. Сохраните настройки заново.";
            if (legacyTask || legacyUser) error = "Программа переименована. Сохраните автозапуск заново.";
            return new(enabled ? StartupMode.Administrator : userStartup || legacyUser ? StartupMode.User : StartupMode.Off, true, error, legacyTask || legacyUser);
        }
        catch (Exception ex) { return new(userStartup || legacyUser ? StartupMode.User : StartupMode.Off, Error: "Не удалось проверить автозапуск: " + ex.Message); }
        finally { Release(task); Release(folder); Release(service); }
    }
    public static void Apply(StartupMode mode, string expectedSid)
    {
        if (!Enum.IsDefined(mode)) throw new IOException("Неизвестная настройка автозапуска.");
        if (expectedSid != UserSid) throw new IOException("Подтвердите запрос Windows под той же учётной записью, в которой работает LocalRemote.");
        string path = ExecutablePath();
        object? service = null, folder = null, task = null;
        try
        {
            service = ConnectService(); folder = ((dynamic)service).GetFolder(@"\");
            if (mode == StartupMode.Administrator)
            {
                if (!IsElevated) throw new IOException("Для этого автозапуска нужны права администратора.");
                task = ((dynamic)folder).RegisterTask(TaskName(expectedSid), BuildTaskXml(path, expectedSid), 6, expectedSid, null, 3, null);
            }
            else
            {
                try { ((dynamic)folder).DeleteTask(TaskName(expectedSid), 0); }
                catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { }
            }
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            if (mode == StartupMode.User) key.SetValue("LocalRemote", $"\"{path}\" --host --tray");
            else key.DeleteValue("LocalRemote", false);
            try { ((dynamic)folder).DeleteTask(LegacyTaskName(expectedSid), 0); }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { }
            key.DeleteValue("NewAnyDesk", false);
        }
        finally { Release(task); Release(folder); Release(service); }
    }
    public static async Task ConfigureAsync(StartupMode mode)
    {
        _ = ExecutablePath(); var before = ReadStatus();
        if (before.Error.Length > 0 && !before.HasTask && !before.Legacy) throw new IOException(before.Error);
        if (!IsElevated && (mode == StartupMode.Administrator || before.HasTask))
        {
            if (mode == StartupMode.Administrator && !CanElevate) throw new IOException("Для режима администратора войдите в Windows под учётной записью администратора.");
            var start = new ProcessStartInfo(ExecutablePath()) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--configure-startup"); start.ArgumentList.Add(mode.ToString()); start.ArgumentList.Add("--require-user"); start.ArgumentList.Add(UserSid);
            try
            {
                using var helper = await Task.Run(() => Process.Start(start) ?? throw new IOException("Не удалось открыть настройку автозапуска."));
                await helper.WaitForExitAsync();
                if (helper.ExitCode != 0) throw new IOException("Автозапуск не сохранён. Проверьте сообщение настройки Windows и повторите попытку.");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("Запрос прав администратора отменён.", ex); }
        }
        else Apply(mode, UserSid);
        var after = ReadStatus();
        if (after.Mode != mode || after.Error.Length > 0) throw new IOException("Windows не подтвердила сохранение автозапуска. " + after.Error);
    }
    public static void StartElevatedReplacement()
    {
        object? service = null, folder = null, task = null, running = null;
        try
        {
            var status = ReadStatus();
            if (status.Mode == StartupMode.Administrator && status.Error.Length == 0)
            {
                service = ConnectService(); folder = ((dynamic)service).GetFolder(@"\"); task = ((dynamic)folder).GetTask(TaskName(UserSid));
                running = ((dynamic)task).Run(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                return;
            }
            var start = new ProcessStartInfo(ExecutablePath()) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            foreach (string arg in new[] { "--host", "--require-user", UserSid, "--wait-for-parent", Environment.ProcessId.ToString(CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("Не удалось перезапустить хост.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("Запрос прав администратора отменён.", ex); }
        finally { Release(running); Release(task); Release(folder); Release(service); }
    }
    public static void ValidateTaskXml(string xml)
    {
        object? service = null, folder = null, validation = null;
        try { service = ConnectService(); folder = ((dynamic)service).GetFolder(@"\"); validation = ((dynamic)folder).RegisterTask("LocalRemote-Validation", xml, 1, UserSid, null, 3, null); }
        finally { Release(validation); Release(folder); Release(service); }
    }
}
