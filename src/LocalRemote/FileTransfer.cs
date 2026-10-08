using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography;

namespace LocalRemote;

public sealed record FileRequest(Guid Id, string Operation, string Path = "", string Name = "", long Length = 0, bool Overwrite = false, int Page = 0);
public sealed record FileEntry(string Name, string Path, bool Directory, long Length = 0, DateTime Modified = default);
public sealed record FileReply(Guid Id, string State, string Path = "", long Length = 0, string Hash = "", string Error = "", FileEntry[]? Entries = null, bool HasMore = false);
public sealed record TransferProgress(long Completed, long Total);

public static class FileBlocks
{
    public const int BlockSize = 64 * 1024;
    public static byte[] Encode(Guid id, long offset, ReadOnlySpan<byte> data)
    {
        byte[] result = new byte[24 + data.Length]; id.TryWriteBytes(result); BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(16), offset); data.CopyTo(result.AsSpan(24)); return result;
    }
    public static (Guid Id, long Offset, byte[] Data) Decode(byte[] bytes)
    {
        if (bytes.Length is <= 24 or > BlockSize + 24) throw new InvalidDataException("Неверный блок файла.");
        long offset = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(16));
        if (offset < 0) throw new InvalidDataException("Неверная позиция в файле.");
        return (new(bytes.AsSpan(0, 16)), offset, bytes[24..]);
    }
    public static async Task LimitRateAsync(Stopwatch watch, long bytes, CancellationToken token)
    {
        // A bounded transfer rate leaves bandwidth for desktop video and input.
        int wait = (int)Math.Max(0, bytes * 1000d / (4 * 1024 * 1024) - watch.Elapsed.TotalMilliseconds);
        if (wait > 0) await Task.Delay(wait, token);
    }
}

