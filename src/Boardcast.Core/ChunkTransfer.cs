using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Boardcast.Core;

public sealed record ChunkManifest
{
    public string Id { get; init; } = "";
    public int ChunkSize { get; init; } = ChunkTransfer.DefaultChunkSize;
    public List<ChunkFile> Files { get; init; } = [];
}

public sealed record ChunkFile
{
    public string RelativePath { get; init; } = "";
    public long Size { get; init; }
    public bool IsDirectory { get; init; }
    public List<string> Hashes { get; init; } = [];
}

public sealed record ChunkRequest(int FileIndex, int ChunkIndex);

/// <summary>Content-addressed transfer with independent chunk verification, shared by files and updates.</summary>
public static class ChunkTransfer
{
    public const int DefaultChunkSize = 1024 * 1024;
    public const int MaximumChunkSize = 8 * 1024 * 1024;
    public const int MaximumManifestBytes = 7 * 1024 * 1024 / 2;
    private const int MaximumEntries = 100000;

    public static async Task<ChunkManifest> BuildManifestAsync(TransferPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        PathSafety.ValidateManifest(plan.Offer, new AppSettings { MaxEntries = MaximumEntries, MaxTransferBytes = long.MaxValue });
        var files = plan.Offer.Entries.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .Select(entry => new ChunkFile { RelativePath = entry.RelativePath, Size = entry.Size, IsDirectory = entry.IsDirectory }).ToList();
        var chunkSize = DefaultChunkSize;
        while (EstimateManifestBytes(files, chunkSize) > MaximumManifestBytes)
        {
            if (chunkSize == MaximumChunkSize) throw new InvalidDataException("文件分块清单过大，请分批传输。");
            chunkSize *= 2;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.IsDirectory) continue;
                if (!plan.LocalFiles.TryGetValue(file.RelativePath, out var path)) throw new InvalidDataException("传输源文件缺失。");
                ValidateSourcePath(path);
                await using var stream = OpenRead(path);
                if (stream.Length != file.Size) throw new IOException("源文件大小已改变，请重新发起传输。");
                for (long remaining = file.Size; remaining > 0;)
                {
                    var length = (int)Math.Min(remaining, chunkSize);
                    await stream.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                    file.Hashes.Add(Hash(buffer.AsSpan(0, length)));
                    remaining -= length;
                }
                if (stream.Length != file.Size) throw new IOException("源文件大小已改变，请重新发起传输。");
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }

