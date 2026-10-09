using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Boardcast.Core;

/// <summary>Owns only a receive task's temporary bytes and records where verified files were committed.</summary>
public sealed class ReceiveStorage
{
    private sealed record StoredFile
    {
        public string RelativePath { get; set; } = "";
        public string FinalPath { get; set; } = "";
        public long Size { get; set; }
        public bool IsDirectory { get; set; }
        public bool Completed { get; set; }
        public string TemporaryPath { get; set; } = "";
        public bool CommitPending { get; set; }
    }
    private sealed record Ledger
    {
        public string TransferId { get; set; } = "";
        public string SenderFingerprint { get; set; } = "";
        public string ContentId { get; set; } = "";
        public string Destination { get; set; } = "";
        public List<StoredFile> Files { get; set; } = [];
        public List<string> CreatedDirectories { get; set; } = [];
        public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
        public bool Completed { get; set; }
    }
    private static readonly object DestinationGate = new();
    private readonly object stateGate = new();
    private readonly Ledger ledger;
    private readonly Action<int>? completed;
    private bool cancelled;
    public string CacheRoot { get; }
    public string Destination => ledger.Destination;
    public string TransferId => ledger.TransferId;
    public string SenderFingerprint => ledger.SenderFingerprint;
    public DateTimeOffset SavedAt => ledger.SavedAt;
    public int CompletedFiles { get { lock (stateGate) return ledger.Files.Count(file => !file.IsDirectory && file.Completed); } }
    public long CompletedBytes { get { lock (stateGate) return ledger.Files.Where(file => !file.IsDirectory && file.Completed).Sum(file => file.Size); } }
    public long TotalBytes => ledger.Files.Sum(file => file.Size);
    public bool IsCompleted { get { lock (stateGate) return ledger.Completed; } }
    public bool IsCancelled { get { lock (stateGate) return cancelled; } }
    private string LedgerPath => Path.Combine(CacheRoot, "task.json");
    private string CancelPath => Path.Combine(CacheRoot, "cancelled");

    private ReceiveStorage(string root, Ledger ledger, Action<int>? completed)
    {
        CacheRoot = PathSafety.ValidateReceiveRoot(root);
        this.ledger = ledger;
        this.completed = completed;
        cancelled = File.Exists(CancelPath);
    }