public sealed class RemoteFileServer : IAsyncDisposable
{
    private readonly SslStream stream;
    private readonly SemaphoreSlim writeGate;
    private readonly CancellationToken token;
    private readonly string? allowedRoot;
    private readonly Dictionary<Guid, (Task Task, CancellationTokenSource Cancel)> downloads = [];
    private readonly object downloadGate = new();
    private Upload? upload;
    private sealed class Upload : IDisposable
    {
        public required Guid Id; public required string Target; public required string Partial; public required FileStream Stream; public required long Length; public bool Overwrite; public long Received;
        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public void Dispose() { Stream.Dispose(); Hash.Dispose(); if (File.Exists(Partial)) File.Delete(Partial); }
    }
    public RemoteFileServer(SslStream stream, SemaphoreSlim writeGate, CancellationToken token, string? allowedRoot = null)
    { this.stream = stream; this.writeGate = writeGate; this.token = token; this.allowedRoot = allowedRoot == null ? null : System.IO.Path.GetFullPath(allowedRoot).TrimEnd('\\') + "\\"; }
    private string ValidatePath(string path)
    {
        if (path.Length is < 3 or > 4096 || !System.IO.Path.IsPathFullyQualified(path) || path.StartsWith(@"\\")) throw new IOException("Выберите папку на локальном диске этого ПК.");
        string full = System.IO.Path.GetFullPath(path);
        if (allowedRoot != null && !full.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) && !full.Equals(allowedRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Папка вне разрешённого каталога.");
        return full;
    }
    private Task ReplyAsync(FileReply reply) => Wire.WriteAsync(stream, writeGate, PacketType.FileReply, Wire.Json(reply), token);
    public async Task HandleAsync(FileRequest request)
    {
        try
        {
            if (request.Id == Guid.Empty) throw new IOException("Неверный номер запроса.");
            switch (request.Operation)
            {
                case "roots":
                    FileEntry[] roots = allowedRoot != null ? [new(allowedRoot.TrimEnd('\\'), allowedRoot.TrimEnd('\\'), true)] : DriveInfo.GetDrives().Select(d => new FileEntry(d.Name, d.Name, true)).ToArray();
                    await ReplyAsync(new(request.Id, "list", Entries: roots)); break;
                case "list":
                    if (request.Page is < 0 or > 100000) throw new IOException("Неверная страница.");
                    string folder = ValidatePath(request.Path);
                    var paths = Directory.EnumerateFileSystemEntries(folder).OrderBy(p => Directory.Exists(p) ? 0 : 1).ThenBy(p => System.IO.Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase).Skip(request.Page * 100).Take(101).ToArray();
                    var entries = new List<FileEntry>();
                    foreach (string path in paths.Take(100))
                    {
                        try
                        {
                            bool directory = Directory.Exists(path);
                            var info = directory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
                            entries.Add(new(info.Name, info.FullName, directory, directory ? 0 : ((FileInfo)info).Length, info.LastWriteTime));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                    await ReplyAsync(new(request.Id, "list", folder, Entries: entries.ToArray(), HasMore: paths.Length > 100)); break;
                case "upload":
                    if (upload != null) throw new IOException("Передача файла уже выполняется.");
                    if (request.Length is < 0 or > 1024L * 1024 * 1024 * 1024) throw new IOException("Неверный размер файла.");
                    string targetFolder = ValidatePath(request.Path);
                    if (!Directory.Exists(targetFolder)) throw new DirectoryNotFoundException("Папка назначения не найдена.");
                    if (string.IsNullOrWhiteSpace(request.Name) || request.Name is "." or ".." || request.Name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || System.IO.Path.GetFileName(request.Name) != request.Name) throw new IOException("Неверное имя файла.");
                    string target = System.IO.Path.Combine(targetFolder, request.Name);
                    if (File.Exists(target) && !request.Overwrite) throw new IOException("Файл уже существует. Выберите другое имя или подтвердите замену.");
                    string partial = System.IO.Path.Combine(targetFolder, ".localremote-" + request.Id.ToString("N") + ".partial");
                    var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileBlocks.BlockSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    upload = new() { Id = request.Id, Target = target, Partial = partial, Stream = file, Length = request.Length, Overwrite = request.Overwrite };
                    await ReplyAsync(new(request.Id, "ready", target, request.Length)); break;
                case "finish":
                    if (upload == null || upload.Id != request.Id) throw new IOException("Передача не найдена.");
                    var finished = upload;
                    if (finished.Received != finished.Length) throw new IOException("Размер принятого файла не совпал.");
                    string hash = Convert.ToHexString(finished.Hash.GetHashAndReset());
                    await finished.Stream.FlushAsync(token); finished.Stream.Dispose();
                    File.Move(finished.Partial, finished.Target, finished.Overwrite);
                    upload = null; finished.Dispose();
                    await ReplyAsync(new(request.Id, "complete", finished.Target, finished.Received, hash)); break;
                case "download":
                    string source = ValidatePath(request.Path);
                    if (!File.Exists(source)) throw new FileNotFoundException("Файл не найден.");
                    lock (downloadGate) if (downloads.Count > 0) throw new IOException("Скачивание уже выполняется.");
                    var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var task = Task.Run(() => DownloadAsync(request.Id, source, cancel.Token), CancellationToken.None);
                    lock (downloadGate) downloads.Add(request.Id, (task, cancel));
                    _ = task.ContinueWith(_ => { lock (downloadGate) downloads.Remove(request.Id); cancel.Dispose(); }, TaskScheduler.Default);
                    break;
                case "cancel":
                    if (upload?.Id == request.Id) { upload.Dispose(); upload = null; }
                    lock (downloadGate) if (downloads.TryGetValue(request.Id, out var pending)) pending.Cancel.Cancel();
                    await ReplyAsync(new(request.Id, "cancelled")); break;
                default: throw new IOException("Неизвестная операция с файлом.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (upload?.Id == request.Id) { upload.Dispose(); upload = null; }
            await ReplyAsync(new(request.Id, "error", Error: ex.Message.Truncate(700)));
        }
    }
    public async Task ReceiveChunkAsync(byte[] bytes)
    {
        var block = FileBlocks.Decode(bytes);
        if (upload == null || upload.Id != block.Id) return;
        try
        {
            if (block.Offset != upload.Received || block.Data.Length > upload.Length - upload.Received) throw new IOException("Порядок или размер блоков файла не совпал.");
            await upload.Stream.WriteAsync(block.Data, token); upload.Hash.AppendData(block.Data); upload.Received += block.Data.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { upload.Dispose(); upload = null; await ReplyAsync(new(block.Id, "error", Error: ex.Message.Truncate(700))); }
    }
    private async Task DownloadAsync(Guid id, string path, CancellationToken cancellation)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileBlocks.BlockSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await ReplyAsync(new(id, "ready", path, file.Length));
            byte[] buffer = new byte[FileBlocks.BlockSize]; long offset = 0; var watch = Stopwatch.StartNew();
            int count;
            while ((count = await file.ReadAsync(buffer, cancellation)) > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                await Wire.WriteAsync(stream, writeGate, PacketType.FileChunk, FileBlocks.Encode(id, offset, buffer.AsSpan(0, count)), token);
                hash.AppendData(buffer.AsSpan(0, count)); offset += count; await FileBlocks.LimitRateAsync(watch, offset, cancellation);
            }
            await ReplyAsync(new(id, "complete", path, offset, Convert.ToHexString(hash.GetHashAndReset())));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!token.IsCancellationRequested) { try { await ReplyAsync(new(id, "error", Error: ex.Message.Truncate(700))); } catch { } }
    }
    public async ValueTask DisposeAsync()
    {
        upload?.Dispose(); upload = null;
        (Task Task, CancellationTokenSource Cancel)[] pending;
        lock (downloadGate) { pending = downloads.Values.ToArray(); foreach (var item in pending) item.Cancel.Cancel(); }
        foreach (var item in pending) { try { await item.Task; } catch { } }
    }
}

public sealed class FileTransferClient : IAsyncDisposable
{
    private readonly Func<PacketType, byte[], CancellationToken, Task> send;
    private readonly CancellationToken lifetime;
    private readonly Dictionary<Guid, TaskCompletionSource<FileReply>> queries = [];
    private readonly object gate = new();
    private readonly SemaphoreSlim transferGate = new(1);
    private Transfer? active;
    private sealed class Transfer : IDisposable
    {
        public required Guid Id; public FileStream? Output; public long Received; public long Total; public bool Stopped; public IProgress<TransferProgress>? Progress;
        public readonly SemaphoreSlim IoGate = new(1);
        public readonly IncrementalHash Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public readonly TaskCompletionSource<FileReply> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<FileReply> Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() { Output?.Dispose(); Hash.Dispose(); }
    }
    public FileTransferClient(Func<PacketType, byte[], CancellationToken, Task> send, CancellationToken lifetime) { this.send = send; this.lifetime = lifetime; }
    public async Task<FileReply> ListAsync(string path, int page, CancellationToken token)
    {
        var id = Guid.NewGuid(); var completion = new TaskCompletionSource<FileReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) queries.Add(id, completion);
        try
        {
            await send(PacketType.FileRequest, Wire.Json(new FileRequest(id, path.Length == 0 ? "roots" : "list", path, Page: page)), token);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        }
        finally { lock (gate) queries.Remove(id); }
    }
    public void HandleReply(FileReply reply)
    {
        lock (gate)
        {
            if (queries.TryGetValue(reply.Id, out var query))
            { if (reply.State == "error") query.TrySetException(new IOException(reply.Error)); else query.TrySetResult(reply); }
            if (active?.Id != reply.Id) return;
            if (reply.State == "ready") { active.Total = reply.Length; active.Ready.TrySetResult(reply); }
            else if (reply.State == "complete") active.Completed.TrySetResult(reply);
            else if (reply.State is "error" or "cancelled")
            {
                Exception error = reply.State == "cancelled" ? new OperationCanceledException("Передача отменена.") : new IOException(reply.Error);
                active.Ready.TrySetException(error); active.Completed.TrySetException(error);
            }
        }
    }
    public async Task ReceiveChunkAsync(byte[] bytes)
    {
        var block = FileBlocks.Decode(bytes); Transfer? transfer;
        lock (gate) transfer = active?.Id == block.Id ? active : null;
        if (transfer == null || transfer.Output == null) return;
        await transfer.IoGate.WaitAsync();
        try
        {
            if (transfer.Stopped) return;
            if (block.Offset != transfer.Received || block.Data.Length > transfer.Total - transfer.Received) throw new IOException("Повреждена передача файла.");
            await transfer.Output.WriteAsync(block.Data, lifetime); transfer.Hash.AppendData(block.Data); transfer.Received += block.Data.Length;
            transfer.Progress?.Report(new(transfer.Received, transfer.Total));
        }
        catch (Exception ex) { transfer.Completed.TrySetException(ex); }
        finally { transfer.IoGate.Release(); }
    }
    public async Task UploadAsync(string source, string remoteFolder, bool overwrite, IProgress<TransferProgress>? progress, CancellationToken cancellation)
    {
        using var token = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellation);
        await transferGate.WaitAsync(token.Token);
        var transfer = new Transfer { Id = Guid.NewGuid(), Progress = progress };
        lock (gate) active = transfer;
        try
        {
            await using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, FileBlocks.BlockSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await send(PacketType.FileRequest, Wire.Json(new FileRequest(transfer.Id, "upload", remoteFolder, System.IO.Path.GetFileName(source), file.Length, overwrite)), token.Token);
            await transfer.Ready.Task.WaitAsync(TimeSpan.FromSeconds(15), token.Token);
            byte[] buffer = new byte[FileBlocks.BlockSize]; long offset = 0; var watch = Stopwatch.StartNew(); int count;
            while ((count = await file.ReadAsync(buffer, token.Token)) > 0)
            {
                if (transfer.Completed.Task.IsFaulted) await transfer.Completed.Task;
                await send(PacketType.FileChunk, FileBlocks.Encode(transfer.Id, offset, buffer.AsSpan(0, count)), token.Token);
                transfer.Hash.AppendData(buffer.AsSpan(0, count)); offset += count; progress?.Report(new(offset, file.Length));
                await FileBlocks.LimitRateAsync(watch, offset, token.Token);
            }
            await send(PacketType.FileRequest, Wire.Json(new FileRequest(transfer.Id, "finish")), token.Token);
            var result = await transfer.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30), token.Token);
            if (result.Length != offset || result.Hash != Convert.ToHexString(transfer.Hash.GetHashAndReset())) throw new IOException("Контрольная сумма переданного файла не совпала.");
        }
        catch { await CancelAsync(transfer.Id); throw; }
        finally { lock (gate) active = null; transfer.Dispose(); transferGate.Release(); }
    }
    public async Task DownloadAsync(string remotePath, string target, bool overwrite, IProgress<TransferProgress>? progress, CancellationToken cancellation)
    {
        using var token = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellation);
        await transferGate.WaitAsync(token.Token);
        string partial = target + ".localremote-" + Guid.NewGuid().ToString("N") + ".partial";
        var transfer = new Transfer { Id = Guid.NewGuid(), Progress = progress };
        lock (gate) active = transfer;
        try
        {
            if (File.Exists(target) && !overwrite) throw new IOException("Файл уже существует.");
            transfer.Output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileBlocks.BlockSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await send(PacketType.FileRequest, Wire.Json(new FileRequest(transfer.Id, "download", remotePath)), token.Token);
            await transfer.Ready.Task.WaitAsync(TimeSpan.FromSeconds(15), token.Token);
            var result = await transfer.Completed.Task.WaitAsync(token.Token);
            await transfer.IoGate.WaitAsync(token.Token);
            try
            {
                if (result.Length != transfer.Received || result.Hash != Convert.ToHexString(transfer.Hash.GetHashAndReset())) throw new IOException("Контрольная сумма скачанного файла не совпала.");
                await transfer.Output.FlushAsync(token.Token); transfer.Output.Dispose(); transfer.Output = null;
                File.Move(partial, target, overwrite);
            }
            finally { transfer.IoGate.Release(); }
        }
        catch { await CancelAsync(transfer.Id); throw; }
        finally
        {
            lock (gate) active = null;
            await transfer.IoGate.WaitAsync();
            try { transfer.Stopped = true; transfer.Dispose(); if (File.Exists(partial)) File.Delete(partial); }
            finally { transfer.IoGate.Release(); }
            transferGate.Release();
        }
    }
    private async Task CancelAsync(Guid id)
    {
        if (lifetime.IsCancellationRequested) return;
        try { await send(PacketType.FileRequest, Wire.Json(new FileRequest(id, "cancel")), lifetime); } catch { }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            foreach (var query in queries.Values) query.TrySetCanceled(); queries.Clear();
            active?.Ready.TrySetCanceled(); active?.Completed.TrySetCanceled();
        }
        return ValueTask.CompletedTask;
    }
}