        var manifest = new ChunkManifest { ChunkSize = chunkSize, Files = files };
        manifest = manifest with { Id = ComputeId(manifest) };
        ValidateManifest(manifest, plan.Offer);
        return manifest;
    }

    public static void ValidateManifest(ChunkManifest manifest, TransferOffer? offer = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.ChunkSize is < DefaultChunkSize or > MaximumChunkSize || (manifest.ChunkSize & (manifest.ChunkSize - 1)) != 0)
            throw new InvalidDataException("分块大小无效。");
        if (!IsHash(manifest.Id) || manifest.Files is null || manifest.Files.Count is <= 0 or > MaximumEntries)
            throw new InvalidDataException("分块清单无效。");
        var paths = new Dictionary<string, ChunkFile>(StringComparer.OrdinalIgnoreCase);
        string? previousPath = null;
        long total = 0;
        long metadata = 128;
        foreach (var file in manifest.Files)
        {
            if (file is null || file.Size < 0 || (file.IsDirectory && file.Size != 0) || file.Hashes is null)
                throw new InvalidDataException("分块文件信息无效。");
            var path = PathSafety.NormalizeRelativePath(file.RelativePath);
            if (!string.Equals(path, file.RelativePath, StringComparison.Ordinal) ||
                (previousPath is not null && StringComparer.Ordinal.Compare(previousPath, path) >= 0) || !paths.TryAdd(path, file))
                throw new InvalidDataException("分块文件路径重复、未规范化或未排序。");
            previousPath = path;
            var count = ChunkCount(file.Size, manifest.ChunkSize);
            if (count != file.Hashes.Count || file.Hashes.Any(hash => !IsHash(hash)))
                throw new InvalidDataException("文件分块校验值无效。");
            try { total = checked(total + file.Size); }
            catch (OverflowException) { throw new InvalidDataException("分块文件总大小溢出。"); }
            metadata += JsonEncodedText.Encode(path).EncodedUtf8Bytes.Length + 128L + count * 67;
            if (metadata > MaximumManifestBytes + MaximumEntries * 128L)
                throw new InvalidDataException("分块清单超过协议限额。");
        }
        foreach (var path in paths.Keys)
        {
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                if (paths.TryGetValue(path[..slash], out var parent) && !parent.IsDirectory)
                    throw new InvalidDataException("文件不能包含子项目。");
        }
        if (offer is not null)
        {
            if (offer.Entries is null || offer.Entries.Count != paths.Count) throw new InvalidDataException("分块清单与传输请求不匹配。");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in offer.Entries)
            {
                if (entry is null || !seen.Add(entry.RelativePath) || !paths.TryGetValue(entry.RelativePath, out var file) ||
                    !string.Equals(file.RelativePath, entry.RelativePath, StringComparison.Ordinal) || file.Size != entry.Size || file.IsDirectory != entry.IsDirectory)
                    throw new InvalidDataException("分块清单与传输请求不匹配。");
            }
        }
        if (JsonSerializer.SerializeToUtf8Bytes(manifest, BoardcastJson.Options).Length > MaximumManifestBytes)
            throw new InvalidDataException("分块清单超过协议限额。");
        if (!string.Equals(manifest.Id, ComputeId(manifest), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("分块清单内容校验失败。");
    }

    public static async Task<byte[]> ReadChunkAsync(ChunkManifest manifest, ChunkRequest request,
        Func<string, string> pathResolver, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pathResolver);
        var (file, offset, length) = DescribeChunk(manifest, request);
        var path = pathResolver(file.RelativePath);
        ValidateSourcePath(path);
        await using var stream = OpenRead(path);
        if (stream.Length != file.Size) throw new IOException("源文件大小已改变，请重新发起传输。");
        stream.Position = offset;
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        if (stream.Length != file.Size || !Matches(data, file.Hashes[request.ChunkIndex]))
            throw new InvalidDataException("文件分块校验失败，源文件可能已改变。");
        return data;
    }

    /// <summary>Read-only validation of an already completed tree; never creates or resizes its files.</summary>
    public static async Task<bool> VerifyExistingAsync(ChunkManifest manifest, string root, CancellationToken cancellationToken = default)
    {
        ValidateManifest(manifest);
        var buffer = ArrayPool<byte>.Shared.Rent(manifest.ChunkSize);
        try
        {
            root = PathSafety.ValidateReceiveRoot(root);
            if (!Directory.Exists(root)) return false;
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = PathSafety.ResolveUnderRoot(root, file.RelativePath);
                if (file.IsDirectory)
                {
                    if (!Directory.Exists(path)) return false;
                    continue;
                }
                await using var stream = OpenRead(path);
                if (stream.Length != file.Size) return false;
                foreach (var expected in file.Hashes)
                {
                    var length = (int)Math.Min(manifest.ChunkSize, file.Size - stream.Position);
                    await stream.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                    if (!Matches(buffer.AsSpan(0, length), expected)) return false;
                }
                if (stream.Length != file.Size) return false;
            }
            return true;
        }
        catch (InvalidDataException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static async Task DownloadAsync(ChunkStore store, Func<ChunkRequest, CancellationToken, Task<byte[]>> fetch,
        Action<long>? progress = null, CancellationToken cancellationToken = default, int concurrency = 1,
        Action<ChunkRequest>? chunkCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fetch);
        if (concurrency is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(concurrency));
        var queue = new ConcurrentQueue<ChunkRequest>();
        for (var fileIndex = 0; fileIndex < store.Manifest.Files.Count; fileIndex++)
            for (var chunkIndex = 0; chunkIndex < store.Manifest.Files[fileIndex].Hashes.Count; chunkIndex++)
            {
                var request = new ChunkRequest(fileIndex, chunkIndex);
                if (!store.IsVerified(request)) queue.Enqueue(request);
            }
        progress?.Invoke(store.VerifiedBytes);
        using var workersCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var callbackGate = new object();
        async Task WorkerAsync()
        {
            try
            {
                while (queue.TryDequeue(out var request))
                {
                    workersCancellation.Token.ThrowIfCancellationRequested();
                    var data = await fetch(request, workersCancellation.Token).ConfigureAwait(false);
                    await store.WriteAsync(request, data, workersCancellation.Token).ConfigureAwait(false);
                    lock (callbackGate)
                    {
                        progress?.Invoke(store.VerifiedBytes);
                        chunkCompleted?.Invoke(request);
                    }
                }
            }
            catch
            {
                await workersCancellation.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => WorkerAsync())).ConfigureAwait(false);
        await store.CompleteAsync(cancellationToken).ConfigureAwait(false);
        progress?.Invoke(store.VerifiedBytes);
    }

    internal static (ChunkFile File, long Offset, int Length) DescribeChunk(ChunkManifest manifest, ChunkRequest request)
    {
        if (request is null || request.FileIndex < 0 || request.FileIndex >= manifest.Files.Count) throw new InvalidDataException("分块文件索引无效。");
        var file = manifest.Files[request.FileIndex];
        if (file.IsDirectory || request.ChunkIndex < 0 || request.ChunkIndex >= file.Hashes.Count)
            throw new InvalidDataException("文件分块索引无效。");
        var offset = (long)request.ChunkIndex * manifest.ChunkSize;
        return (file, offset, (int)Math.Min(manifest.ChunkSize, file.Size - offset));
    }

    internal static bool Matches(ReadOnlySpan<byte> data, string expected) => string.Equals(Hash(data), expected, StringComparison.OrdinalIgnoreCase);
    internal static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
        65536, FileOptions.Asynchronous | FileOptions.RandomAccess);

    private static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit);
    private static long ChunkCount(long size, int chunkSize) => size / chunkSize + (size % chunkSize == 0 ? 0 : 1);

    private static long EstimateManifestBytes(List<ChunkFile> files, int chunkSize)
    {
        // Escaped paths are counted exactly, with fixed-width SHA-256 entries added before expensive file hashing.
        long length = JsonSerializer.SerializeToUtf8Bytes(new ChunkManifest { Id = new string('0', 64), ChunkSize = chunkSize, Files = files }, BoardcastJson.Options).Length;
        foreach (var file in files)
        {
            var count = ChunkCount(file.Size, chunkSize);
            if (count > MaximumManifestBytes / 66) return long.MaxValue;
            if (count > 0) length += count * 67 - 1;
        }
        return length;
    }

    private static string ComputeId(ChunkManifest manifest)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string value)
        {
            // NUL cannot appear in validated paths, hexadecimal hashes, or decimal numbers.
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hash.AppendData([0]);
        }
        Append("boardcast-chunks-v1");
        Append(manifest.ChunkSize.ToString(CultureInfo.InvariantCulture));
        foreach (var file in manifest.Files)
        {
            Append(file.RelativePath);
            Append(file.Size.ToString(CultureInfo.InvariantCulture));
            Append(file.IsDirectory ? "directory" : "file");
            foreach (var chunkHash in file.Hashes) Append(chunkHash.ToLowerInvariant());
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void ValidateSourcePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        PathSafety.ValidateReceiveRoot(Path.GetDirectoryName(fullPath)!);
        if ((File.GetAttributes(fullPath) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("分块源必须是本机普通文件。");
    }
}

/// <summary>Verified state is reconstructed from cached bytes, never trusted from a sidecar.</summary>
public sealed class ChunkStore : IDisposable
{
    private string _root;
    private readonly ReceiveStorage? _receiveStorage;
    private readonly bool[][] _verified;
    private readonly SemaphoreSlim[] _fileGates;
    private readonly object _stateGate = new();
    private long _verifiedBytes;
    private int _stopped;
    private int _disposed;

    public ChunkManifest Manifest { get; }
    public string Root => Volatile.Read(ref _root);
    public long VerifiedBytes => Interlocked.Read(ref _verifiedBytes);

    private ChunkStore(ChunkManifest manifest, string root, ReceiveStorage? receiveStorage = null)
    {
        Manifest = manifest;
        _root = root;
        _receiveStorage = receiveStorage;
        _verified = manifest.Files.Select(file => new bool[file.Hashes.Count]).ToArray();
        _fileGates = manifest.Files.Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    }

    public static async Task<ChunkStore> OpenAsync(ChunkManifest manifest, string root, CancellationToken cancellationToken = default)
    {
        ChunkTransfer.ValidateManifest(manifest);
        // A caller changing its deserialized lists cannot change our verified-state mapping.
        var snapshot = manifest with { Files = manifest.Files.Select(file => file with { Hashes = [.. file.Hashes] }).ToList() };
        var store = new ChunkStore(snapshot, PathSafety.ValidateReceiveRoot(root));
        Directory.CreateDirectory(store.Root);
        foreach (var (file, index) in snapshot.Files.Select((file, index) => (file, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = store.GetFilePath(index);
            if (file.IsDirectory) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            path = store.GetFilePath(index);
            var existed = File.Exists(path);
            await using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.RandomAccess))
            {
                if (stream.Length > file.Size) throw new InvalidDataException("缓存文件大小异常，无法安全恢复。");
                if (stream.Length < file.Size) stream.SetLength(file.Size);
            }
            if (existed) await store.VerifyFileAsync(index, cancellationToken).ConfigureAwait(false);
        }
        return store;
    }

    /// <summary>Resume files from the internal cache and publish each verified file independently.</summary>
    public static async Task<ChunkStore> OpenReceiveAsync(ChunkManifest manifest, ReceiveStorage storage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ChunkTransfer.ValidateManifest(manifest);
        var snapshot = manifest with { Files = manifest.Files.Select(file => file with { Hashes = [.. file.Hashes] }).ToList() };
        var store = new ChunkStore(snapshot, PathSafety.ValidateReceiveRoot(storage.CacheRoot), storage);
        try
        {
            for (var index = 0; index < snapshot.Files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshot.Files[index].IsDirectory) continue;
                if (storage.IsFileCompleted(index))
                {
                    if (await store.VerifyFileAsync(index, cancellationToken).ConfigureAwait(false)) continue;
                    await storage.InvalidateCompletedAsync(index, cancellationToken).ConfigureAwait(false);
                }
                await storage.EnsureFileAsync(index, cancellationToken).ConfigureAwait(false);
                if (await store.VerifyFileAsync(index, cancellationToken).ConfigureAwait(false))
                    await store.CommitReceivedFileAsync(index, cancellationToken).ConfigureAwait(false);
            }
            return store;
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    public string GetFilePath(int fileIndex)
    {
        if (fileIndex < 0 || fileIndex >= Manifest.Files.Count) throw new ArgumentOutOfRangeException(nameof(fileIndex));
        return _receiveStorage is null
            ? PathSafety.ResolveUnderRoot(Root, Manifest.Files[fileIndex].RelativePath)
            : _receiveStorage.GetFilePath(fileIndex);
    }


    /// <summary>Commit the whole tree while preventing any reader or writer from retaining its old path.</summary>
    public async Task MoveToAsync(string destination, CancellationToken cancellationToken = default)
    {
        if (_receiveStorage is not null) throw new InvalidOperationException("接收文件已分别保存，不能移动整个接收缓存。");
        var acquired = 0;
        try
        {
            for (; acquired < _fileGates.Length; acquired++)
                await _fileGates[acquired].WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var validated = PathSafety.ValidateReceiveRoot(destination);
            var source = PathSafety.ValidateReceiveRoot(Root);
            if (string.Equals(Path.TrimEndingDirectorySeparator(source), Path.TrimEndingDirectorySeparator(validated),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
            Directory.Move(source, validated);
            Volatile.Write(ref _root, validated);
        }
        finally
        {
            for (var index = acquired - 1; index >= 0; index--) _fileGates[index].Release();
        }
    }

    public bool IsVerified(ChunkRequest request)
    {
        ChunkTransfer.DescribeChunk(Manifest, request);
        lock (_stateGate) return _verified[request.FileIndex][request.ChunkIndex];
    }

    public async Task WriteAsync(ChunkRequest request, byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var (file, offset, length) = ChunkTransfer.DescribeChunk(Manifest, request);
        if (data.Length != length || !ChunkTransfer.Matches(data, file.Hashes[request.ChunkIndex]))
            throw new InvalidDataException("收到的分块长度或校验值无效。");
        var gate = _fileGates[request.FileIndex];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            if (_receiveStorage is not null && _receiveStorage.IsFileCompleted(request.FileIndex))
            {
                if (IsVerified(request)) return;
                await _receiveStorage.InvalidateCompletedAsync(request.FileIndex, cancellationToken).ConfigureAwait(false);
                await _receiveStorage.EnsureFileAsync(request.FileIndex, cancellationToken).ConfigureAwait(false);
                await VerifyFileAsync(request.FileIndex, cancellationToken).ConfigureAwait(false);
            }
            SetVerified(request, false);
            await using (var stream = new FileStream(GetFilePath(request.FileIndex), FileMode.Open, FileAccess.Write, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.RandomAccess))
            {
                if (stream.Length != file.Size) throw new IOException("缓存文件大小已改变。");
                stream.Position = offset;
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            SetVerified(request, true);
            if (_receiveStorage is not null && IsFileVerified(request.FileIndex))
            {
                if (!await VerifyFileAsync(request.FileIndex, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException("文件内容已改变，可以继续恢复缺失分块。");
                await CommitReceivedFileAsync(request.FileIndex, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { gate.Release(); }
    }

    public async Task<byte[]> ReadAsync(ChunkRequest request, CancellationToken cancellationToken = default)
    {
        ChunkTransfer.DescribeChunk(Manifest, request);
        var gate = _fileGates[request.FileIndex];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfStopped();
            if (!IsVerified(request)) throw new InvalidDataException("该分块尚未通过校验。");
            try
            {
                return await ChunkTransfer.ReadChunkAsync(Manifest, request,
                    _ => GetFilePath(request.FileIndex), cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException) { SetVerified(request, false); throw; }
            catch (IOException) { SetVerified(request, false); throw; }
            catch (UnauthorizedAccessException) { SetVerified(request, false); throw; }
        }
        finally { gate.Release(); }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        var valid = true;
        for (var index = 0; index < Manifest.Files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gate = _fileGates[index];
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfStopped();
                var file = Manifest.Files[index];
                if (_receiveStorage is not null)
                {
                    if (file.IsDirectory) continue;
                    var fileValid = await VerifyFileAsync(index, cancellationToken).ConfigureAwait(false);
                    if (fileValid)
                    {
                        if (!_receiveStorage.IsFileCompleted(index))
                            await CommitReceivedFileAsync(index, cancellationToken).ConfigureAwait(false);
                    }
                    else if (_receiveStorage.IsFileCompleted(index))
                    {
                        await _receiveStorage.InvalidateCompletedAsync(index, cancellationToken).ConfigureAwait(false);
                        await _receiveStorage.EnsureFileAsync(index, cancellationToken).ConfigureAwait(false);
                        ClearFileVerification(index);
                    }
                    valid &= fileValid;
                }
                else if (file.IsDirectory) Directory.CreateDirectory(GetFilePath(index));
                else
                {
                    if (file.Size == 0)
                    {
                        var path = GetFilePath(index);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        using var empty = new FileStream(GetFilePath(index), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                        if (empty.Length != 0) throw new InvalidDataException("空文件缓存内容已改变。");
                    }
                    valid &= await VerifyFileAsync(index, cancellationToken).ConfigureAwait(false);
                }
            }
            finally { gate.Release(); }
        }
        if (!valid) throw new InvalidDataException("传输内容不完整或已改变，可以继续恢复缺失分块。");
    }

    private async Task CommitReceivedFileAsync(int fileIndex, CancellationToken cancellationToken)
    {
        var storage = _receiveStorage ?? throw new InvalidOperationException("该缓存不是文件接收存储。");
        var file = Manifest.Files[fileIndex];
        await storage.CommitFileAsync(fileIndex, cancellationToken, async (input, token) =>
        {
            if (input.Length != file.Size) throw new InvalidDataException("待保存文件大小已改变。");
            input.Position = 0;
            var buffer = ArrayPool<byte>.Shared.Rent(Manifest.ChunkSize);
            try
            {
                for (var chunkIndex = 0; chunkIndex < file.Hashes.Count; chunkIndex++)
                {
                    var (_, _, length) = ChunkTransfer.DescribeChunk(Manifest, new ChunkRequest(fileIndex, chunkIndex));
                    await input.ReadExactlyAsync(buffer.AsMemory(0, length), token).ConfigureAwait(false);
                    if (!ChunkTransfer.Matches(buffer.AsSpan(0, length), file.Hashes[chunkIndex]))
                        throw new InvalidDataException("待保存文件内容校验失败。");
                }
                if (input.Length != file.Size) throw new InvalidDataException("待保存文件大小已改变。");
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }).ConfigureAwait(false);
    }

    private async Task<bool> VerifyFileAsync(int fileIndex, CancellationToken cancellationToken)
    {
        var file = Manifest.Files[fileIndex];
        try
        {
            await using var stream = ChunkTransfer.OpenRead(GetFilePath(fileIndex));
            if (stream.Length != file.Size) throw new IOException("缓存文件大小已改变。");
            var buffer = ArrayPool<byte>.Shared.Rent(Manifest.ChunkSize);
            try
            {
                var valid = true;
                for (var chunkIndex = 0; chunkIndex < file.Hashes.Count; chunkIndex++)
                {
                    var request = new ChunkRequest(fileIndex, chunkIndex);
                    var (_, _, length) = ChunkTransfer.DescribeChunk(Manifest, request);
                    await stream.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                    var matches = ChunkTransfer.Matches(buffer.AsSpan(0, length), file.Hashes[chunkIndex]);
                    SetVerified(request, matches);
                    valid &= matches;
                }
                if (stream.Length != file.Size) throw new IOException("缓存文件大小已改变。");
                return valid;
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        catch (IOException)
        {
            ClearFileVerification(fileIndex);
            return false;
        }
        catch (UnauthorizedAccessException) when (_receiveStorage is not null)
        {
            ClearFileVerification(fileIndex);
            return false;
        }
    }

    private bool IsFileVerified(int fileIndex)
    {
        lock (_stateGate) return _verified[fileIndex].All(value => value);
    }

    private void ClearFileVerification(int fileIndex)
    {
        for (var index = 0; index < Manifest.Files[fileIndex].Hashes.Count; index++)
            SetVerified(new ChunkRequest(fileIndex, index), false);
    }

    /// <summary>Stop new operations and wait until all active file handles have closed before cache cleanup.</summary>
    public async Task StopAsync()
    {
        Interlocked.Exchange(ref _stopped, 1);
        var acquired = 0;
        try
        {
            for (; acquired < _fileGates.Length; acquired++)
                await _fileGates[acquired].WaitAsync().ConfigureAwait(false);
        }
        finally
        {
            for (var index = acquired - 1; index >= 0; index--) _fileGates[index].Release();
        }
    }

    private void ThrowIfStopped()
    {
        if (Volatile.Read(ref _stopped) != 0) throw new OperationCanceledException("该传输已停止。");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var gate in _fileGates) gate.Dispose();
    }
    private void SetVerified(ChunkRequest request, bool value)
    {
        var (_, _, length) = ChunkTransfer.DescribeChunk(Manifest, request);
        lock (_stateGate)
        {
            if (_verified[request.FileIndex][request.ChunkIndex] == value) return;
            _verified[request.FileIndex][request.ChunkIndex] = value;
            Interlocked.Add(ref _verifiedBytes, value ? length : -length);
        }
    }
}
