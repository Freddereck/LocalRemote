using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace LocalRemote;

public static class FfmpegTools
{
    public static string? FindExecutable()
    {
        string adjacent = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(adjacent)) return adjacent;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; directory != null && i < 8; i++, directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "third_party", "ffmpeg", "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
    public static Process Start(IEnumerable<string> arguments, bool input)
    {
        var info = new ProcessStartInfo(FindExecutable() ?? throw new FileNotFoundException("Рядом с LocalRemote.exe отсутствует ffmpeg.exe. Скопируйте всю папку новой сборки."))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = input, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("Не удалось запустить видеокодек.");
    }
}

public sealed class H264Encoder : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task errors;
    private readonly StringBuilder diagnostic = new();
    public VideoInfo Info { get; }
    public byte[] FirstChunk { get; private set; } = [];
    public Stream Output => process.StandardOutput.BaseStream;
    private H264Encoder(Process process, VideoInfo info)
    {
        this.process = process; Info = info;
        errors = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            { lock (diagnostic) { diagnostic.AppendLine(line); if (diagnostic.Length > 4096) diagnostic.Remove(0, diagnostic.Length - 4096); } }
        });
    }
    public static Task<H264Encoder> StartDesktopAsync(int monitor, VideoOptions options, CancellationToken token)
    {
        var bounds = ScreenCapture.GetBounds(monitor);
        var size = options.Fit(bounds.Size);
        bool duplication = Screen.AllScreens.Length == 1;
        return StartAsync(bounds, size, monitor, options, duplication, false, token);
    }
    public static Task<H264Encoder> StartSyntheticAsync(VideoOptions options, CancellationToken token) => StartAsync(new(0, 0, options.Width, options.Height), new(options.Width, options.Height), 0, options, false, true, token);
    private static async Task<H264Encoder> StartAsync(Rectangle bounds, Size size, int monitor, VideoOptions options, bool duplication, bool synthetic, CancellationToken token)
    {
        if (!options.IsValid()) throw new InvalidDataException("Неверные настройки видео.");
        var failures = new List<string>();
        foreach (string codec in new[] { "h264_nvenc", "h264_amf", "libx264" })
        {
            foreach (bool useDuplication in duplication && !synthetic ? new[] { true, false } : new[] { false })
            {
                token.ThrowIfCancellationRequested();
                var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
                if (synthetic) args.AddRange(["-re", "-f", "lavfi", "-i", $"testsrc2=size={bounds.Width}x{bounds.Height}:rate={options.Fps}"]);
                else if (useDuplication) args.AddRange(["-f", "lavfi", "-i", $"ddagrab=output_idx=0:framerate={options.Fps}:draw_mouse=1"]);
                else args.AddRange(["-f", "gdigrab", "-framerate", options.Fps.ToString(CultureInfo.InvariantCulture), "-draw_mouse", "1", "-offset_x", bounds.X.ToString(CultureInfo.InvariantCulture), "-offset_y", bounds.Y.ToString(CultureInfo.InvariantCulture), "-video_size", $"{bounds.Width}x{bounds.Height}", "-i", "desktop"]);
                string filter = (useDuplication ? "hwdownload,format=bgra," : "") + $"scale={size.Width}:{size.Height}:flags=lanczos,format=yuv420p";
                args.AddRange(["-vf", filter, "-an", "-c:v", codec]);
                if (codec == "h264_nvenc") args.AddRange(["-preset", "p2", "-tune", "ull", "-rc", "cbr", "-zerolatency", "1", "-rc-lookahead", "0"]);
                else if (codec == "h264_amf") args.AddRange(["-usage", "ultralowlatency", "-quality", "speed", "-rc", "cbr"]);
                else args.AddRange(["-preset", "ultrafast", "-tune", "zerolatency", "-threads", "2"]);
                int bitrate = options.Bitrate;
                args.AddRange(["-b:v", bitrate.ToString(CultureInfo.InvariantCulture), "-maxrate", bitrate.ToString(CultureInfo.InvariantCulture), "-bufsize", (bitrate / 10).ToString(CultureInfo.InvariantCulture), "-g", Math.Max(15, options.Fps / 2).ToString(CultureInfo.InvariantCulture), "-bf", "0", "-flush_packets", "1", "-f", "h264", "pipe:1"]);
                var info = new VideoInfo(size.Width, size.Height, bounds.Width, bounds.Height, monitor, options.Fps, bitrate, codec == "h264_nvenc" ? "NVENC" : codec == "h264_amf" ? "AMD AMF" : "CPU H.264", synthetic ? "Тест" : useDuplication ? "Desktop Duplication" : "GDI");
                var encoder = new H264Encoder(FfmpegTools.Start(args, false), info);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    byte[] first = new byte[32 * 1024];
                    int count = await encoder.Output.ReadAsync(first, timeout.Token);
                    if (count == 0) throw new IOException("Видеокодировщик завершился до первого кадра.");
                    encoder.FirstChunk = first[..count]; return encoder;
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    await encoder.DisposeAsync();
                    lock (encoder.diagnostic) failures.Add(codec + ": " + encoder.diagnostic.ToString().Trim());
                }
                catch { await encoder.DisposeAsync(); throw; }
            }
        }
        throw new IOException("Не удалось включить H.264. " + string.Join("; ", failures).Truncate(800));
    }
    public async ValueTask DisposeAsync()
    {
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        try { await process.WaitForExitAsync(); await errors; } catch { }
        process.Dispose();
    }
}

