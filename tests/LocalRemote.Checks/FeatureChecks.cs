using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using LocalRemote;

internal static class FeatureChecks
{
    private static void Check(bool value, string text) { if (!value) throw new Exception("FAIL: " + text); Console.WriteLine("PASS: " + text); }
    public static async Task RunAsync()
    {
        Check(new VideoOptions().Fit(new(1920,1080)) == new Size(1280,720), "720p profile preserves 16:9 dimensions");
        Check(!new VideoOptions(Fps: 1000).IsValid() && !new VideoOptions(Width: 8192).IsValid(), "unsafe video settings rejected");
        await FilesAsync();
        await FileSpeedChecks.RunAsync();
        if (FfmpegTools.FindExecutable() == null) throw new Exception("FFmpeg is missing from the feature verification environment.");
        await RunVideoChecksAsync();
    }
    public static async Task RunVideoChecksAsync() { await VideoAsync(false); if (DesktopAccess.IsAvailable()) await VideoAsync(true); }
    private static async Task FilesAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var identity = HostIdentity.Create();
        await using var host = new HostServer(identity, [new(0,"Files test",1280,720,true)], _ => new(1280,720,[1,2,3]), (_,_) => { }, () => { }, 0, IPAddress.Loopback);
        host.Start();
        await using var viewer = new ViewerClient(); await viewer.ConnectAsync(new("127.0.0.1",host.Port,identity.Fingerprint,identity.Secret), timeout.Token);
        viewer.FrameReceived += _ => Task.CompletedTask; viewer.StartReceiving();
        string root = Path.Combine(Path.GetTempPath(), "LocalRemote-file-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string remote = Path.Combine(root,"remote"); Directory.CreateDirectory(remote);
            byte[] content = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 73);
            string original = Path.Combine(root,"проверка-file.bin"); await File.WriteAllBytesAsync(original,content,timeout.Token);
            var roots = await viewer.Files.ListAsync("",0,timeout.Token); Check(roots.Entries?.Any(e => e.Directory) == true,"remote drive list over authenticated TLS");
            var listing = await viewer.Files.ListAsync(remote,0,timeout.Token); Check(listing.Entries?.Length == 0,"remote directory listing");
            await viewer.Files.UploadAsync(original,remote,false,null,timeout.Token);
            string uploaded = Path.Combine(remote,Path.GetFileName(original));
            Check((await File.ReadAllBytesAsync(uploaded,timeout.Token)).SequenceEqual(content),"upload preserves all bytes across chunk boundaries and Unicode names");
            string downloaded = Path.Combine(root,"download.bin");
            await viewer.Files.DownloadAsync(uploaded,downloaded,false,null,timeout.Token);
            Check((await File.ReadAllBytesAsync(downloaded,timeout.Token)).SequenceEqual(content),"download preserves all bytes and SHA256");
            try { await viewer.Files.UploadAsync(original,remote,false,null,timeout.Token); throw new Exception("Existing file was overwritten without permission."); }
            catch (IOException) { Console.WriteLine("PASS: remote overwrite requires explicit request"); }
            string empty = Path.Combine(root,"empty.txt"); await File.WriteAllBytesAsync(empty,[],timeout.Token);
            await viewer.Files.UploadAsync(empty,remote,false,null,timeout.Token);
            await viewer.Files.DownloadAsync(Path.Combine(remote,"empty.txt"),Path.Combine(root,"empty-copy.txt"),false,null,timeout.Token);
            Check(new FileInfo(Path.Combine(root,"empty-copy.txt")).Length == 0,"zero-byte file round trip");
            byte[] replacement = RandomNumberGenerator.GetBytes(18000); await File.WriteAllBytesAsync(original,replacement,timeout.Token);
            await viewer.Files.UploadAsync(original,remote,true,null,timeout.Token);
            Check((await File.ReadAllBytesAsync(uploaded,timeout.Token)).SequenceEqual(replacement),"explicit overwrite finishes atomically");
            string large = Path.Combine(root,"cancelled.bin");
            await using (var file = File.Create(large)) file.SetLength(32 * 1024 * 1024);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
            try { await viewer.Files.UploadAsync(large,remote,false,null,cancel.Token); throw new Exception("Upload cancellation did not trigger."); }
            catch (OperationCanceledException) { }
            await Task.Delay(250,timeout.Token);
            Check(!File.Exists(Path.Combine(remote,"cancelled.bin")) && !Directory.EnumerateFiles(remote,"*.partial").Any(),"cancelled upload leaves no destination or partial file");
            using var cancelDownload = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
            string cancelledTarget = Path.Combine(root,"cancel-download.bin");
            try { await viewer.Files.DownloadAsync(large,cancelledTarget,false,null,cancelDownload.Token); throw new Exception("Download cancellation did not trigger."); }
            catch (OperationCanceledException) { }
            Check(!File.Exists(cancelledTarget) && !Directory.EnumerateFiles(root,"*.partial").Any(),"cancelled download cleans local partial file");
            Check((await viewer.Files.ListAsync(remote,0,timeout.Token)).Entries?.Length == 2,"file session remains usable after cancellation");
        }
        finally
        {
            string verified = Path.GetFullPath(root);
            string intended = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!verified.StartsWith(intended,StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(verified).StartsWith("LocalRemote-file-check-",StringComparison.Ordinal)) throw new Exception("Unexpected cleanup path.");
            if (Directory.Exists(verified)) Directory.Delete(verified,true);
        }
    }
    private static async Task VideoAsync(bool desktop)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(22));
        using var identity = HostIdentity.Create();
        var monitors = desktop ? ScreenCapture.GetMonitors() : [new MonitorInfo(0,"Video test",1280,720,true)];
        await using var host = new HostServer(identity,monitors,_ => new(1280,720,[1,2,3]),(_,_)=>{},()=>{},0,IPAddress.Loopback,videoFactory: (index,settings,token) => desktop ? H264Encoder.StartDesktopAsync(index,settings,token) : H264Encoder.StartSyntheticAsync(settings,token));
        host.Start();
        await using var viewer = new ViewerClient(); await viewer.ConnectAsync(new("127.0.0.1",host.Port,identity.Fingerprint,identity.Secret),timeout.Token);
        int count = 0; var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var watch = new Stopwatch();
        VideoInfo? info = null;
        host.StatusChanged += value => Console.WriteLine("HOST STATUS: " + value);
        viewer.VideoStarted += value => { info = value; Console.WriteLine($"VIDEO STARTED: {value.Width}x{value.Height}, {value.Encoder}, {value.Capture}"); };
        viewer.StatusChanged += value => Console.WriteLine("VIDEO STATUS: " + value);
        viewer.FrameReceived += frame =>
        {
            CheckFrame(frame);
            if (Interlocked.Increment(ref count) == 121) watch.Start();
            if (count >= 361) { watch.Stop(); arrived.TrySetResult(); }
            return Task.CompletedTask;
        };
        viewer.StartReceiving();
        try { await arrived.Task.WaitAsync(timeout.Token); }
        catch { Console.WriteLine($"VIDEO TIMEOUT: frames={count}, bytes={viewer.VideoBytesReceived}, info={info}"); throw; }
        double fps = 240 / watch.Elapsed.TotalSeconds;
        Console.WriteLine($"VIDEO BENCHMARK ({(desktop ? "desktop" : "synthetic")}): {fps:0.0} FPS, {info?.Width}x{info?.Height}, {info?.Encoder}, {info?.Capture}");
        Check(info is { Width: 1280, Height: 720, Fps: 60 } && fps >= 50,"720p/60 pipeline measured with real H.264 encode, TLS and decode" + (desktop ? " on Windows desktop" : " on moving test pattern"));
        var drives = await viewer.Files.ListAsync("",0,timeout.Token);
        Check(drives.Entries?.Length > 0,"file manager responds while H.264 video is playing");
        if (!desktop)
        {
            string root = Path.Combine(Path.GetTempPath(), "LocalRemote-video-files-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string remote = Path.Combine(root, "remote"); Directory.CreateDirectory(remote);
                byte[] content = RandomNumberGenerator.GetBytes(1024 * 1024 + 91);
                string original = Path.Combine(root, "video-copy.bin");
                await File.WriteAllBytesAsync(original, content, timeout.Token);
                int before = Volatile.Read(ref count);
                await viewer.Files.UploadAsync(original, remote, false, null, timeout.Token);
                string downloaded = Path.Combine(root, "copy.bin");
                await viewer.Files.DownloadAsync(Path.Combine(remote, "video-copy.bin"), downloaded, false, null, timeout.Token);
                Check((await File.ReadAllBytesAsync(downloaded, timeout.Token)).SequenceEqual(content) && Volatile.Read(ref count) > before + 10,
                    "bidirectional file copy preserves bytes while video frames continue on the same TLS connection");
            }
            finally
            {
                string verified = Path.GetFullPath(root);
                string intended = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!verified.StartsWith(intended, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(verified).StartsWith("LocalRemote-video-files-check-", StringComparison.Ordinal)) throw new Exception("Unexpected cleanup path.");
                if (Directory.Exists(verified)) Directory.Delete(verified, true);
            }
        }
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewer.FrameReceived += frame => { if (frame.PixelWidth == 1920 && frame.PixelHeight == 1080) first.TrySetResult(); return Task.CompletedTask; };
        viewer.SetVideoOptions(new(1920,1080,30,16000000));
        await first.Task.WaitAsync(timeout.Token);
        Check(true,"stream profile switches to 1080p without reconnecting");
        static void CheckFrame(FrameData frame)
        {
            if (frame.Encoding != FrameEncoding.Bgr24 || frame.PixelWidth < 1 || frame.Jpeg.Length != frame.PixelWidth * frame.PixelHeight * 3) throw new Exception("Invalid decoded video frame.");
        }
    }
}
