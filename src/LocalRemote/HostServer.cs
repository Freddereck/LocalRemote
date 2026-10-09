using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace LocalRemote;

public sealed class HostServer : IAsyncDisposable
{
    private readonly HostIdentity identity;
    private readonly MonitorInfo[] monitors;
    private readonly Func<int, FrameData> capture;
    private readonly Action<RemoteInput, int> applyInput;
    private readonly Action releaseInput;
    private readonly Action<VideoOptions>? configureCapture;
    private readonly Func<int, VideoOptions, CancellationToken, Task<H264Encoder>>? videoFactory;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<Task> sessions = [];
    private readonly object sessionsGate = new();
    private int occupied;
    private Task? acceptTask;
    private TcpClient? activeClient;
    public event Action<string>? StatusChanged;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public HostServer(HostIdentity identity, MonitorInfo[] monitors, Func<int, FrameData> capture, Action<RemoteInput, int> applyInput, Action releaseInput, int port = 48930, IPAddress? bind = null, Action<VideoOptions>? configureCapture = null, Func<int, VideoOptions, CancellationToken, Task<H264Encoder>>? videoFactory = null)
    {
        this.identity = identity; this.monitors = monitors; this.capture = capture; this.applyInput = applyInput; this.releaseInput = releaseInput;
        this.configureCapture = configureCapture; this.videoFactory = videoFactory;
        listener = new(bind ?? IPAddress.Any, port);
    }
    public void Start()
    {
        listener.Start(4);
        acceptTask = AcceptLoopAsync();
        StatusChanged?.Invoke("Доступ включён. Ожидаю подключение.");
    }
    public void DisconnectViewer() => activeClient?.Dispose();
    private async Task AcceptLoopAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(lifetime.Token); }
            catch (Exception) when (lifetime.IsCancellationRequested) { break; }
            if (client.Client.RemoteEndPoint is not IPEndPoint peer || !LanAddresses.IsPrivate(peer.Address) || Interlocked.CompareExchange(ref occupied, 1, 0) != 0)
            { client.Dispose(); continue; }
            activeClient = client;
            var task = Task.Run(() => ServeAsync(client), CancellationToken.None);
            lock (sessionsGate) sessions.Add(task);
            _ = task.ContinueWith(t => { lock (sessionsGate) sessions.Remove(t); }, TaskScheduler.Default);
        }
    }
    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var session = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        using (var writeGate = new SemaphoreSlim(1))
        using (var frameCredit = new SemaphoreSlim(1, 1))
        {
            bool authenticated = false;
            Task? frames = null;
            RemoteFileServer? files = null;
            var restartGate = new object();
            CancellationTokenSource? currentVideo = null;
            int revision = 0;
            void RestartVideo() { Interlocked.Increment(ref revision); lock (restartGate) currentVideo?.Cancel(); }
            try
            {
                using var stream = new SslStream(client.GetStream(), false);
                client.NoDelay = true;
                using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
                {
                    handshake.CancelAfter(TimeSpan.FromSeconds(8));
                    await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = identity.Certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, ClientCertificateRequired = false }, handshake.Token);
                    var auth = await Wire.ReadAsync(stream, handshake.Token);
                    if (auth.Type != PacketType.Authenticate || auth.Data.Length != 64 || !identity.CheckSecret(Encoding.ASCII.GetString(auth.Data)))
                    {
                        await Wire.WriteAsync(stream, writeGate, PacketType.Error, Encoding.UTF8.GetBytes("Неверный код доступа."), handshake.Token);
                        await Task.Delay(1000, session.Token);
                        return;
                    }
                }
                authenticated = true;
                int selected = Array.FindIndex(monitors, m => m.Primary);
                if (selected < 0) selected = 0;
                VideoOptions options = videoFactory != null ? new() : new(Mode: "jpeg", Fps: 30);
                await Wire.WriteAsync(stream, writeGate, PacketType.Welcome, Wire.Json(new WelcomeInfo(Environment.MachineName, monitors, selected, options.Fps, ProtocolVersion: 3, SupportsVideo: videoFactory != null)), session.Token);
                files = new RemoteFileServer(stream, writeGate, session.Token);
                StatusChanged?.Invoke("Подключён: " + ((IPEndPoint)client.Client.RemoteEndPoint!).Address);
                frames = Task.Run(async () =>
                {
                    try
                    {
                        while (!session.IsCancellationRequested)
                        {
                            var settings = Volatile.Read(ref options);
                            int currentRevision = Volatile.Read(ref revision);
                            if (settings.Mode == "h264" && videoFactory != null)
                            {
                                using var videoLifetime = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                                lock (restartGate) { currentVideo = videoLifetime; if (revision != currentRevision) videoLifetime.Cancel(); }
                                try
                                {
                                    if (!DesktopAccess.IsAvailable()) throw new DesktopUnavailableException();
                                    await using var encoder = await videoFactory(Volatile.Read(ref selected), settings, videoLifetime.Token);
                                    videoLifetime.Token.ThrowIfCancellationRequested();
                                    await Wire.WriteAsync(stream, writeGate, PacketType.VideoStart, Wire.Json(encoder.Info), session.Token);
                                    await Wire.WriteAsync(stream, writeGate, PacketType.VideoChunk, encoder.FirstChunk, session.Token);
                                    byte[] buffer = new byte[32 * 1024];
                                    while (!videoLifetime.IsCancellationRequested)
                                    {
                                        if (!DesktopAccess.IsAvailable()) throw new DesktopUnavailableException();
                                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(videoLifetime.Token);
                                        idle.CancelAfter(TimeSpan.FromSeconds(6));
                                        int count = await encoder.Output.ReadAsync(buffer, idle.Token);
                                        if (count == 0) throw new IOException("Видеокодировщик остановился.");
                                        await Wire.WriteAsync(stream, writeGate, PacketType.VideoChunk, buffer[..count], session.Token);
                                    }
                                }
                                catch (OperationCanceledException) when (videoLifetime.IsCancellationRequested) { }
                                catch (DesktopUnavailableException ex)
                                {
                                    releaseInput();
                                    await Wire.WriteAsync(stream, writeGate, PacketType.Status, Encoding.UTF8.GetBytes(ex.Message), session.Token);
                                    await Task.Delay(800, session.Token);
                                }
                                catch (Exception ex) when (!session.IsCancellationRequested)
                                {
                                    if (!DesktopAccess.IsAvailable())
                                    {
                                        releaseInput();
                                        await Wire.WriteAsync(stream, writeGate, PacketType.Status, Encoding.UTF8.GetBytes(new DesktopUnavailableException().Message), session.Token);
                                        await Task.Delay(800, session.Token);
                                    }
                                    else
                                    {
                                        await Wire.WriteAsync(stream, writeGate, PacketType.Status, Encoding.UTF8.GetBytes(("H.264 недоступен; включён совместимый режим. " + ex.Message).Truncate(1500)), session.Token);
                                        Volatile.Write(ref options, settings with { Mode = "jpeg", Fps = 30 });
                                    }
                                }
                                finally { lock (restartGate) currentVideo = null; }
                                continue;
                            }
                            await frameCredit.WaitAsync(session.Token);
                            configureCapture?.Invoke(settings);
                            long started = Environment.TickCount64;
                            try { await Wire.WriteAsync(stream, writeGate, PacketType.Frame, Wire.EncodeFrame(capture(Volatile.Read(ref selected))), session.Token); }
                            catch (DesktopUnavailableException ex)
                            {
                                releaseInput();
                                await Wire.WriteAsync(stream, writeGate, PacketType.Status, Encoding.UTF8.GetBytes(ex.Message), session.Token);
                                frameCredit.Release();
                                await Task.Delay(800, session.Token);
                            }
                            int delay = (int)Math.Max(0, 1000d / settings.Fps - (Environment.TickCount64 - started));
                            if (delay > 0) await Task.Delay(delay, session.Token);
                        }
                    }
                    finally { session.Cancel(); client.Dispose(); }
                }, session.Token);
                while (!session.IsCancellationRequested)
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                    var packet = await Wire.ReadAsync(stream, readTimeout.Token);
                    switch (packet.Type)
                    {
                        case PacketType.Ping:
                            await Wire.WriteAsync(stream, writeGate, PacketType.Pong, [], session.Token); break;
                        case PacketType.FrameAck:
                            if (frameCredit.CurrentCount == 0) frameCredit.Release(); break;
                        case PacketType.ReleaseInput: releaseInput(); break;
                        case PacketType.SelectMonitor:
                            int index = Wire.Parse<int>(packet.Data);
                            if (index < 0 || index >= monitors.Length) throw new InvalidDataException("Неверный монитор.");
                            releaseInput(); Volatile.Write(ref selected, index); RestartVideo(); break;
                        case PacketType.SetVideoOptions:
                            var videoOptions = Wire.Parse<VideoOptions>(packet.Data);
                            if (!videoOptions.IsValid()) throw new InvalidDataException("Неверные настройки видео.");
                            if (videoOptions.Mode == "h264" && videoFactory == null) videoOptions = videoOptions with { Mode = "jpeg", Fps = 30 };
                            releaseInput(); Volatile.Write(ref options, videoOptions); RestartVideo(); break;
                        case PacketType.FileRequest: await files.HandleAsync(Wire.Parse<FileRequest>(packet.Data)); break;
                        case PacketType.FileChunk: await files.ReceiveChunkAsync(packet.Data); break;
                        case PacketType.Input:
                            var input = Wire.Parse<RemoteInput>(packet.Data);
                            if (!InputValidation.IsValid(input)) throw new InvalidDataException("Неверное событие ввода.");
                            try { applyInput(input, Volatile.Read(ref selected)); }
                            catch (InvalidOperationException ex) { await Wire.WriteAsync(stream, writeGate, PacketType.Status, Encoding.UTF8.GetBytes(ex.Message), session.Token); }
                            break;
                        default: throw new InvalidDataException("Сообщение недопустимо в текущей сессии.");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException or ObjectDisposedException)
            { if (authenticated && !lifetime.IsCancellationRequested) StatusChanged?.Invoke("Соединение закрыто. Ожидаю подключение."); }
            catch (Exception ex) { StatusChanged?.Invoke("Ошибка сессии: " + ex.Message); }
            finally
            {
                session.Cancel(); client.Dispose();
                if (frames != null) { try { await frames; } catch { } }
                if (files != null) await files.DisposeAsync();
                if (authenticated) releaseInput();
                activeClient = null;
                Interlocked.Exchange(ref occupied, 0);
                if (!lifetime.IsCancellationRequested) StatusChanged?.Invoke("Доступ включён. Ожидаю подключение.");
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); listener.Stop(); activeClient?.Dispose();
        if (acceptTask != null) { try { await acceptTask; } catch { } }
        Task[] pending; lock (sessionsGate) pending = sessions.ToArray();
        await Task.WhenAll(pending);
        lifetime.Dispose();
    }
}
