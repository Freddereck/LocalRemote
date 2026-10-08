using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalRemote;

public enum PacketType : byte { Authenticate = 1, Welcome, Input, Frame, Status, Ping, Pong, SelectMonitor, ReleaseInput, Error, FrameAck, SetVideoOptions, VideoStart, VideoChunk, FileRequest, FileReply, FileChunk }
public sealed record Packet(PacketType Type, byte[] Data);
public sealed record MonitorInfo(int Index, string Name, int Width, int Height, bool Primary);
public sealed record WelcomeInfo(string Computer, MonitorInfo[] Monitors, int SelectedMonitor, int Fps, int ProtocolVersion = 1, bool SupportsVideo = false);
public sealed record RemoteInput(string Kind, int X = 0, int Y = 0, int Value = 0, bool Up = false, bool Extended = false);
public enum FrameEncoding { Jpeg, Bgr24 }
public sealed record FrameData(int OriginalWidth, int OriginalHeight, byte[] Jpeg, int MonitorIndex = 0, FrameEncoding Encoding = FrameEncoding.Jpeg, int PixelWidth = 0, int PixelHeight = 0);
public sealed record VideoOptions(int Width = 1280, int Height = 720, int Fps = 60, int Bitrate = 12000000, string Mode = "h264", int JpegQuality = 92)
{
    public bool IsValid() => Width is >= 320 and <= 1920 && Height is >= 240 and <= 1080 && Fps is 15 or 30 or 60 && Bitrate is >= 2000000 and <= 30000000 && Mode is "h264" or "jpeg" && JpegQuality is >= 70 and <= 98;
    public Size Fit(Size source)
    {
        double scale = Math.Min(1, Math.Min(Width / (double)source.Width, Height / (double)source.Height));
        return new(Math.Max(2, (int)(source.Width * scale) / 2 * 2), Math.Max(2, (int)(source.Height * scale) / 2 * 2));
    }
}
public sealed record VideoInfo(int Width, int Height, int OriginalWidth, int OriginalHeight, int MonitorIndex, int Fps, int Bitrate, string Encoder, string Capture);

public static class Wire
{
    public const int MaxPayload = 8 * 1024 * 1024;
    public const int MaxControl = 8192;
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
    public static T Parse<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new InvalidDataException("Пустое сообщение.");

    public static async Task<Packet> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[5];
        await stream.ReadExactlyAsync(header, token);
        if (!Enum.IsDefined(typeof(PacketType), header[0])) throw new InvalidDataException("Неизвестный тип сообщения.");
        var type = (PacketType)header[0];
        int size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        int max = PayloadLimit(type);
        if (size < 0 || size > max) throw new InvalidDataException("Неверная длина сообщения.");
        byte[] data = new byte[size];
        await stream.ReadExactlyAsync(data, token);
        return new(type, data);
    }

    public static async Task WriteAsync(Stream stream, SemaphoreSlim gate, PacketType type, byte[] data, CancellationToken token)
    {
        int max = PayloadLimit(type);
        if (data.Length > max) throw new InvalidDataException("Слишком большое сообщение.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await gate.WaitAsync(timeout.Token);
        try
        {
            byte[] header = new byte[5];
            header[0] = (byte)type;
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), data.Length);
            await stream.WriteAsync(header, timeout.Token);
            await stream.WriteAsync(data, timeout.Token);
        }
        finally { gate.Release(); }
    }

    private static int PayloadLimit(PacketType type) => type switch { PacketType.Frame => MaxPayload, PacketType.VideoChunk or PacketType.FileChunk or PacketType.FileReply => 128 * 1024, _ => MaxControl };

    public static byte[] EncodeFrame(FrameData frame)
    {
        byte[] bytes = new byte[12 + frame.Jpeg.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes, frame.OriginalWidth);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), frame.OriginalHeight);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), frame.MonitorIndex);
        frame.Jpeg.CopyTo(bytes, 12);
        return bytes;
    }

    public static FrameData DecodeFrame(byte[] bytes)
    {
        if (bytes.Length < 13) throw new InvalidDataException("Повреждённый кадр.");
        int width = BinaryPrimitives.ReadInt32BigEndian(bytes);
        int height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4));
        int monitor = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8));
        if (width < 1 || height < 1 || width > 32768 || height > 32768 || monitor is < 0 or > 63) throw new InvalidDataException("Неверный размер экрана или монитор.");
        return new(width, height, bytes[12..], monitor);
    }
}

public static class InputValidation
{
    public static bool IsValid(RemoteInput input) => input.Kind switch
    {
        "move" => input.X is >= 0 and <= 65535 && input.Y is >= 0 and <= 65535,
        "button" => input.Value is >= 0 and <= 2 && input.X is >= 0 and <= 65535 && input.Y is >= 0 and <= 65535,
        "wheel" => input.Value is >= -1200 and <= 1200 && input.X is >= 0 and <= 65535 && input.Y is >= 0 and <= 65535,
        "key" => input.Value is >= 1 and <= 255,
        _ => false
    };
}
