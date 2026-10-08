using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using LocalRemote;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--write-icon") { ApplicationConfiguration.Initialize(); using var iconForm = new MainForm(false, false); using var output = File.Create(args[1]); (iconForm.Icon ?? throw new IOException("Значок не создан.")).Save(output); return 0; }
            if (args.Length == 2 && args[0] == "--render-ui") { RenderUi(args[1]); return 0; }
            if (args.Length == 1 && args[0] == "--video-checks") { FeatureChecks.RunVideoChecksAsync().GetAwaiter().GetResult(); return 0; }
            if (args.Length == 1 && args[0] == "--startup-checks") { StartupChecks.RunAsync().GetAwaiter().GetResult(); return 0; }
            StartupChecks.RunAsync().GetAwaiter().GetResult(); RunAsync().GetAwaiter().GetResult(); FeatureChecks.RunAsync().GetAwaiter().GetResult(); RunUiReconnectCheck(); Console.WriteLine("All checks passed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Assert(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    private static async Task RejectAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); } catch (T) { Console.WriteLine("PASS: " + name); return; }
        throw new Exception("FAIL: " + name);
    }
    private static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        using var gate = new SemaphoreSlim(1);
        using var memory = new MemoryStream();
        var input = new RemoteInput("key", Value: 0x1d, Extended: true);
        await Wire.WriteAsync(memory, gate, PacketType.Input, Wire.Json(input), token);
        memory.Position = 0;
        using var fragmented = new FragmentedStream(memory.ToArray());
        var read = await Wire.ReadAsync(fragmented, token);
        Assert(read.Type == PacketType.Input && Wire.Parse<RemoteInput>(read.Data) == input, "partial TCP reads preserve commands");
        byte[] hostile = new byte[5]; hostile[0] = (byte)PacketType.Input; BinaryPrimitives.WriteInt32BigEndian(hostile.AsSpan(1), int.MaxValue);
        await RejectAsync<InvalidDataException>(async () => { await Wire.ReadAsync(new MemoryStream(hostile), token); }, "oversize rejected before allocation");
        hostile[0] = 255;
        await RejectAsync<InvalidDataException>(async () => { await Wire.ReadAsync(new MemoryStream(hostile), token); }, "unknown packet rejected");
        Assert(!InputValidation.IsValid(new("key", Value: 256)) && !InputValidation.IsValid(new("move", X: -1)) && !InputValidation.IsValid(new("shell")), "untrusted input ranges and kinds rejected");
        var encodedFrame = Wire.EncodeFrame(new(1920, 1080, [1, 2, 3]));
        var decodedFrame = Wire.DecodeFrame(encodedFrame);
        Assert(decodedFrame.OriginalWidth == 1920 && decodedFrame.OriginalHeight == 1080 && decodedFrame.Jpeg.SequenceEqual(new byte[] { 1, 2, 3 }), "frame metadata round trip");
        Assert(LanAddresses.IsPrivate(IPAddress.Parse("192.168.1.4")) && !LanAddresses.IsPrivate(IPAddress.Parse("8.8.8.8")), "public network addresses excluded");
        using var identity = HostIdentity.Create();
        Assert(identity.Certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx).Length > 0, "host identity can be saved after certificate import");
        Assert(identity.CheckSecret(identity.Secret) && !identity.CheckSecret(new string('0', 64)), "host secret authentication");
        if (DesktopAccess.IsAvailable())
        {
            var capture = new ScreenCapture();
            var actualMonitors = ScreenCapture.GetMonitors();
            int primary = Array.FindIndex(actualMonitors, m => m.Primary);
            var screen = capture.Capture(Math.Max(0, primary));
            using var bytes = new MemoryStream(screen.Jpeg); using var image = Image.FromStream(bytes);
            Assert(image.Width <= 1280 && image.Height <= 900 && screen.OriginalWidth == actualMonitors[Math.Max(0, primary)].Width, "real Windows capture produces bounded JPEG frame");
        }
        else Console.WriteLine("SKIP: real capture (interactive desktop unavailable)");
        string identityPath = Path.Combine(Path.GetTempPath(), "LocalRemote-identity-check-" + Guid.NewGuid().ToString("N") + ".bin");
        HostIdentity persisted;
        string savedSecret, savedFingerprint;
        try
        {
            using (var saved = HostIdentity.LoadOrCreate(identityPath))
            {
                savedSecret = saved.Secret; savedFingerprint = saved.Fingerprint;
                Assert(File.Exists(identityPath), "first host startup saves encrypted identity");
            }
            persisted = HostIdentity.LoadOrCreate(identityPath);
            Assert(persisted.Secret == savedSecret && persisted.Fingerprint == savedFingerprint, "host restart preserves invitation and private key");
        }
        finally { if (File.Exists(identityPath)) File.Delete(identityPath); }
        using var persistedIdentity = persisted;
        int captures = 0, inputs = 0, releases = 0;
        await using var host = new HostServer(persistedIdentity, [new(0, "Test", 1920, 1080, true)], _ => { Interlocked.Increment(ref captures); return new(1920, 1080, [1, 2, 3]); }, (_, _) => Interlocked.Increment(ref inputs), () => Interlocked.Increment(ref releases), 0, IPAddress.Loopback);
        host.Start();
        var invitation = new Invitation("127.0.0.1", host.Port, persistedIdentity.Fingerprint, persistedIdentity.Secret);
        Assert(Invitation.Parse(invitation.ToString()) == invitation, "invitation round trip including certificate pin");
        Assert(Invitation.Parse(invitation.ToString().Replace("localremote://", "newanydesk://")) == invitation, "legacy invitation remains compatible after rename");
        CheckLegacyMigration();
        await RejectAsync<FormatException>(() => { Invitation.Parse("localremote://8.8.8.8:48930/" + identity.Fingerprint + "#" + identity.Secret); return Task.CompletedTask; }, "public invitation rejected");
        await using (var wrongPin = new ViewerClient())
            await RejectAsync<AuthenticationException>(async () => { await wrongPin.ConnectAsync(invitation with { Fingerprint = new string('0', 64) }, token); }, "TLS rejects wrong host fingerprint");
        await Task.Delay(150, token);
        await using (var wrongSecret = new ViewerClient())
            await RejectAsync<AuthenticationException>(async () => { await wrongSecret.ConnectAsync(invitation with { Secret = new string('0', 64) }, token); }, "wrong client secret rejected");
        await Task.Delay(1100, token);
        using (var intruder = new TcpClient())
        {
            await intruder.ConnectAsync(IPAddress.Loopback, host.Port, token);
            using var ssl = new SslStream(intruder.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "LocalRemote" }, token);
            await Wire.WriteAsync(ssl, gate, PacketType.Input, Wire.Json(input), token);
            Assert((await Wire.ReadAsync(ssl, token)).Type == PacketType.Error, "commands before authentication refused");
        }
        await Task.Delay(1100, token);
        Assert(captures == 0 && inputs == 0 && releases == 0, "no screen capture or input before authentication");
        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, host.Port, token);
            using var ssl = new SslStream(raw.GetStream(), false, (_, certificate, _, _) => certificate != null && Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())) == persistedIdentity.Fingerprint);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "LocalRemote" }, token);
            await Wire.WriteAsync(ssl, gate, PacketType.Authenticate, Encoding.ASCII.GetBytes(persistedIdentity.Secret), token);
            Assert((await Wire.ReadAsync(ssl, token)).Type == PacketType.Welcome, "TLS host welcomes authenticated peer");
            Assert((await Wire.ReadAsync(ssl, token)).Type == PacketType.Frame, "authenticated peer receives screen frame");
            int beforeWait = captures; await Task.Delay(350, token);
            Assert(captures == beforeWait, "screen capture waits for viewer acknowledgment");
            await Wire.WriteAsync(ssl, gate, PacketType.Input, Wire.Json(input), token);
            await Task.Delay(100, token); Assert(inputs == 1, "authenticated keyboard event delivered");
            await Wire.WriteAsync(ssl, gate, PacketType.ReleaseInput, [], token);
            await Task.Delay(100, token); Assert(releases == 1, "focus release clears held keys");
            await Wire.WriteAsync(ssl, gate, PacketType.FrameAck, [], token);
            Assert((await Wire.ReadAsync(ssl, token)).Type == PacketType.Frame, "next screen frame after acknowledgment");
            await Wire.WriteAsync(ssl, gate, PacketType.Input, Wire.Json(new RemoteInput("key", Value: 999)), token);
            try { await Wire.ReadAsync(ssl, token); throw new Exception("Invalid input connection stayed open."); } catch (EndOfStreamException) { }
            Assert(inputs == 1, "invalid remote input never delivered");
        }
        await Task.Delay(150, token); Assert(releases >= 2, "disconnect releases held input");
        await using (var valid = new ViewerClient())
        {
            var welcome = await valid.ConnectAsync(invitation, token);
            var gotFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            valid.FrameReceived += _ => { gotFrame.TrySetResult(); return Task.CompletedTask; };
            valid.StartReceiving(); await gotFrame.Task.WaitAsync(token);
            Assert(welcome.Computer == Environment.MachineName, "real viewer client reconnects to TLS host");
            await valid.DisposeAsync(); await valid.DisposeAsync();
            Assert(true, "viewer dispose is idempotent");
        }
    }
    private static void CheckLegacyMigration()
    {
        string root = Path.Combine(Path.GetTempPath(), "LocalRemote-migration-" + Guid.NewGuid().ToString("N"));
        string legacy = Path.Combine(root, "NewAnyDesk"), current = Path.Combine(root, "LocalRemote");
        Directory.CreateDirectory(legacy);
        try
        {
            string fingerprint, secret;
            using (var identity = HostIdentity.LoadOrCreate(Path.Combine(legacy, "host.identity"))) { fingerprint = identity.Fingerprint; secret = identity.Secret; }
            File.WriteAllText(Path.Combine(legacy, "viewer.connection"), "opaque encrypted data");
            Settings.MigrateLegacySettings(current, legacy);
            using (var imported = HostIdentity.LoadOrCreate(Path.Combine(current, "host.identity")))
                Assert(imported.Fingerprint == fingerprint && imported.Secret == secret, "rename imports DPAPI identity without changing access keys");
            Assert(File.ReadAllBytes(Path.Combine(legacy, "viewer.connection")).SequenceEqual(File.ReadAllBytes(Path.Combine(current, "viewer.connection"))), "rename copies saved viewer settings without rewriting encrypted bytes");
            File.Delete(Path.Combine(current, "viewer.connection")); Settings.MigrateLegacySettings(current, legacy);
            Assert(!File.Exists(Path.Combine(current, "viewer.connection")), "forgotten legacy connection is not imported again");
            string existing = Path.Combine(root, "existing"); Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "host.identity"), "existing key"); Settings.MigrateLegacySettings(existing, legacy);
            Assert(File.ReadAllText(Path.Combine(existing, "host.identity")) == "existing key", "migration preserves existing LocalRemote settings");
        }
        finally
        {
            string checkedRoot = Path.GetFullPath(root), temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!checkedRoot.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(checkedRoot).StartsWith("LocalRemote-migration-", StringComparison.Ordinal)) throw new IOException("Unexpected migration test directory.");
            Directory.Delete(checkedRoot, true);
        }
    }
    private static void RenderUi(string directory)
    {
        Directory.CreateDirectory(directory);
        ApplicationConfiguration.Initialize();
        using var form = new MainForm(false, false);
        Control[] Walk(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Walk(c))).ToArray();
        foreach (var box in Walk(form).OfType<TextBox>().Where(b => b.UseSystemPasswordChar)) box.Clear();
        form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents(); form.PerformLayout();
        var tabs = form.Controls.OfType<TabControl>().Single();
        for (int i = 0; i < 2; i++)
        {
            tabs.SelectedIndex = i; Application.DoEvents(); form.PerformLayout();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(directory, i == 0 ? "viewer.png" : "host.png"));
            var originalSize = form.Size; form.Size = form.MinimumSize; Application.DoEvents(); form.PerformLayout();
            using (var compact = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(compact, new Rectangle(Point.Empty, form.Size)); compact.Save(Path.Combine(directory, i == 0 ? "viewer-compact.png" : "host-compact.png")); }
            form.Size = originalSize; Application.DoEvents();
        }
        Console.WriteLine("UI previews rendered to " + directory);
        RenderSessionUi(directory);
    }
    private static void RenderSessionUi(string directory)
    {
        using var identity = HostIdentity.Create();
        var host = new HostServer(identity,[new(0,"Preview",1920,1080,true)],_ => new(1920,1080,[1,2,3]),(_,_)=>{},()=>{},0,IPAddress.Loopback,videoFactory: (_,options,token) => H264Encoder.StartSyntheticAsync(options,token));
        host.Start();
        var invitation = new Invitation("127.0.0.1",host.Port,identity.Fingerprint,identity.Secret);
        var client = new ViewerClient(); var welcome = client.ConnectAsync(invitation).GetAwaiter().GetResult();
        using var viewer = new ViewerForm(client,welcome,invitation) { ShowInTaskbar = false, Opacity = 0 };
        viewer.Show();
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(3)) { Application.DoEvents(); Thread.Sleep(10); }
        using (var bitmap = new Bitmap(viewer.Width,viewer.Height)) { viewer.DrawToBitmap(bitmap,new(Point.Empty,viewer.Size)); bitmap.Save(Path.Combine(directory,"session.png")); }
        using var files = new FileManagerForm(() => client.Files) { ShowInTaskbar = false, Opacity = 0 };
        Control[] Walk(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Walk(c))).ToArray();
        foreach (var box in Walk(files).OfType<TextBox>())
            if (box.AccessibleName == "Папка на этом ПК" || box.AccessibleName == "Папка на втором ПК") box.Text = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","..",".."));
        files.Show(viewer); wait.Restart();
        while (wait.Elapsed < TimeSpan.FromSeconds(1)) { Application.DoEvents(); Thread.Sleep(10); }
        using (var bitmap = new Bitmap(files.Width,files.Height)) { files.DrawToBitmap(bitmap,new(Point.Empty,files.Size)); bitmap.Save(Path.Combine(directory,"files.png")); }
        files.Size = files.MinimumSize; Application.DoEvents();
        using (var bitmap = new Bitmap(files.Width,files.Height)) { files.DrawToBitmap(bitmap,new(Point.Empty,files.Size)); bitmap.Save(Path.Combine(directory,"files-compact.png")); }
        files.Close(); viewer.Close(); Application.DoEvents(); host.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    private static void RunUiReconnectCheck()
    {
        using var identity = HostIdentity.Create();
        Assert(identity.Certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx).Length > 0, "host identity can be saved after certificate import");
        using var canvas = new Bitmap(640, 360);
        using (var graphics = Graphics.FromImage(canvas)) { graphics.Clear(Color.SteelBlue); graphics.DrawString("Test desktop", SystemFonts.DefaultFont, Brushes.White, 40, 40); }
        using var encoded = new MemoryStream(); canvas.Save(encoded, System.Drawing.Imaging.ImageFormat.Jpeg);
        byte[] jpeg = encoded.ToArray();
        int connections = 0, inputCount = 0;
        var host = new HostServer(identity, [new(0, "UI Test", 640, 360, true)], _ => new(640, 360, jpeg), (_, _) => Interlocked.Increment(ref inputCount), () => { }, 0, IPAddress.Loopback);
        host.StatusChanged += text => { if (text.StartsWith("Подключён:")) Interlocked.Increment(ref connections); };
        host.Start();
        var invitation = new Invitation("127.0.0.1", host.Port, identity.Fingerprint, identity.Secret);
        var client = new ViewerClient();
        var welcome = client.ConnectAsync(invitation).GetAwaiter().GetResult();
        using var form = new ViewerForm(client, welcome, invitation) { ShowInTaskbar = false, Opacity = 0 };
        Exception? failure = null;
        form.Shown += async (_, _) =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try
            {
                await Task.Delay(150, timeout.Token);
                host.DisconnectViewer();
                while (Volatile.Read(ref connections) < 2) await Task.Delay(50, timeout.Token);
                await Task.Delay(150, timeout.Token);
                var taskManager = form.Controls.OfType<FlowLayoutPanel>().SelectMany(p => p.Controls.OfType<Button>()).Single(b => b.Text == "Диспетчер задач");
                taskManager.PerformClick();
                while (Volatile.Read(ref inputCount) < 6) await Task.Delay(50, timeout.Token);
                Assert(inputCount == 6, "viewer UI restores keyboard actions after network reconnect");
            }
            catch (Exception ex) { failure = ex; }
            finally { form.Close(); }
        };
        Application.Run(form);
        host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (failure != null) throw failure;
    }
    private sealed class FragmentedStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => base.ReadAsync(buffer[..Math.Min(2, buffer.Length)], token);
    }
}
