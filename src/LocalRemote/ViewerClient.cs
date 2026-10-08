using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace LocalRemote;

public sealed class ViewerClient : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly TcpClient client = new() { NoDelay = true };
    private readonly SemaphoreSlim writeGate = new(1);
    private readonly Channel<Packet> outgoing = Channel.CreateBounded<Packet>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private SslStream? stream;
    private Task? sending;
    private Task? heartbeat;
    private Task? receiving;
    private int disposing;
    private H264Decoder? decoder;
    public long VideoBytesReceived { get; private set; }
    public FileTransferClient Files { get; }
    public ViewerClient() { Files = new FileTransferClient(QueueAsync, lifetime.Token); }
    public event Func<FrameData, Task>? FrameReceived;
    public event Action<VideoInfo>? VideoStarted;
    public event Action<string>? StatusChanged;
    public event Action? Disconnected;
    public bool IsConnected => stream != null && !lifetime.IsCancellationRequested;
    public async Task<WelcomeInfo> ConnectAsync(Invitation invitation, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await client.ConnectAsync(invitation.Host, invitation.Port, timeout.Token);
        stream = new(client.GetStream(), false, (_, certificate, _, _) => certificate != null &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), Convert.FromHexString(invitation.Fingerprint)));
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "LocalRemote", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, timeout.Token);
        await Wire.WriteAsync(stream, writeGate, PacketType.Authenticate, Encoding.ASCII.GetBytes(invitation.Secret), timeout.Token);
        var reply = await Wire.ReadAsync(stream, timeout.Token);
        if (reply.Type == PacketType.Error) throw new AuthenticationException(Encoding.UTF8.GetString(reply.Data));
        if (reply.Type != PacketType.Welcome) throw new InvalidDataException("Хост не подтвердил подключение.");
        var welcome = Wire.Parse<WelcomeInfo>(reply.Data);
        if (welcome.ProtocolVersion != 2) throw new InvalidDataException("Обновите LocalRemote на обоих компьютерах до одной версии.");
        if (welcome.Monitors.Length is < 1 or > 64 || welcome.Monitors.Any(m => m.Width is < 1 or > 32768 || m.Height is < 1 or > 32768)) throw new InvalidDataException("Неверная информация об экранах.");
        return welcome;
    }
    public void StartReceiving()
    {
        if (stream == null) throw new InvalidOperationException("Сначала подключитесь.");
        sending = Task.Run(async () =>
        {
            try { await foreach (var packet in outgoing.Reader.ReadAllAsync(lifetime.Token)) await Wire.WriteAsync(stream, writeGate, packet.Type, packet.Data, lifetime.Token); }
            catch (Exception) { Stop(); }
        });
        heartbeat = Task.Run(async () =>
        {
            try { while (!lifetime.IsCancellationRequested) { await Task.Delay(2000, lifetime.Token); Queue(PacketType.Ping, []); } }
            catch (OperationCanceledException) { }
        });
        receiving = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(8));
                    var packet = await Wire.ReadAsync(stream, timeout.Token);
                    switch (packet.Type)
                    {
                        case PacketType.Frame:
                            if (decoder != null) { await decoder.DisposeAsync(); decoder = null; }
                            if (FrameReceived != null) await FrameReceived.Invoke(Wire.DecodeFrame(packet.Data));
                            Queue(PacketType.FrameAck, []); break;
                        case PacketType.VideoStart:
                            if (decoder != null) await decoder.DisposeAsync();
                            var videoInfo = Wire.Parse<VideoInfo>(packet.Data);
                            decoder = new H264Decoder(videoInfo, frame => FrameReceived?.Invoke(frame) ?? Task.CompletedTask, text => { StatusChanged?.Invoke(text); });
                            VideoStarted?.Invoke(videoInfo); break;
                        case PacketType.VideoChunk:
                            VideoBytesReceived += packet.Data.Length;
                            if (decoder == null) throw new InvalidDataException("Видеопоток не инициализирован.");
                            await decoder.PushAsync(packet.Data, lifetime.Token); break;
                        case PacketType.FileReply: Files.HandleReply(Wire.Parse<FileReply>(packet.Data)); break;
                        case PacketType.FileChunk: await Files.ReceiveChunkAsync(packet.Data); break;
                        case PacketType.Status: StatusChanged?.Invoke(Encoding.UTF8.GetString(packet.Data)); break;
                        case PacketType.Pong: break;
                        default: throw new InvalidDataException("Неожиданное сообщение хоста.");
                    }
                }
            }
            catch (Exception ex) { if (!lifetime.IsCancellationRequested) StatusChanged?.Invoke("Соединение прервано: " + ex.Message); }
            finally { Stop(); Disconnected?.Invoke(); }
        });
    }
    public void SendInput(RemoteInput input) => Queue(PacketType.Input, Wire.Json(input));
    public void SelectMonitor(int index) => Queue(PacketType.SelectMonitor, Wire.Json(index));
    public void SetVideoOptions(VideoOptions options) => Queue(PacketType.SetVideoOptions, Wire.Json(options));
    public void ReleaseInput() => Queue(PacketType.ReleaseInput, []);
    private void Queue(PacketType type, byte[] data)
    {
        if (!lifetime.IsCancellationRequested && !outgoing.Writer.TryWrite(new(type, data)))
        { StatusChanged?.Invoke("Ввод остановлен: сеть не успевает передавать команды. Переподключитесь."); Stop(); }
    }
    private async Task QueueAsync(PacketType type, byte[] data, CancellationToken token) => await outgoing.Writer.WriteAsync(new(type, data), token);
    private void Stop() { lifetime.Cancel(); outgoing.Writer.TryComplete(); client.Dispose(); }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposing, 1) != 0) return;
        Stop();
        foreach (var task in new[] { sending, heartbeat, receiving }) if (task != null) { try { await task; } catch { } }
        if (decoder != null) await decoder.DisposeAsync();
        await Files.DisposeAsync();
        stream?.Dispose(); writeGate.Dispose(); lifetime.Dispose();
    }
}