public sealed class H264Decoder : IAsyncDisposable
{
    private readonly Process process;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task reading;
    private readonly Task errors;
    public H264Decoder(VideoInfo info, Func<FrameData, Task> frame, Action<string> failed)
    {
        if (info.Width is < 2 or > 1920 || info.Height is < 2 or > 1080 || info.OriginalWidth is < 1 or > 32768 || info.OriginalHeight is < 1 or > 32768 || info.MonitorIndex is < 0 or > 63 || info.Fps is < 1 or > 60)
            throw new InvalidDataException("Неверные параметры видеопотока.");
        process = FfmpegTools.Start(["-hide_banner", "-loglevel", "error", "-nostdin", "-flags", "low_delay", "-probesize", "32768", "-analyzeduration", "1", "-threads", "2", "-f", "h264", "-framerate", info.Fps.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0", "-an", "-pix_fmt", "bgr24", "-fps_mode", "passthrough", "-flush_packets", "1", "-f", "rawvideo", "pipe:1"], true);
        errors = Task.Run(async () => { while (await process.StandardError.ReadLineAsync() is { } line) { if (line.Length > 0 && !lifetime.IsCancellationRequested) failed("Видеокодек: " + line.Truncate(250)); } });
        reading = Task.Run(async () =>
        {
            try
            {
                int length = checked(info.Width * info.Height * 3);
                while (!lifetime.IsCancellationRequested)
                {
                    byte[] pixels = new byte[length];
                    await process.StandardOutput.BaseStream.ReadExactlyAsync(pixels, lifetime.Token);
                    await frame(new(info.OriginalWidth, info.OriginalHeight, pixels, info.MonitorIndex, FrameEncoding.Bgr24, info.Width, info.Height)).WaitAsync(lifetime.Token);
                }
            }
            catch (Exception ex) { if (!lifetime.IsCancellationRequested) failed("Декодирование видео остановлено: " + ex.Message); }
        });
    }
    public async Task PushAsync(byte[] chunk, CancellationToken token) => await process.StandardInput.BaseStream.WriteAsync(chunk, token);
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        try { await reading; await process.WaitForExitAsync(); await errors; } catch { }
        process.Dispose(); lifetime.Dispose();
    }
}

internal static class TextLimits
{
    public static string Truncate(this string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
