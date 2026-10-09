using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using LocalRemote;

internal static class FileSpeedChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
    public static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var identity = HostIdentity.Create(); int inputs = 0;
        await using var host = new HostServer(identity, [new(0,"Speed test",1280,720,true)], _ => new(1280,720,[1,2,3]), (_,_) => Interlocked.Increment(ref inputs), () => {}, 0, IPAddress.Loopback);
        host.Start(); await using var viewer = new ViewerClient();
        await viewer.ConnectAsync(new("127.0.0.1",host.Port,identity.Fingerprint,identity.Secret),timeout.Token);
        viewer.FrameReceived += _ => Task.CompletedTask; viewer.StartReceiving();
        string root = Path.Combine(Path.GetTempPath(), "LocalRemote-speed-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string remote = Path.Combine(root,"remote"); Directory.CreateDirectory(remote);
            string source = Path.Combine(root,"large.bin");
            byte[] block = RandomNumberGenerator.GetBytes(1024 * 1024);
            await using (var file = File.Create(source)) for (int i = 0; i < 64; i++) await file.WriteAsync(block,timeout.Token);
            string originalHash = await HashAsync(source,timeout.Token);
            var watch = Stopwatch.StartNew();
            var upload = viewer.Files.UploadAsync(source,remote,false,null,timeout.Token,rateLimitMiB: 0);
            for (int i = 0; i < 20; i++) { viewer.SendInput(new("key",Value: 0x1e)); await Task.Delay(5,timeout.Token); }
            await upload; watch.Stop();
            double uploadRate = 64 / watch.Elapsed.TotalSeconds;
            Console.WriteLine($"FILE BENCHMARK upload: {uploadRate:0.0} MiB/s, 64 MiB, TLS loopback, no limit");
            string uploaded = Path.Combine(remote,"large.bin");
            Check(uploadRate > 8 && await HashAsync(uploaded,timeout.Token) == originalHash,"unlimited upload exceeds the old cap and preserves SHA-256");
            for (int i = 0; i < 50 && Volatile.Read(ref inputs) < 20; i++) await Task.Delay(10,timeout.Token);
            Check(viewer.IsConnected && Volatile.Read(ref inputs) == 20,"bulk file upload keeps the keyboard command queue usable");
            string downloaded = Path.Combine(root,"copy.bin"); watch.Restart();
            await viewer.Files.DownloadAsync(uploaded,downloaded,false,null,timeout.Token,rateLimitMiB: 0); watch.Stop();
            double downloadRate = 64 / watch.Elapsed.TotalSeconds;
            Console.WriteLine($"FILE BENCHMARK download: {downloadRate:0.0} MiB/s, 64 MiB, TLS loopback, no limit");
            Check(downloadRate > 8 && await HashAsync(downloaded,timeout.Token) == originalHash,"unlimited download exceeds the old cap and preserves SHA-256");
            string small = Path.Combine(root,"limited.bin");
            await using (var file = File.Create(small)) for (int i = 0; i < 3; i++) await file.WriteAsync(block,timeout.Token);
            watch.Restart();
            await viewer.Files.DownloadAsync(small,Path.Combine(root,"limited-copy.bin"),false,null,timeout.Token,rateLimitMiB: 1); watch.Stop();
            Check(watch.Elapsed.TotalSeconds >= 2.8,"host honors the requested download speed limit");
            using var cancelUpload = new CancellationTokenSource();
            string cancelSource = Path.Combine(root,"cancel-fast.bin"); File.Copy(source,cancelSource);
            try
            {
                await viewer.Files.UploadAsync(cancelSource,remote,false,new ImmediateProgress(_ => cancelUpload.Cancel()),cancelUpload.Token,rateLimitMiB: 0);
                throw new Exception("Fast upload ignored cancellation.");
            }
            catch (OperationCanceledException) { }
            await Task.Delay(200,timeout.Token);
            Check(!File.Exists(Path.Combine(remote,"cancel-fast.bin")) && !Directory.EnumerateFiles(remote,"*.partial").Any(),"unlimited upload cancellation removes the partial file");
            using var cancelDownload = new CancellationTokenSource();
            string cancelTarget = Path.Combine(root,"cancel-copy.bin");
            try
            {
                await viewer.Files.DownloadAsync(source,cancelTarget,false,new ImmediateProgress(_ => cancelDownload.Cancel()),cancelDownload.Token,rateLimitMiB: 0);
                throw new Exception("Fast download ignored cancellation.");
            }
            catch (OperationCanceledException) { }
            await Task.Delay(200,timeout.Token);
            Check(!File.Exists(cancelTarget) && !Directory.EnumerateFiles(root,"*.partial").Any(),"unlimited download cancellation removes the partial file");
            Check((await viewer.Files.ListAsync(remote,0,timeout.Token)).Entries?.Length == 1,"file connection survives unlimited transfer cancellation");
        }
        finally
        {
            string full = Path.GetFullPath(root), temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(temporary,StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("LocalRemote-speed-check-",StringComparison.Ordinal)) throw new IOException("Unexpected cleanup directory.");
            if (Directory.Exists(full)) Directory.Delete(full,true);
        }
    }
    private static async Task<string> HashAsync(string path, CancellationToken token) { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream,token)); }
    private sealed class ImmediateProgress(Action<TransferProgress> action) : IProgress<TransferProgress> { public void Report(TransferProgress progress) => action(progress); }
}
