using System.Net;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using LocalRemote;

internal static class StartupChecks
{
    private static void Check(bool value, string text) { if (!value) throw new Exception("FAIL: " + text); Console.WriteLine("PASS: " + text); }
    public static async Task RunAsync()
    {
        string path = @"C:\Program Files\LocalRemote & Stream\LocalRemote.exe";
        string sid = Startup.UserSid;
        string text = Startup.BuildTaskXml(path, sid);
        var xml = XDocument.Parse(text); XNamespace ns = xml.Root!.Name.Namespace;
        string Value(string name) => xml.Descendants(ns + name).Single().Value;
        Check(Value("Command") == path && Value("WorkingDirectory") == Path.GetDirectoryName(path), "scheduled startup escapes paths with spaces and ampersands");
        Check(Value("LogonType") == "InteractiveToken" && Value("RunLevel") == "HighestAvailable" && Value("Principal").Length > 0 && xml.Descendants(ns + "UserId").All(e => e.Value == sid), "elevated startup uses the same user's interactive desktop without stored credentials");
        Check(Value("Arguments") == Startup.TaskArguments && Value("ExecutionTimeLimit") == "PT0S" && Value("RunOnlyIfNetworkAvailable") == "false", "startup enables host in tray without time limit or dependence on Wi-Fi readiness");
        Check(Value("DisallowStartIfOnBatteries") == "false" && Value("StopIfGoingOnBatteries") == "false" && Value("MultipleInstancesPolicy") == "IgnoreNew", "startup remains active on battery and avoids duplicate task instances");
        Startup.ValidateTaskXml(text);
        Check(true, "Windows Task Scheduler validates production startup XML without installing startup");
        var status = Startup.ReadStatus(); if (status.Error.Length > 0) Console.WriteLine(status.Error); Check(status.Error.Length == 0, "actual startup state can be read from Windows");
        if (Startup.IsElevated) RoundTripTask(xml);
        await InputDeniedAsync();
    }
    private static void RoundTripTask(XDocument xml)
    {
        XNamespace ns = xml.Root!.Name.Namespace;
        foreach (var item in xml.Descendants(ns + "Enabled")) item.Value = "false";
        string name = "LocalRemote-Check-" + Guid.NewGuid().ToString("N");
        object? service = null, folder = null, task = null;
        bool created = false;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            ((dynamic)service).Connect(); folder = ((dynamic)service).GetFolder(@"\");
            task = ((dynamic)folder).RegisterTask(name, xml.ToString(), 2, Startup.UserSid, null, 3, null); created = true;
            var saved = XDocument.Parse((string)((dynamic)task).Xml);
            bool SameUser(string value) => value == Startup.UserSid || ((System.Security.Principal.SecurityIdentifier)new System.Security.Principal.NTAccount(value).Translate(typeof(System.Security.Principal.SecurityIdentifier))).Value == Startup.UserSid;
            Check(!(bool)((dynamic)task).Enabled && saved.Descendants(ns + "RunLevel").Single().Value == "HighestAvailable" && saved.Descendants(ns + "UserId").All(e => SameUser(e.Value)), "disabled temporary task registers and retains elevated interactive principal");
        }
        finally
        {
            if (created)
            {
                if (!name.StartsWith("LocalRemote-Check-", StringComparison.Ordinal) || name == Startup.TaskName(Startup.UserSid)) throw new Exception("Unexpected cleanup task.");
                ((dynamic)folder!).DeleteTask(name, 0);
            }
            foreach (var item in new[] { task, folder, service }) if (item != null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
        }
    }
    private static async Task InputDeniedAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var identity = HostIdentity.Create(); int releases = 0;
        await using var host = new HostServer(identity, [new(0,"Input test",1280,720,true)], _ => new(1280,720,[1,2,3]), (_,_) => throw new InvalidOperationException("Windows отклонила ввод. Перезапустите хост от администратора."), () => Interlocked.Increment(ref releases), 0, IPAddress.Loopback);
        host.Start(); await using var viewer = new ViewerClient();
        await viewer.ConnectAsync(new("127.0.0.1", host.Port, identity.Fingerprint, identity.Secret), timeout.Token);
        var warning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.StatusChanged += value => { if (value.StartsWith("Windows отклонила ввод.")) warning.TrySetResult(); };
        viewer.FrameReceived += _ => Task.CompletedTask; viewer.StartReceiving();
        viewer.SendInput(new("key", Value: 0x1e)); await warning.Task.WaitAsync(timeout.Token);
        Check(viewer.IsConnected && (await viewer.Files.ListAsync("", 0, timeout.Token)).Entries?.Length > 0, "Windows input refusal produces a warning while remote session and files remain usable");
    }
}