    public static Task<ReceiveStorage> OpenAsync(string dataDirectory, string senderFingerprint, string transferId,
        string contentId, string destination, IReadOnlyList<TransferEntry> entries, Action<int>? completed = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        destination = PathSafety.ValidateReceiveRoot(destination);
        var tasks = TasksRoot(dataDirectory);
        Directory.CreateDirectory(tasks);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(senderFingerprint.ToLowerInvariant() + ":" + transferId + ":" + contentId + ":" + destination.ToUpperInvariant())));
        var root = PathSafety.ResolveUnderRoot(tasks, key);
        var existing = Load(root, completed);
        if (existing is not null)
        {
            if (existing.ledger.TransferId != transferId || existing.ledger.SenderFingerprint != senderFingerprint ||
                existing.ledger.ContentId != contentId || !string.Equals(existing.Destination, destination, StringComparison.OrdinalIgnoreCase) ||
                existing.ledger.Files.Count != entries.Count || existing.ledger.Files.Where((file, index) =>
                    file.RelativePath != entries[index].RelativePath || file.Size != entries[index].Size || file.IsDirectory != entries[index].IsDirectory).Any())
                throw new InvalidDataException("接收任务记录与请求不匹配。");
            if (existing.IsCancelled)
            {
                if (!existing.CleanupCancelled()) throw new IOException("上次取消的临时数据尚未清理，请稍后重试。");
                // Preserve committed paths when explicitly retrying a discarded transfer.
                existing.cancelled = false;
                CreatePrivateDirectory(root);
                existing.Save();
            }
            existing.ReconcileCommits();
            completed?.Invoke(existing.CompletedFiles);
            return Task.FromResult(existing);
        }
        CreatePrivateDirectory(root);
        var state = new Ledger { TransferId = transferId, SenderFingerprint = senderFingerprint, ContentId = contentId, Destination = destination,
            Files = entries.Select(entry => new StoredFile { RelativePath = PathSafety.NormalizeRelativePath(entry.RelativePath), FinalPath = PathSafety.NormalizeRelativePath(entry.RelativePath), Size = entry.Size, IsDirectory = entry.IsDirectory }).ToList() };
        var storage = new ReceiveStorage(root, state, completed);
        lock (DestinationGate)
        {
            Directory.CreateDirectory(destination);
            var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in state.Files)
            {
                var slash = file.RelativePath.IndexOf('/');
                var top = slash < 0 ? file.RelativePath : file.RelativePath[..slash];
                if (!mappings.TryGetValue(top, out var mapped))
                {
                    mapped = storage.UnusedRelativePath(top, slash >= 0 || file.IsDirectory, reserved);
                    mappings[top] = mapped;
                    reserved.Add(mapped);
                    if (slash >= 0 || file.IsDirectory) directories.Add(mapped);
                }
                file.FinalPath = mapped + (slash < 0 ? "" : file.RelativePath[slash..]);
            }
            storage.Save();
            foreach (var directory in directories) storage.EnsureDirectory(directory);
            storage.Save();
        }
        completed?.Invoke(0);
        return Task.FromResult(storage);
    }

    public bool IsFileCompleted(int index) { lock (stateGate) return ledger.Files[index].Completed; }
    public string GetFilePath(int index)
    {
        lock (stateGate)
        {
            var file = ledger.Files[index];
            return file.Completed ? PathSafety.ResolveUnderRoot(Destination, file.FinalPath) : GetStagingPath(index);
        }
    }
    public string GetStagingPath(int index) => PathSafety.ResolveUnderRoot(CacheRoot, "files/" + ledger.Files[index].RelativePath);

    public Task EnsureFileAsync(int index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (stateGate)
        {
            ThrowIfCancelled(cancellationToken);
            var file = ledger.Files[index];
            if (file.IsDirectory || file.Completed) return Task.CompletedTask;
            var path = GetStagingPath(index);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            path = GetStagingPath(index);
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            if (stream.Length > file.Size) throw new InvalidDataException("缓存文件大小异常，无法安全恢复。");
            if (stream.Length < file.Size) stream.SetLength(file.Size);
        }
        return Task.CompletedTask;
    }

    public Task InvalidateCompletedAsync(int index, CancellationToken cancellationToken = default)
    {
        lock (stateGate)
        {
            ThrowIfCancelled(cancellationToken);
            var file = ledger.Files[index];
            file.Completed = false;
            file.CommitPending = false;
            ledger.Completed = false;
            file.FinalPath = UnusedRelativePath(file.FinalPath, false);
            Save();
        }
        completed?.Invoke(CompletedFiles);
        return Task.CompletedTask;
    }

    public async Task CommitFileAsync(int index, CancellationToken cancellationToken = default,
        Func<Stream, CancellationToken, Task>? verify = null)
    {
        string temporary;
        string stage;
        lock (stateGate)
        {
            ThrowIfCancelled(cancellationToken);
            var file = ledger.Files[index];
            if (file.Completed || file.IsDirectory) return;
            if (file.TemporaryPath.Length > 0)
            {
                File.Delete(PathSafety.ResolveUnderRoot(Destination, file.TemporaryPath));
                file.TemporaryPath = "";
                file.CommitPending = false;
            }
            lock (DestinationGate)
            {
                file.FinalPath = UnusedRelativePath(file.FinalPath, false);
                var parent = Parent(file.FinalPath);
                if (parent.Length > 0) EnsureDirectory(parent);
                file.TemporaryPath = (parent.Length == 0 ? "" : parent + "/") + ".boardcast-" + Guid.NewGuid().ToString("N") + ".tmp";
                Save();
            }
            temporary = PathSafety.ResolveUnderRoot(Destination, file.TemporaryPath);
            stage = GetStagingPath(index);
        }
        // Copy into a task-owned sibling before the final move, so committing across volumes remains atomic.
        await using (var input = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = PrivateFile.Create(temporary))
        {
            if (input.Length != ledger.Files[index].Size) throw new InvalidDataException("缓存文件大小已改变。");
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(true);
        }
        await using var finalCopy = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (finalCopy.Length != ledger.Files[index].Size) throw new InvalidDataException("待保存文件大小已改变。");
        if (verify is not null) await verify(finalCopy, cancellationToken).ConfigureAwait(false);
        lock (stateGate)
        {
            ThrowIfCancelled(cancellationToken);
            var file = ledger.Files[index];
            lock (DestinationGate)
            {
                file.FinalPath = UnusedRelativePath(file.FinalPath, false);
                file.CommitPending = true;
                Save();
                File.Move(PathSafety.ResolveUnderRoot(Destination, file.TemporaryPath), PathSafety.ResolveUnderRoot(Destination, file.FinalPath), false);
                file.Completed = true;
                file.CommitPending = false;
                file.TemporaryPath = "";
                Save();
            }
            // A deletion failure is retryable; the ledger already records that the official file is complete.
            try { File.Delete(stage); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        completed?.Invoke(CompletedFiles);
    }

    public Task FinishAsync(CancellationToken cancellationToken = default)
    {
        lock (stateGate)
        {
            ThrowIfCancelled(cancellationToken);
            if (ledger.Files.Any(file => !file.IsDirectory && !file.Completed)) throw new InvalidDataException("仍有文件未完成保存。");
            foreach (var file in ledger.Files.Where(file => file.IsDirectory)) EnsureDirectory(file.FinalPath);
            ledger.Completed = true;
            Save();
            // Keep a small internal receipt for acknowledgement loss, with no manifest hashes or duplicate payloads.
            CleanupStaging();
        }
        return Task.CompletedTask;
    }

    public bool MarkCancelled()
    {
        lock (stateGate)
        {
            if (ledger.Completed) return false;
            if (!cancelled)
            {
                if (!Directory.Exists(CacheRoot)) CreatePrivateDirectory(CacheRoot);
                using var marker = PrivateFile.Create(CancelPath);
                marker.WriteByte(1);
                marker.Flush(true);
                cancelled = true;
            }
            return true;
        }
    }

    public bool CleanupCancelled()
    {
        try { return CleanupCancelledCore(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }
    private bool CleanupCancelledCore()
    {
        lock (stateGate)
        {
            if (!cancelled || ledger.Completed) return true;
            var clean = CleanupStaging();
            foreach (var file in ledger.Files)
            {
                if (file.Completed || file.TemporaryPath.Length == 0) continue;
                try { File.Delete(PathSafety.ResolveUnderRoot(Destination, file.TemporaryPath)); file.TemporaryPath = ""; file.CommitPending = false; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { clean = false; }
            }
            foreach (var relative in ledger.CreatedDirectories.OrderByDescending(path => path.Length))
            {
                try
                {
                    var path = PathSafety.ResolveUnderRoot(Destination, relative);
                    if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path, false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { clean = false; }
            }
            if (Directory.Exists(CacheRoot))
            {
                Save();
                if (clean)
                {
                    // Retain the internal record only when it describes completed files that must survive a retry.
                    if (CompletedFiles > 0) { File.Delete(CancelPath); cancelled = false; }
                    else
                    {
                        try { DeleteOwnedTree(CacheRoot); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { clean = false; }
                    }
                }
            }
            return clean;
        }
    }

    public static IReadOnlyList<ReceiveStorage> FindStored(string dataDirectory, string? transferId = null, ISet<string>? excludedRoots = null)
    {
        var root = TasksRoot(dataDirectory);
        if (!Directory.Exists(root)) return [];
        var result = new List<ReceiveStorage>();
        foreach (var path in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(path);
            if (name.Length != 64 || !name.All(Uri.IsHexDigit) || excludedRoots?.Contains(path) == true) continue;
            var storage = Load(path, null);
            if (storage is not null && (transferId is null || storage.TransferId == transferId))
            {
                try { storage.ReconcileCommits(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
                result.Add(storage);
            }
        }
        return result;
    }

    private static ReceiveStorage? Load(string root, Action<int>? completed)
    {
        try
        {
            PathSafety.ValidateReceiveRoot(root);
            var path = Path.Combine(root, "task.json");
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 24 * 1024 * 1024) throw new InvalidDataException("接收记录过大。");
            var state = JsonSerializer.Deserialize<Ledger>(File.ReadAllBytes(path), BoardcastJson.Options) ?? throw new InvalidDataException("接收记录无效。");
            if (!Guid.TryParse(state.TransferId, out _) || state.Files is null || state.Files.Count is <= 0 or > 100000 || state.CreatedDirectories is null)
                throw new InvalidDataException("接收记录无效。");
            state.Destination = PathSafety.ValidateReceiveRoot(state.Destination);
            foreach (var file in state.Files)
            {
                PathSafety.NormalizeRelativePath(file.RelativePath);
                PathSafety.ResolveUnderRoot(state.Destination, file.FinalPath);
                if (file.TemporaryPath.Length > 0 && (!Path.GetFileName(file.TemporaryPath).StartsWith(".boardcast-", StringComparison.Ordinal) || !file.TemporaryPath.EndsWith(".tmp", StringComparison.Ordinal)))
                    throw new InvalidDataException("临时接收路径无效。");
                if (file.TemporaryPath.Length > 0) PathSafety.ResolveUnderRoot(state.Destination, file.TemporaryPath);
            }
            foreach (var directory in state.CreatedDirectories) PathSafety.ResolveUnderRoot(state.Destination, directory);
            return new ReceiveStorage(root, state, completed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException) { return null; }
    }

    public bool RetryCleanup()
    {
        lock (stateGate)
        {
            if (cancelled) return CleanupCancelled();
            var clean = true;
            for (var index = 0; index < ledger.Files.Count; index++)
            {
                if (!ledger.Files[index].Completed) continue;
                try { File.Delete(GetStagingPath(index)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { clean = false; }
            }
            if (ledger.Completed) clean &= CleanupStaging();
            return clean;
        }
    }
    public static void PruneStored(string dataDirectory, ISet<string>? active = null)
    {
        var stored = FindStored(dataDirectory, excludedRoots: active).ToList();
        var old = stored.Where(storage => storage.SavedAt < DateTimeOffset.UtcNow.AddDays(-7)).ToHashSet();
        foreach (var storage in stored.Where(storage => storage.IsCompleted).OrderByDescending(storage => storage.SavedAt).Skip(128)) old.Add(storage);
        foreach (var storage in old)
        {
            try
            {
                if (!storage.IsCompleted) storage.MarkCancelled();
                if (storage.RetryCleanup() && Directory.Exists(storage.CacheRoot)) DeleteOwnedTree(storage.CacheRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
    }
    private void ReconcileCommits()
    {
        lock (stateGate)
        {
            var changed = false;
            foreach (var file in ledger.Files.Where(file => file.CommitPending))
            {
                if (file.TemporaryPath.Length > 0 && !File.Exists(PathSafety.ResolveUnderRoot(Destination, file.TemporaryPath)) &&
                    File.Exists(PathSafety.ResolveUnderRoot(Destination, file.FinalPath)))
                { file.Completed = true; file.CommitPending = false; file.TemporaryPath = ""; changed = true; }
            }
            if (changed) Save();
        }
    }
    private string UnusedRelativePath(string requested, bool directory, ISet<string>? reserved = null)
    {
        var requestedPath = PathSafety.ResolveUnderRoot(Destination, requested);
        if (!File.Exists(requestedPath) && !Directory.Exists(requestedPath) && reserved?.Contains(requested) != true) return requested;
        var parent = Parent(requested);
        var name = Path.GetFileName(requested);
        var extension = directory ? "" : Path.GetExtension(name);
        var stem = extension.Length == 0 ? name : name[..^extension.Length];
        for (var number = 1; number < 1000000; number++)
        {
            var renamed = stem + " (" + number + ")" + extension;
            var relative = (parent.Length == 0 ? "" : parent + "/") + renamed;
            var path = PathSafety.ResolveUnderRoot(Destination, relative);
            if (!File.Exists(path) && !Directory.Exists(path) && reserved?.Contains(relative) != true) return relative;
        }
        throw new IOException("同名项目过多，无法选择保存名称。");
    }
    private void EnsureDirectory(string relative)
    {
        var current = "";
        foreach (var segment in PathSafety.NormalizeRelativePath(relative).Split('/'))
        {
            current = current.Length == 0 ? segment : current + "/" + segment;
            var path = PathSafety.ResolveUnderRoot(Destination, current);
            if (!Directory.Exists(path))
            {
                ledger.CreatedDirectories.Add(current);
                Save();
                Directory.CreateDirectory(path);
            }
        }
    }
    private bool CleanupStaging()
    {
        try
        {
            var path = PathSafety.ResolveUnderRoot(CacheRoot, "files");
            if (Directory.Exists(path)) DeleteOwnedTree(path);
            foreach (var temporary in Directory.EnumerateFiles(CacheRoot, "task.*.tmp")) File.Delete(temporary);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }
    private void Save()
    {
        ledger.SavedAt = DateTimeOffset.UtcNow;
        var temporary = Path.Combine(CacheRoot, "task." + Guid.NewGuid().ToString("N") + ".tmp");
        using (var file = PrivateFile.Create(temporary))
        {
            JsonSerializer.Serialize(file, ledger, BoardcastJson.Options);
            file.Flush(true);
        }
        File.Move(temporary, LedgerPath, true);
    }
    private void ThrowIfCancelled(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (cancelled) throw new OperationCanceledException("接收任务已取消。", ct);
    }
    private static string TasksRoot(string dataDirectory) => PathSafety.ResolveUnderRoot(PathSafety.ValidateReceiveRoot(dataDirectory), "receive-tasks");
    private static string Parent(string relative) { var slash = relative.LastIndexOf('/'); return slash < 0 ? "" : relative[..slash]; }
    private static void DeleteOwnedTree(string path)
    {
        PathSafety.ValidateReceiveRoot(path);
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("临时目录包含链接，无法安全清理。");
            if ((attributes & FileAttributes.Directory) != 0) DeleteOwnedTree(child);
            else File.Delete(child);
        }
        Directory.Delete(path, false);
    }
    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("No current user identity.");
            security.SetOwner(user);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(security);
        }
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
